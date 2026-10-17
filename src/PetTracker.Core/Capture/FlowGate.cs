using System.Net;
using PetTracker.Core.Protocol;

namespace PetTracker.Core.Capture;

/// <summary>
/// Decides which captured TCP flows are the game's. The capture filter is broad (all TCP except web ports), so every
/// new server-to-client flow is held here until it is classified: by matching a TCP-table endpoint, or (fallback) by
/// carrying the heartbeat signature. Held segments are released in arrival order, so an accepted flow is decoded from
/// its first byte. Flows that start like TLS, or stay unclassified for too long, are dropped and remembered.
/// Only byte patterns are examined; nothing is decrypted.
/// </summary>
public sealed class FlowGate(
    TimeSpan? maxAge = null,
    int maxBytes = 256 * 1024,
    byte[]? heartbeat = null,
    bool heartbeatFallback = true,
    int maxPendingFlows = 512,
    IReadOnlySet<int>? forcedServerPorts = null)
{
    private enum State { Pending, Accepted, Ignored }

    private sealed class Entry(long firstTicks)
    {
        public State State = State.Pending;
        public readonly List<TcpSegment> Buffer = [];
        public long Bytes;
        public long FirstTicks = firstTicks;
        public bool HeadChecked;
    }

    private const int MaxIgnoredRemembered = 8192;

    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromSeconds(3);
    private readonly byte[] _heartbeat = heartbeat ?? [0x0E, 0x00, 0x36];
    private readonly Dictionary<FlowKey, Entry> _flows = [];
    private HashSet<IPEndPoint> _known = [];

    /// <summary>Raised when a flow is accepted, with the reason ("TCP table" or "heartbeat").</summary>
    public event Action<FlowKey, string>? Accepted;

    public int PendingFlows => _flows.Values.Count(e => e.State == State.Pending);
    public long IgnoredTls { get; private set; }
    public long IgnoredTimeout { get; private set; }

    /// <summary>Segments that should go on to reassembly now (possibly several: a released backlog).</summary>
    public IReadOnlyList<TcpSegment> Offer(TcpSegment segment)
    {
        var client = segment.Direction == Direction.ClientToServer;
        if (!client && segment.Direction != Direction.ServerToClient)
            return [];

        var key = client ? new FlowKey(segment.Destination, segment.Source) : new FlowKey(segment.Source, segment.Destination);
        var syn = segment.Flags.HasFlag(TcpFlags.Syn);
        var ending = segment.Flags.HasFlag(TcpFlags.Fin) || segment.Flags.HasFlag(TcpFlags.Rst);

        if (_flows.TryGetValue(key, out var entry))
        {
            if (entry.State == State.Accepted)
            {
                if (ending && !syn)
                    Forget(key);
                return [segment];
            }
            if (entry.State == State.Ignored && !client && syn)
            {
                _flows.Remove(key);
                entry = null;
            }
        }

        if (client)
            return []; // client payload is never needed; client FIN/RST only matter for accepted flows

        if (entry is null)
        {
            if (_flows.Count >= MaxIgnoredRemembered + maxPendingFlows)
                _flows.Where(kv => kv.Value.State == State.Ignored).Select(kv => kv.Key).ToList().ForEach(k => _flows.Remove(k));
            entry = new Entry(segment.Ticks);
            _flows[key] = entry;
            TrimPending();
        }

        if (entry.State == State.Ignored)
            return [];

        entry.Buffer.Add(segment);
        entry.Bytes += segment.Payload.Length;

        if (_known.Contains(key.Source))
            return Accept(key, entry, "TCP table");

        if (forcedServerPorts is not null && forcedServerPorts.Contains(key.Source.Port))
            return Accept(key, entry, "forced port");

        if (!entry.HeadChecked && segment.Payload.Length >= 3)
        {
            entry.HeadChecked = true;
            if (StreamProcessor.LooksLikeTls(segment.Payload))
            {
                IgnoredTls++;
                return Ignore(entry);
            }
        }

        if (heartbeatFallback && segment.Payload.Length >= _heartbeat.Length && segment.Payload.AsSpan().IndexOf(_heartbeat) >= 0)
            return Accept(key, entry, "heartbeat");

        if (entry.Bytes > maxBytes || TimeSpan.FromTicks(segment.Ticks - entry.FirstTicks) > _maxAge)
        {
            IgnoredTimeout++;
            return Ignore(entry);
        }
        return [];
    }

    /// <summary>Replaces the TCP-table endpoints; releases held flows that now match.</summary>
    public IReadOnlyList<TcpSegment> SetKnown(IEnumerable<IPEndPoint> remotes)
    {
        _known = remotes.ToHashSet();
        var released = new List<TcpSegment>();
        foreach (var (key, entry) in _flows.ToList())
            if (entry.State == State.Pending && _known.Contains(key.Source))
                released.AddRange(Accept(key, entry, "TCP table"));
        return released;
    }

    /// <summary>Drops pending flows older than the hold time (call periodically so silent flows do not linger).</summary>
    public void Expire(long nowTicks)
    {
        foreach (var entry in _flows.Values)
        {
            if (entry.State == State.Pending && TimeSpan.FromTicks(nowTicks - entry.FirstTicks) > _maxAge)
            {
                IgnoredTimeout++;
                Ignore(entry);
            }
        }
    }

    private IReadOnlyList<TcpSegment> Accept(FlowKey key, Entry entry, string reason)
    {
        entry.State = State.Accepted;
        var released = entry.Buffer.ToList();
        entry.Buffer.Clear();
        Accepted?.Invoke(key, reason);
        // A FIN or RST already inside the backlog ends the flow right after it is released.
        if (released.Any(s => s.Flags.HasFlag(TcpFlags.Fin) || s.Flags.HasFlag(TcpFlags.Rst)))
            Forget(key);
        return released;
    }

    private static IReadOnlyList<TcpSegment> Ignore(Entry entry)
    {
        entry.State = State.Ignored;
        entry.Buffer.Clear();
        entry.Bytes = 0;
        return [];
    }

    private void Forget(FlowKey key)
    {
        // Remember as ignored so stray late segments do not resurrect the flow; a new SYN starts fresh.
        if (_flows.TryGetValue(key, out var entry))
            Ignore(entry);
    }

    private void TrimPending()
    {
        var pending = _flows.Where(kv => kv.Value.State == State.Pending).ToList();
        if (pending.Count <= maxPendingFlows)
            return;
        foreach (var (_, entry) in pending.OrderBy(kv => kv.Value.FirstTicks).Take(pending.Count - maxPendingFlows))
            Ignore(entry);
    }
}
