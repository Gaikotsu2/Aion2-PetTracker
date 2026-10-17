using System.Buffers.Binary;
using System.Net;
using PetTracker.Core;
using PetTracker.Core.Capture;
using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Tests.Capture;

/// <summary>Offline import: synthetic pcap/pcapng files built in memory, so Npcap is not needed.</summary>
public class ImportTests
{
    private static readonly LengthMode V4 = LengthMode.Fixed(-4);
    private static readonly IPAddress ClientIp = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress GameIp = IPAddress.Parse("193.202.112.61");
    private static readonly IPAddress TlsIp = IPAddress.Parse("203.0.113.9");
    private static readonly long T0 = new DateTime(2026, 10, 17, 18, 30, 0, DateTimeKind.Utc).Ticks;

    private sealed record Pkt(long Ticks, byte[] Frame);

    private static byte[] Ethernet(IPAddress src, int sport, IPAddress dst, int dport, uint seq, TcpFlags flags, byte[] payload)
    {
        var f = new byte[14 + 20 + 20 + payload.Length];
        f[12] = 0x08; // IPv4
        var ip = 14;
        f[ip] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(ip + 2), (ushort)(20 + 20 + payload.Length));
        f[ip + 8] = 64;
        f[ip + 9] = 6;
        src.TryWriteBytes(f.AsSpan(ip + 12), out _);
        dst.TryWriteBytes(f.AsSpan(ip + 16), out _);
        var tcp = ip + 20;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(tcp), (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(tcp + 2), (ushort)dport);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(tcp + 4), seq);
        f[tcp + 12] = 5 << 4;
        f[tcp + 13] = (byte)flags;
        payload.CopyTo(f, tcp + 20);
        return f;
    }

    /// <summary>A client with a game connection (SYN seen, 7-byte server chunks) and a TLS connection, interleaved.</summary>
    private static (List<Pkt> Packets, byte[] GameStream) Scenario()
    {
        var game = TestFrames.Sequence(V4, 40, seed: 11);
        game = [.. TestFrames.Heartbeat(V4), .. game, .. TestFrames.Heartbeat(V4)];
        var packets = new List<Pkt>();
        var t = T0;
        Pkt Add(byte[] frame) { var p = new Pkt(t, frame); t += 1_000_000; packets.Add(p); return p; } // 100 ms apart

        Add(Ethernet(ClientIp, 50000, GameIp, 13328, 500, TcpFlags.Syn, []));
        Add(Ethernet(GameIp, 13328, ClientIp, 50000, 9000, TcpFlags.Syn | TcpFlags.Ack, []));
        Add(Ethernet(ClientIp, 50000, GameIp, 13328, 501, TcpFlags.Ack, []));
        Add(Ethernet(ClientIp, 50001, TlsIp, 8443, 70, TcpFlags.Syn, []));
        Add(Ethernet(TlsIp, 8443, ClientIp, 50001, 1, TcpFlags.Syn | TcpFlags.Ack, []));
        Add(Ethernet(TlsIp, 8443, ClientIp, 50001, 2, TcpFlags.Ack, [0x16, 0x03, 0x03, 0x00, 0x20, .. new byte[32]]));
        Add(Ethernet(ClientIp, 50000, GameIp, 13328, 501, TcpFlags.Ack | TcpFlags.Psh, [9, 9, 9, 9])); // encrypted C2S, ignored
        var seq = 9001u;
        for (var i = 0; i < game.Length; i += 700)
        {
            var chunk = game[i..Math.Min(game.Length, i + 700)];
            Add(Ethernet(GameIp, 13328, ClientIp, 50000, seq, TcpFlags.Ack | TcpFlags.Psh, chunk));
            seq += (uint)chunk.Length;
        }
        Add(Ethernet(GameIp, 443, ClientIp, 50002, 1, TcpFlags.Ack, [0x0E, 0x00, 0x36, 1, 2, 3])); // web port: filtered out
        return (packets, game);
    }

    private static byte[] ClassicPcap(IEnumerable<Pkt> packets, bool bigEndian = false)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void U16(ushort v) { var b = new byte[2]; if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        U32(0xA1B2C3D4); U16(2); U16(4); U32(0); U32(0); U32(65535); U32(1);
        foreach (var p in packets)
        {
            var unix = p.Ticks - DateTime.UnixEpoch.Ticks;
            U32((uint)(unix / TimeSpan.TicksPerSecond));
            U32((uint)(unix % TimeSpan.TicksPerSecond / 10));
            U32((uint)p.Frame.Length); U32((uint)p.Frame.Length);
            ms.Write(p.Frame);
        }
        return ms.ToArray();
    }

    private static byte[] PcapNg(IEnumerable<Pkt> packets)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void Block(uint type, byte[] body)
        {
            var pad = (4 - body.Length % 4) % 4;
            var total = (uint)(12 + body.Length + pad);
            U32(type); U32(total); ms.Write(body); ms.Write(new byte[pad]); U32(total);
        }

        // Section header: byte-order magic, version 1.0, unknown section length.
        Block(0x0A0D0D0A, [0x4D, 0x3C, 0x2B, 0x1A, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        // Interface: Ethernet, snaplen, option if_tsresol = 9 (nanoseconds), end of options.
        Block(1, [1, 0, 0, 0, 0xFF, 0xFF, 0, 0, 9, 0, 1, 0, 9, 0, 0, 0, 0, 0, 0, 0]);
        foreach (var p in packets)
        {
            var nanos = (ulong)(p.Ticks - DateTime.UnixEpoch.Ticks) * 100;
            var body = new byte[20 + p.Frame.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)(nanos >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)nanos);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)p.Frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)p.Frame.Length);
            p.Frame.CopyTo(body, 20);
            Block(6, body);
        }
        return ms.ToArray();
    }

    private static List<A2CapRecord> Import(byte[] file, ImportOptions? options, out ImportResult result)
    {
        var records = new List<A2CapRecord>();
        result = CaptureImporter.Import(() => CaptureFileReader.Read(new MemoryStream(file)), options ?? new ImportOptions { TimeZone = TimeZoneInfo.Utc }, records.Add);
        return records;
    }

    private static List<Frame> Decode(IEnumerable<A2CapRecord> records, out StreamProcessor processor)
    {
        processor = new StreamProcessor();
        var frames = new List<Frame>();
        foreach (var r in records)
            if (processor.Process(r) is { } d)
                frames.AddRange(d.Frames);
        return frames;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassicPcapIsReadWithTimestamps(bool bigEndian)
    {
        var (packets, _) = Scenario();
        var read = CaptureFileReader.Read(new MemoryStream(ClassicPcap(packets, bigEndian))).ToList();
        Assert.Equal(packets.Count, read.Count);
        Assert.Equal(packets.Select(p => p.Ticks), read.Select(p => p.Ticks));
        Assert.All(read, p => Assert.Equal(1, p.LinkType));
        Assert.Equal(packets[3].Frame, read[3].Data);
    }

    [Fact]
    public void PcapNgIsReadWithNanosecondResolution()
    {
        var (packets, _) = Scenario();
        var read = CaptureFileReader.Read(new MemoryStream(PcapNg(packets))).ToList();
        Assert.Equal(packets.Select(p => p.Ticks), read.Select(p => p.Ticks));
        Assert.Equal(packets[^1].Frame, read[^1].Data);
    }

    [Fact]
    public void TruncatedAndForeignFilesAreHandled()
    {
        var (packets, _) = Scenario();
        var bytes = ClassicPcap(packets);
        var cut = CaptureFileReader.Read(new MemoryStream(bytes[..^10])).Count();
        Assert.Equal(packets.Count - 1, cut);
        Assert.Throws<InvalidDataException>(() => CaptureFileReader.Read(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8])).ToList());
    }

    [Fact]
    public void ImportProducesTheSameFramesAsTheLivePath()
    {
        var (packets, gameStream) = Scenario();
        var expected = new FrameDecoder().Feed(gameStream).Select(f => f.Opcode).ToList();

        // Live path: the same segments, with the adapter-style direction, through the shared FlowTracker.
        var tracker = new FlowTracker(new PipelineOptions());
        var live = new List<A2CapRecord>();
        foreach (var p in packets)
        {
            Assert.True(TcpPacketParser.TryParse(p.Frame, TcpPacketParser.LinkEthernet, out var tcp));
            if (tcp.SourcePort is 443) continue;
            var s2c = tcp.SourceAddress.Equals(GameIp) || tcp.SourceAddress.Equals(TlsIp);
            var payload = s2c ? p.Frame.AsSpan(tcp.PayloadOffset, tcp.PayloadLength).ToArray() : [];
            live.AddRange(tracker.Offer(new TcpSegment(p.Ticks, new IPEndPoint(tcp.SourceAddress, tcp.SourcePort),
                new IPEndPoint(tcp.DestinationAddress, tcp.DestinationPort), tcp.Sequence, tcp.Flags, payload,
                s2c ? Direction.ServerToClient : Direction.ClientToServer)));
        }
        var liveFrames = Decode(live, out _);
        Assert.Equal(expected, liveFrames.Select(f => f.Opcode));

        foreach (var file in new[] { ClassicPcap(packets), PcapNg(packets) })
        {
            var imported = Import(file, null, out var result);
            var frames = Decode(imported, out var processor);
            Assert.Equal(liveFrames.Select(f => f.Opcode), frames.Select(f => f.Opcode));
            Assert.Equal(liveFrames.Select(f => f.Body.Length), frames.Select(f => f.Body.Length));
            Assert.Equal(0, processor.Stats.Desyncs);
            Assert.Equal(1, result.GameFlowsAccepted);
            Assert.Equal(packets.Count - 1, result.TcpPackets); // the port-443 packet is not counted
            Assert.Equal(live.Count, imported.Count);
            Assert.Equal(live.Select(r => r.GetType()), imported.Select(r => r.GetType()));
            Assert.Contains(imported, r => r is ConnectionEvent { Kind: ConnectionEventKind.FlowOpened });
        }
    }

    [Fact]
    public void ServerPortForcesAFlowThatHasNoHeartbeat()
    {
        var odd = TestFrames.Sequence(V4, 5, seed: 2); // no heartbeat anywhere
        var packets = new List<Pkt>
        {
            new(T0, Ethernet(GameIp, 7777, ClientIp, 50000, 100, TcpFlags.Ack, odd)),
        };
        var file = ClassicPcap(packets);

        Import(file, null, out var without);
        Assert.Equal(0, without.GameFlowsAccepted);

        var forced = new ImportOptions { TimeZone = TimeZoneInfo.Utc, Pipeline = new PipelineOptions { ForcedServerPorts = [7777] } };
        var records = Import(file, forced, out var withPort);
        Assert.Equal(1, withPort.GameFlowsAccepted);
        Assert.Equal(odd.Length, records.OfType<S2CChunk>().Sum(c => c.Data.Length));
    }

    [Fact]
    public void MarkersAreWrittenInTimeOrderOnTheCaptureTimeline()
    {
        var (packets, _) = Scenario();
        var options = new ImportOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            Markers = MarkerFile.Parse(["18:30:00.450 second", "18:30:00.150 first", "23:59:59 way after"]),
        };
        var records = Import(ClassicPcap(packets), options, out var result);
        var markers = records.OfType<Marker>().ToList();

        Assert.Equal(3, markers.Count);
        Assert.Equal(["first", "second", "way after"], markers.Select(m => m.Note));
        Assert.Equal([1, 2, 3], markers.Select(m => m.Number));
        Assert.Equal(T0 + 1_500_000, markers[0].Ticks);
        Assert.Equal(1, result.MarkersOutsideCapture);

        // Chunk and marker records both land in the file, markers in time order.
        Assert.Contains(records, r => r is S2CChunk);
    }

    [Fact]
    public void MarkerFileParsing()
    {
        var entries = MarkerFile.Parse(["# comment", "", "  09:05:07  ", "18:30:00.5 pet window opened", "7:01:02,25\tfeed pet"]);
        Assert.Equal(3, entries.Count);
        Assert.Equal(new TimeOnly(9, 5, 7), entries[0].Time);
        Assert.Null(entries[0].Note);
        Assert.Equal(new TimeOnly(18, 30, 0, 500), entries[1].Time);
        Assert.Equal("pet window opened", entries[1].Note);
        Assert.Equal(new TimeOnly(7, 1, 2, 250), entries[2].Time);
        Assert.Equal("feed pet", entries[2].Note);

        Assert.Throws<FormatException>(() => MarkerFile.Parse(["12:00:00", "noon"]));
        Assert.Throws<FormatException>(() => MarkerFile.Parse(["25:00:00"]));
        Assert.Throws<FormatException>(() => MarkerFile.Parse(["12:61:00"]));
    }

    [Fact]
    public void MarkerTimesUseLocalTimeAndRollOverMidnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");
        var start = new DateTime(2026, 10, 17, 21, 59, 0, DateTimeKind.Utc).Ticks; // 23:59 local
        var markers = MarkerFile.Resolve(MarkerFile.Parse(["23:59:30 a", "00:00:15.5 b"]), start, zone);

        Assert.Equal(new DateTime(2026, 10, 17, 21, 59, 30, DateTimeKind.Utc).Ticks, markers[0].Ticks);
        Assert.Equal(new DateTime(2026, 10, 17, 22, 0, 15, DateTimeKind.Utc).Ticks + 5_000_000, markers[1].Ticks);
        Assert.Equal(["a", "b"], markers.Select(m => m.Note));
    }
}
