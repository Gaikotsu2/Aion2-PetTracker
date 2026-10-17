using PetTracker.Core.Recording;

namespace PetTracker.Core.Capture;

/// <summary>
/// Per-connection reassembly of server-to-client segments into <see cref="S2CChunk"/>s, plus the flow lifecycle
/// events a decoder needs to stay aligned. Client-to-server payloads are never looked at.
/// </summary>
public sealed class TcpReassembler
{
    private sealed class Flow(int id, TcpStreamReassembler stream)
    {
        public int Id { get; } = id;
        public TcpStreamReassembler Stream { get; } = stream;
    }

    private readonly Dictionary<FlowKey, Flow> _flows = [];
    private int _nextFlowId = 1;

    public long Duplicates { get; private set; }
    public long Gaps { get; private set; }
    public int ActiveFlows => _flows.Count;

    public IReadOnlyList<A2CapRecord> Process(TcpSegment segment)
    {
        var output = new List<A2CapRecord>();
        switch (segment.Direction)
        {
            case Direction.ServerToClient:
                ProcessServerSegment(segment, output);
                break;
            case Direction.ClientToServer when segment.Flags.HasFlag(TcpFlags.Rst) || segment.Flags.HasFlag(TcpFlags.Fin):
                Close(new FlowKey(segment.Destination, segment.Source), segment.Ticks, output);
                break;
        }
        return output;
    }

    public IReadOnlyList<A2CapRecord> CloseAll(long ticks)
    {
        var output = new List<A2CapRecord>();
        foreach (var key in _flows.Keys.ToList())
            Close(key, ticks, output);
        return output;
    }

    public IReadOnlyList<A2CapRecord> CloseFlowsExcept(IReadOnlySet<System.Net.IPEndPoint> remotes, long ticks)
    {
        var output = new List<A2CapRecord>();
        foreach (var key in _flows.Keys.Where(k => !remotes.Contains(k.Source)).ToList())
            Close(key, ticks, output);
        return output;
    }

    private void ProcessServerSegment(TcpSegment segment, List<A2CapRecord> output)
    {
        var key = new FlowKey(segment.Source, segment.Destination);
        var syn = segment.Flags.HasFlag(TcpFlags.Syn);

        if (syn && _flows.ContainsKey(key))
            Close(key, segment.Ticks, output);

        if (!_flows.TryGetValue(key, out var flow))
        {
            flow = new Flow(_nextFlowId++, new TcpStreamReassembler());
            _flows[key] = flow;
            flow.Stream.Start(syn ? segment.Sequence + 1 : segment.Sequence);
            output.Add(new ConnectionEvent(segment.Ticks,
                syn ? ConnectionEventKind.FlowOpened : ConnectionEventKind.FlowJoined, flow.Id, key.ToString()));
        }

        if (segment.Payload.Length > 0)
        {
            var dupsBefore = flow.Stream.Duplicates;
            var result = flow.Stream.Push(segment.Sequence, segment.Payload);
            Duplicates += flow.Stream.Duplicates - dupsBefore;

            if (result.Gap)
            {
                Gaps++;
                output.Add(new ConnectionEvent(segment.Ticks, ConnectionEventKind.Gap, flow.Id, "missing TCP data, resync"));
            }
            if (result.Data is { Length: > 0 } data)
                output.Add(new S2CChunk(segment.Ticks, flow.Id, data));
        }

        if (segment.Flags.HasFlag(TcpFlags.Fin) || segment.Flags.HasFlag(TcpFlags.Rst))
            Close(key, segment.Ticks, output);
    }

    private void Close(FlowKey key, long ticks, List<A2CapRecord> output)
    {
        if (_flows.Remove(key, out var flow))
            output.Add(new ConnectionEvent(ticks, ConnectionEventKind.FlowClosed, flow.Id, key.ToString()));
    }
}
