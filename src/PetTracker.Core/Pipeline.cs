using System.Threading.Channels;
using PetTracker.Core.Capture;
using PetTracker.Core.Diagnostics;
using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Core;

public sealed record PipelineOptions
{
    public string ProcessName { get; init; } = "AION2";
    public string? AdapterOverride { get; init; }
    public FrameDecoderOptions Decoder { get; init; } = new();
    /// <summary>Also pick game flows by the heartbeat when the TCP table has not (yet) listed them.</summary>
    public bool HeartbeatFallback { get; init; } = true;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Extra remote/local ports to leave out of the capture (443 and 80 are always excluded).</summary>
    public IReadOnlyList<int> ExcludedPorts { get; init; } = [];
    /// <summary>How long / how many bytes an unclassified flow is held before it is dropped.</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromSeconds(3);
    public int HoldBytes { get; init; } = 256 * 1024;
    /// <summary>Offline import only: treat flows whose server side uses one of these ports as the game's.</summary>
    public IReadOnlyList<int> ForcedServerPorts { get; init; } = [];
}

public sealed record PipelineStatus(
    string Phase,
    IReadOnlyList<GameConnection> Connections,
    string Capture,
    long Packets,
    long BytesS2C,
    long Frames,
    long Desyncs,
    long Gaps,
    long Heartbeats,
    int? ActiveFlowId,
    string? Error,
    long Bundles = 0,
    long InnerFrames = 0,
    long FlowsRejected = 0,
    RelayInfo Relay = default);

/// <summary>Capture, reassembly and framing on one background thread, fed through a channel.</summary>
public sealed class Pipeline : IDisposable
{
    private readonly record struct Input(TcpSegment? Segment, IReadOnlyList<GameConnection>? Connections, bool Tick, A2CapRecord? Record = null);

    private readonly PipelineOptions _options;
    private readonly ConnectionLocator _locator;
    private readonly NpcapSource _source;
    private readonly FlowTracker _flows;
    private readonly StreamProcessor _processor;
    private readonly Channel<Input> _channel = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, string> _flowNames = [];
    private Task? _worker;
    private Task? _ticker;
    private bool _disposed;

    private IReadOnlyList<GameConnection> _connections = [];
    private long _bytes;
    private string? _error;

    public Pipeline(PipelineOptions? options = null)
    {
        _options = options ?? new PipelineOptions();
        _flows = new FlowTracker(_options);
        _source = new NpcapSource(_options.AdapterOverride, _options.ExcludedPorts);
        _locator = new ConnectionLocator(_options.ProcessName);
        _processor = new StreamProcessor(_options.Decoder);
    }

    /// <summary>Every reassembled chunk and connection event, in order. Raised on the pipeline thread.</summary>
    public event Action<A2CapRecord>? RecordProduced;

    /// <summary>Frames decoded from the stream. Raised on the pipeline thread.</summary>
    public event Action<DecodedFrames>? FramesDecoded;

    public PipelineStatus Status
    {
        get
        {
            var s = _processor.Stats;
            var phase = _error is not null ? "Error"
                : s.Heartbeats > 0 ? "Locked (heartbeat seen)"
                : _connections.Count > 0 ? "Connected, waiting for heartbeat"
                : _options.HeartbeatFallback ? "Waiting for AION2 (also watching non-web TCP for the heartbeat)"
                : "Waiting for AION2";
            return new PipelineStatus(phase, _connections, _source.Description, _source.PacketsSeen, Interlocked.Read(ref _bytes),
                s.Frames, s.Desyncs, _flows.Gaps, s.Heartbeats, _processor.ActiveFlowId, _error,
                s.Bundles, s.InnerFrames, s.FlowsRejected, _processor.Relay);
        }
    }

    /// <summary>Starts capturing. Throws <see cref="NpcapNotAvailableException"/> if Npcap is missing.</summary>
    public void Start()
    {
        DiagLog.Info("Pipeline", $"starting; process '{_options.ProcessName}', heartbeat fallback {_options.HeartbeatFallback}, extra excluded ports [{string.Join(",", _options.ExcludedPorts)}]");
        NpcapSource.EnsureAvailable();
        _source.SegmentCaptured += s => _channel.Writer.TryWrite(new Input(s, null, false));
        _locator.Changed += c => _channel.Writer.TryWrite(new Input(null, c, false));
        _source.Start(); // stable filter, opened once: no race with the TCP-table poll
        _worker = Task.Run(() => RunAsync(_cts.Token));
        _ticker = Task.Run(() => TickAsync(_cts.Token));
        _locator.Start(_options.PollInterval);
        DiagLog.Info("Pipeline", $"started: {_source.Description}");
    }

    private async Task TickAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                _channel.Writer.TryWrite(new Input(null, null, true));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var input in _channel.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (input.Record is { } record) Emit(record);
                    else if (input.Segment is { } segment) Handle(segment);
                    else if (input.Connections is { } connections) OnConnections(connections);
                    else OnTick();
                }
                catch (Exception ex) when (ex is NpcapNotAvailableException or InvalidOperationException)
                {
                    _error = ex.Message;
                    DiagLog.Error("Pipeline", "error while processing capture input", ex);
                    Emit(new ConnectionEvent(DateTime.UtcNow.Ticks, ConnectionEventKind.Info, 0, "error: " + ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Anything else would end the worker silently and leave a tracker that looks alive but decodes nothing.
            _error = ex.Message;
            DiagLog.Error("Pipeline", "worker stopped by an unexpected exception; capture is no longer decoding", ex);
        }
    }

    private void OnConnections(IReadOnlyList<GameConnection> connections)
    {
        _connections = connections;
        _error = null;
        Emit(new ConnectionEvent(DateTime.UtcNow.Ticks, ConnectionEventKind.ConnectionsChanged, 0,
            connections.Count == 0 ? "no AION2 connections" : string.Join("; ", connections.Select(c => c.ToString()))));

        // Flows already held (seen before this poll) are released and decoded from their first byte.
        foreach (var record in _flows.SetKnown(connections.Select(c => c.Remote), DateTime.UtcNow.Ticks))
            Emit(record);
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        _flows.Expire(now.Ticks);
        if (now - _lastStatsLog >= StatsInterval)
        {
            _lastStatsLog = now;
            LogStats();
        }
    }

    /// <summary>Counters and flow verdicts, written only when something changed since the last line.</summary>
    private void LogStats()
    {
        var s = Status;
        var line = $"phase '{s.Phase}'; packets {s.Packets}, s2c bytes {s.BytesS2C}, frames {s.Frames} (inner {s.InnerFrames}, bundles {s.Bundles}), " +
                   $"heartbeats {s.Heartbeats}, desyncs {s.Desyncs}, gaps {s.Gaps}, flows rejected {s.FlowsRejected}, active flow {s.ActiveFlowId?.ToString() ?? "none"}, connections {s.Connections.Count}";
        if (line != _lastStatsLine)
        {
            _lastStatsLine = line;
            DiagLog.Info("Stats", line);
        }

        foreach (var flow in _processor.Flows)
        {
            var summary = $"flow {flow.FlowId}: {(flow.Rejected ? "rejected" : flow.Class.ToString())}, syn seen {flow.SawSyn}, {flow.Bytes} bytes, " +
                          $"{flow.Frames} frames, {flow.Heartbeats} heartbeats, {flow.Desyncs} desyncs" + (flow.Reason is null ? "" : $"; {flow.Reason}");
            if (!_flowLines.TryGetValue(flow.FlowId, out var previous) || previous != summary)
            {
                _flowLines[flow.FlowId] = summary;
                DiagLog.Info("Flow", summary);
            }
        }
        if (s.Relay.Detected && s.Relay.Describe() is var relay && relay != _lastRelayLine)
        {
            _lastRelayLine = relay;
            DiagLog.Info("Network", relay);
        }
        if (Opcodes.TakeReport(OpcodeName) is { } opcodes)
            DiagLog.Info("Opcodes", opcodes);
    }

    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastStatsLog = DateTime.UtcNow;
    private string? _lastStatsLine, _lastRelayLine;
    private readonly Dictionary<int, string> _flowLines = [];

    /// <summary>Frame counts per opcode seen on the game stream (numbers only); the app adds names for known opcodes.</summary>
    public OpcodeHistogram Opcodes { get; } = new();
    public Func<ushort, string?> OpcodeName { get; set; } = _ => null;

    private void Handle(TcpSegment segment)
    {
        foreach (var record in _flows.Offer(segment))
            Emit(record);
    }

    private void Emit(A2CapRecord record)
    {
        switch (record)
        {
            case S2CChunk chunk:
                Interlocked.Add(ref _bytes, chunk.Data.Length);
                break;
            case ConnectionEvent { Kind: ConnectionEventKind.FlowOpened or ConnectionEventKind.FlowJoined } e:
                _flowNames[e.FlowId] = e.Detail;
                DiagLog.Info("Flow", $"{e.Kind} flow {e.FlowId}: {e.Detail}");
                break;
            case ConnectionEvent { Kind: ConnectionEventKind.FlowClosed or ConnectionEventKind.HeartbeatLocked or ConnectionEventKind.Info } e:
                DiagLog.Info("Flow", $"{e.Kind} flow {e.FlowId}: {e.Detail}");
                break;
        }

        RecordProduced?.Invoke(record);
        if (_processor.Process(record) is { } frames)
        {
            foreach (var frame in frames.Frames)
                Opcodes.Add(frame.Opcode);
            FramesDecoded?.Invoke(frames);
        }
    }

    public string? FlowName(int flowId) => _flowNames.GetValueOrDefault(flowId);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DiagLog.Info("Pipeline", "stopping");
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _locator.Dispose();
        _source.Dispose();
        try { Task.WaitAll([_worker ?? Task.CompletedTask, _ticker ?? Task.CompletedTask], TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
