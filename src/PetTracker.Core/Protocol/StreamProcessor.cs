using PetTracker.Core.Recording;

namespace PetTracker.Core.Protocol;

public readonly record struct DecodedFrames(long Ticks, int FlowId, IReadOnlyList<Frame> Frames);

public enum FlowClass { Unknown, Game, Tls }

/// <summary>One flow as the processor saw it. Endpoints is the raw detail of the FlowOpened/FlowJoined event ("src -> dst").</summary>
public sealed record FlowSummary(
    int FlowId, string? Endpoints, bool SawSyn, long Bytes, FlowClass Class, bool Rejected,
    long Frames, long Heartbeats, long Desyncs, string? Reason, bool RelayLike = false);

/// <summary>
/// Parallel connections that carry only the game's heartbeat plus bytes that do not parse, and are not the game's own
/// connection: typical of a ping reducer or VPN that opens tunnel connections of its own. Identical says their byte counts
/// match (one stream sent over several routes); Healthy says another flow decodes real game frames, so these are ignored.
/// </summary>
public readonly record struct RelayInfo(int Flows, bool Identical, bool Healthy)
{
    public bool Detected => Flows >= 2;

    public string Describe() => Detected
        ? $"{Flows} parallel heartbeat-only flows{(Identical ? " with matching byte counts" : "")}; " +
          (Healthy ? "ignored, the game data decodes on another flow" : "no other flow is decoding game data yet") +
          "; a ping reducer or VPN is likely active"
        : "none";
}

/// <summary>
/// Turns the .a2cap record stream (live or replayed) into frames: one <see cref="FrameDecoder"/> per flow,
/// realigned on flow and gap events. Each flow starts "pending" and is promoted to game once it yields a heartbeat or two
/// chained frames; flows that look like TLS, or show no valid frame within the probe budget, are rejected quietly
/// (counted in <see cref="DecoderStats.FlowsRejected"/>, never as desyncs).
/// </summary>
public sealed class StreamProcessor(FrameDecoderOptions? options = null, int probeBytes = StreamProcessor.DefaultProbeBytes)
{
    public const int DefaultProbeBytes = 4096;
    /// <summary>Flows that joined mid-stream scan for a heartbeat first, so they get a larger budget.</summary>
    public const int ScanProbeBytes = 256 * 1024;
    /// <summary>Heartbeats a flow must show before it can be called relay-like, and frames beyond heartbeats before it is called healthy.</summary>
    public const int RelayMinHeartbeats = 20;
    public const int HealthyMinFrames = 20;
    private const string StartRejection = "no valid frame at the start of the stream";

    private sealed class FlowState
    {
        public string? Endpoints;
        public bool SawSyn;
        public bool Strict;
        public long Bytes;
        public long Budget;
        public FlowClass Class;
        public bool Rejected;
        public bool Confirmed;
        public bool Relay;
        public string? Reason;
        public FrameDecoder? Decoder;
        public DecoderStats Stats = new();
        public readonly byte[] Head = new byte[3];
        public int HeadLength;
    }

    private readonly FrameDecoderOptions _options = options ?? new FrameDecoderOptions();
    private readonly SortedDictionary<int, FlowState> _flows = [];
    private readonly DecoderStats _retired = new();
    private long _rejected;

    public BundleFormat LastBundleFormat { get; private set; }
    public int? ActiveFlowId { get; private set; }

    public DecoderStats Stats
    {
        get
        {
            var total = new DecoderStats();
            total.Add(_retired);
            foreach (var f in _flows.Values)
                total.Add(f.Stats);
            total.FlowsRejected = _rejected;
            return total;
        }
    }

    public IReadOnlyList<FlowSummary> Flows => _flows.Select(kv =>
    {
        var f = kv.Value;
        return new FlowSummary(kv.Key, f.Endpoints, f.SawSyn, f.Bytes, f.Class, f.Rejected,
            f.Stats.Frames, f.Stats.Heartbeats, f.Stats.Desyncs, f.Reason, f.Relay);
    }).ToList();

    /// <summary>The relay-like flows seen so far (kept after they are dropped, so the report can still describe them).</summary>
    public RelayInfo Relay
    {
        get
        {
            var bytes = _flows.Values.Where(f => f.Relay || f.Reason == StartRejection).Select(f => f.Bytes).ToList();
            if (bytes.Count < 2 || !_flows.Values.Any(f => f.Relay) && !_flows.Values.Any(IsHealthy))
                return default;
            var spread = bytes.Max() - bytes.Min();
            return new RelayInfo(bytes.Count, spread <= Math.Max(256, bytes.Max() / 100), _flows.Values.Any(IsHealthy));
        }
    }

    /// <summary>TLS record header: content type 0x14-0x17, major version 3, minor version 1-4.</summary>
    public static bool LooksLikeTls(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] is >= 0x14 and <= 0x17 && head[1] == 0x03 && head[2] is >= 0x01 and <= 0x04;

    public DecodedFrames? Process(A2CapRecord record)
    {
        switch (record)
        {
            case ConnectionEvent { Kind: ConnectionEventKind.FlowOpened } e:
                Replace(e, syn: true);
                break;
            case ConnectionEvent { Kind: ConnectionEventKind.FlowJoined } e:
                Replace(e, syn: false);
                break;
            case ConnectionEvent { Kind: ConnectionEventKind.Gap } e:
                if (FlowFor(e.FlowId) is { Decoder: { } gapped } gapFlow)
                {
                    gapFlow.Strict = false;
                    gapFlow.Budget = Math.Max(gapFlow.Budget, gapFlow.Bytes + ScanProbeBytes);
                    gapped.Resync();
                }
                break;
            case ConnectionEvent { Kind: ConnectionEventKind.FlowClosed } e:
                if (_flows.TryGetValue(e.FlowId, out var closed))
                    closed.Decoder = null;
                break;
            case S2CChunk chunk:
                return Feed(chunk);
        }
        return null;
    }

    private DecodedFrames? Feed(S2CChunk chunk)
    {
        var flow = FlowFor(chunk.FlowId);
        flow.Bytes += chunk.Data.Length;
        if (flow.Rejected || flow.Decoder is null)
            return null;

        if (flow.HeadLength < flow.Head.Length)
        {
            var take = Math.Min(flow.Head.Length - flow.HeadLength, chunk.Data.Length);
            chunk.Data.AsSpan(0, take).CopyTo(flow.Head.AsSpan(flow.HeadLength));
            flow.HeadLength += take;
            if (flow.HeadLength == flow.Head.Length && !flow.Confirmed && LooksLikeTls(flow.Head))
            {
                Reject(flow, FlowClass.Tls, "TLS record header");
                return null;
            }
        }

        var decoder = flow.Decoder;
        var frames = decoder.Feed(chunk.Data);
        if (decoder.Bundles.LastFormat != BundleFormat.None)
            LastBundleFormat = decoder.Bundles.LastFormat;

        if (!flow.Confirmed)
        {
            if (flow.Stats.Heartbeats > 0 || flow.Stats.Frames >= 2)
            {
                flow.Confirmed = true;
                flow.Class = FlowClass.Game;
            }
            else if (flow.Stats.Desyncs > 0)
            {
                if (flow.Strict)
                {
                    Reject(flow, FlowClass.Unknown, StartRejection);
                    return null;
                }
                // Joined mid-stream: the decoder is already scanning for a heartbeat; that is not a desync.
                flow.Stats.Desyncs = 0;
                flow.Budget = Math.Max(flow.Budget, flow.Bytes + ScanProbeBytes);
            }
            else if (flow.Bytes >= flow.Budget)
            {
                Reject(flow, FlowClass.Unknown, $"no valid frame or heartbeat in the first {flow.Budget} bytes");
                return null;
            }
        }

        IgnoreRelayFlows();
        if (frames.Count > 0 && !flow.Rejected)
        {
            ActiveFlowId = chunk.FlowId;
            return new DecodedFrames(chunk.Ticks, chunk.FlowId, frames);
        }
        return null;
    }

    private static bool IsHealthy(FlowState f) =>
        !f.Rejected && f.Confirmed && f.Stats.Frames - f.Stats.Heartbeats >= HealthyMinFrames && f.Stats.Desyncs <= f.Stats.Frames / 20;

    /// <summary>Only heartbeats decode, and nearly every heartbeat is followed by bytes that do not parse.</summary>
    private static bool IsRelayLike(FlowState f) =>
        !f.Rejected && f.Confirmed && f.Stats.Frames == f.Stats.Heartbeats && f.Stats.Heartbeats >= RelayMinHeartbeats
        && f.Stats.Desyncs >= f.Stats.Heartbeats - 1;

    /// <summary>
    /// Once a flow decodes real game frames, flows that are relay-like are dropped so their endless desyncs do not hide a
    /// working tracker. Before any flow is healthy nothing is dropped: a real framing change looks the same on a single flow.
    /// </summary>
    private void IgnoreRelayFlows()
    {
        if (!_flows.Values.Any(IsHealthy))
            return;
        foreach (var f in _flows.Values)
        {
            if (!IsRelayLike(f))
                continue;
            Reject(f, FlowClass.Unknown, "heartbeat-only stream that does not parse (relay-like); another flow carries the game data");
            f.Relay = true;
        }
    }

    private void Reject(FlowState flow, FlowClass kind, string reason)
    {
        flow.Class = kind;
        flow.Rejected = true;
        flow.Reason = reason;
        flow.Decoder = null;
        flow.Stats = new DecoderStats(); // nothing decoded from a rejected flow counts
        _rejected++;
    }

    private void Replace(ConnectionEvent e, bool syn)
    {
        if (_flows.Remove(e.FlowId, out var old))
            _retired.Add(old.Stats);

        // Both kinds start aligned: the capture layer hands over a flow's bytes from its first byte. A flow that joined
        // mid-stream fails that probe and falls back to scanning for the heartbeat.
        var decoder = new FrameDecoder(_options);
        _flows[e.FlowId] = new FlowState
        {
            Endpoints = e.Detail,
            SawSyn = syn,
            Strict = syn,
            Budget = syn ? probeBytes : ScanProbeBytes,
            Decoder = decoder,
            Stats = decoder.Stats,
        };
    }

    private FlowState FlowFor(int flowId)
    {
        if (!_flows.TryGetValue(flowId, out var flow))
        {
            var decoder = new FrameDecoder(_options);
            _flows[flowId] = flow = new FlowState { Budget = ScanProbeBytes, Decoder = decoder, Stats = decoder.Stats };
        }
        return flow;
    }
}
