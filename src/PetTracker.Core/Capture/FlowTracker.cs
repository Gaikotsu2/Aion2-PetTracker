using System.Net;
using PetTracker.Core.Recording;

namespace PetTracker.Core.Capture;

/// <summary>
/// The shared "which flows are the game's, and what do they carry" stage: <see cref="FlowGate"/> classification feeding
/// <see cref="TcpReassembler"/>. The live <see cref="Pipeline"/> and the offline <see cref="CaptureImporter"/> both use it,
/// so a capture imported from a pcap decodes exactly like a live one.
/// </summary>
public sealed class FlowTracker
{
    private readonly FlowGate _gate;
    private readonly TcpReassembler _reassembler = new();
    private readonly List<A2CapRecord> _pending = [];
    private long _now;

    public FlowTracker(PipelineOptions options)
    {
        _gate = new FlowGate(options.HoldTime, options.HoldBytes, options.Decoder.HeartbeatSignature, options.HeartbeatFallback,
            forcedServerPorts: options.ForcedServerPorts.Count == 0 ? null : options.ForcedServerPorts.ToHashSet());
        _gate.Accepted += (key, reason) => _pending.Add(new ConnectionEvent(_now,
            reason == "heartbeat" ? ConnectionEventKind.HeartbeatLocked : ConnectionEventKind.Info, 0, $"game flow {key} accepted via {reason}"));
    }

    public long Gaps => _reassembler.Gaps;
    public int FlowsAccepted { get; private set; }

    /// <summary>Offers a segment; returns the records it produced (possibly a released backlog).</summary>
    public IReadOnlyList<A2CapRecord> Offer(TcpSegment segment)
    {
        _now = segment.Ticks;
        return Reassemble(_gate.Offer(segment));
    }

    /// <summary>Replaces the TCP-table endpoints; flows held earlier that now match are released.</summary>
    public IReadOnlyList<A2CapRecord> SetKnown(IEnumerable<IPEndPoint> remotes, long nowTicks)
    {
        _now = nowTicks;
        return Reassemble(_gate.SetKnown(remotes));
    }

    public void Expire(long nowTicks) => _gate.Expire(nowTicks);

    private List<A2CapRecord> Reassemble(IReadOnlyList<TcpSegment> released)
    {
        var output = new List<A2CapRecord>(_pending);
        FlowsAccepted += _pending.Count;
        _pending.Clear();
        foreach (var segment in released)
            output.AddRange(_reassembler.Process(segment));
        return output;
    }
}
