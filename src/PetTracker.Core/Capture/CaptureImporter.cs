using System.Net;
using PetTracker.Core.Recording;

namespace PetTracker.Core.Capture;

public sealed record ImportOptions
{
    /// <summary>Decoder, heartbeat fallback and gate settings, shared with the live pipeline. <c>ForcedServerPorts</c> is the --server-port list.</summary>
    public PipelineOptions Pipeline { get; init; } = new();
    /// <summary>Wall-clock marker lines (local time) to place on the capture timeline.</summary>
    public IReadOnlyList<MarkerEntry> Markers { get; init; } = [];
    public TimeZoneInfo? TimeZone { get; init; }
}

public sealed record ImportResult(
    long Packets,
    long TcpPackets,
    int GameFlowsAccepted,
    long Records,
    int Markers,
    int MarkersOutsideCapture,
    DateTime? Start,
    DateTime? End);

/// <summary>
/// Offline counterpart of the live <see cref="Pipeline"/>: feeds the packets of a pcap/pcapng through
/// <see cref="TcpPacketParser"/> and the shared <see cref="FlowTracker"/> (FlowGate classification, reassembly) and
/// emits the same <see cref="A2CapRecord"/>s. A capture file has no TCP table and no local address, so the server side
/// of each flow is inferred (forced port, SYN/SYN-ACK, heartbeat bytes, else the busier side).
/// </summary>
public static class CaptureImporter
{
    private readonly record struct Side(IPAddress Address, int Port);

    private sealed class Pair
    {
        public Side Server;
        public int Priority = -1;
        public long BytesA, BytesB;
        public Side A, B;
    }

    private readonly record struct PairKey(Side Lo, Side Hi);

    private const int ByVolume = 0, ByHeartbeat = 1, BySyn = 2, ByForcedPort = 3;

    /// <param name="packets">Called twice (a direction-inference pass, then the real pass), so it must yield the packets afresh each time.</param>
    public static ImportResult Import(Func<IEnumerable<CapturedPacket>> packets, ImportOptions options, Action<A2CapRecord> sink)
    {
        var forced = options.Pipeline.ForcedServerPorts.ToHashSet();
        var heartbeat = options.Pipeline.Decoder.HeartbeatSignature;
        var ignored = ConnectionLocator.IgnoredRemotePorts;

        // Pass 1: decide which end of each TCP connection is the server, and find the capture's time span.
        var pairs = new Dictionary<PairKey, Pair>();
        long packetCount = 0, tcpCount = 0, first = long.MaxValue, last = long.MinValue;
        foreach (var packet in packets())
        {
            packetCount++;
            first = Math.Min(first, packet.Ticks);
            last = Math.Max(last, packet.Ticks);
            if (!TryParse(packet, ignored, out var tcp))
                continue;
            tcpCount++;

            var src = new Side(tcp.SourceAddress, tcp.SourcePort);
            var dst = new Side(tcp.DestinationAddress, tcp.DestinationPort);
            var pair = Lookup(pairs, src, dst, out var fromA);
            if (fromA) pair.BytesA += tcp.PayloadLength; else pair.BytesB += tcp.PayloadLength;

            if (forced.Contains(src.Port)) Decide(pair, src, ByForcedPort);
            else if (forced.Contains(dst.Port)) Decide(pair, dst, ByForcedPort);

            if (tcp.Flags.HasFlag(TcpFlags.Syn))
                Decide(pair, tcp.Flags.HasFlag(TcpFlags.Ack) ? src : dst, BySyn);

            if (pair.Priority < BySyn && tcp.PayloadLength >= heartbeat.Length
                && packet.Data.AsSpan(tcp.PayloadOffset, tcp.PayloadLength).IndexOf(heartbeat) >= 0)
                Decide(pair, src, ByHeartbeat);
        }

        foreach (var pair in pairs.Values.Where(p => p.Priority < 0))
            pair.Server = pair.BytesA >= pair.BytesB ? pair.A : pair.B;

        if (packetCount == 0)
            return new ImportResult(0, 0, 0, 0, 0, 0, null, null);

        var markers = MarkerFile.Resolve(options.Markers, first, options.TimeZone);
        var outside = markers.Count(m => m.Ticks < first - TimeSpan.TicksPerMinute || m.Ticks > last + TimeSpan.TicksPerMinute);

        // Pass 2: the live code path, with the inferred direction instead of the adapter's addresses.
        var tracker = new FlowTracker(options.Pipeline);
        long records = 0;
        var nextMarker = 0;
        var nextExpire = 0L;

        void Write(A2CapRecord record)
        {
            while (nextMarker < markers.Count && markers[nextMarker].Ticks <= record.Ticks)
            {
                sink(markers[nextMarker++]);
                records++;
            }
            sink(record);
            records++;
        }

        foreach (var packet in packets())
        {
            if (!TryParse(packet, ignored, out var tcp))
                continue;

            var src = new Side(tcp.SourceAddress, tcp.SourcePort);
            var dst = new Side(tcp.DestinationAddress, tcp.DestinationPort);
            var pair = Lookup(pairs, src, dst, out _);
            var direction = pair.Server == src ? Direction.ServerToClient : Direction.ClientToServer;

            var payload = direction == Direction.ServerToClient ? packet.Data.AsSpan(tcp.PayloadOffset, tcp.PayloadLength).ToArray() : [];
            var segment = new TcpSegment(packet.Ticks, new IPEndPoint(src.Address, src.Port), new IPEndPoint(dst.Address, dst.Port),
                tcp.Sequence, tcp.Flags, payload, direction);

            foreach (var record in tracker.Offer(segment))
                Write(record);

            if (packet.Ticks >= nextExpire)
            {
                tracker.Expire(packet.Ticks);
                nextExpire = packet.Ticks + TimeSpan.TicksPerSecond;
            }
        }

        for (; nextMarker < markers.Count; nextMarker++)
        {
            sink(markers[nextMarker]);
            records++;
        }

        return new ImportResult(packetCount, tcpCount, tracker.FlowsAccepted, records, markers.Count, outside,
            new DateTime(first, DateTimeKind.Utc), new DateTime(last, DateTimeKind.Utc));
    }

    private static bool TryParse(CapturedPacket packet, IReadOnlySet<int> ignored, out ParsedTcp tcp) =>
        TcpPacketParser.TryParse(packet.Data, packet.LinkType, out tcp)
        && !ignored.Contains(tcp.SourcePort) && !ignored.Contains(tcp.DestinationPort);

    private static Pair Lookup(Dictionary<PairKey, Pair> pairs, Side src, Side dst, out bool fromA)
    {
        var srcFirst = Compare(src, dst) <= 0;
        var key = srcFirst ? new PairKey(src, dst) : new PairKey(dst, src);
        if (!pairs.TryGetValue(key, out var pair))
            pairs[key] = pair = new Pair { A = key.Lo, B = key.Hi, Server = key.Hi };
        fromA = src == pair.A;
        return pair;
    }

    private static int Compare(Side x, Side y)
    {
        var c = x.Port.CompareTo(y.Port);
        return c != 0 ? c : string.CompareOrdinal(x.Address.ToString(), y.Address.ToString());
    }

    private static void Decide(Pair pair, Side server, int priority)
    {
        if (priority > pair.Priority)
        {
            pair.Priority = priority;
            pair.Server = server;
        }
    }
}
