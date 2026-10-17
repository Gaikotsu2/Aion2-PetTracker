using System.Net;
using PetTracker.Core.Capture;
using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Tests.Protocol;

/// <summary>The confirmed V-4 framing end to end: stream processing, TLS rejection and game-flow selection.</summary>
public class GameFlowTests
{
    private static readonly LengthMode V4 = LengthMode.Fixed(-4);
    private static readonly IPEndPoint Game = new(IPAddress.Parse("193.202.112.61"), 13328);
    private static readonly IPEndPoint Tls = new(IPAddress.Parse("203.0.113.9"), 8443);
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.168.1.20"), 50000);

    private static IEnumerable<A2CapRecord> Chunks(int flow, byte[] data, int size)
    {
        for (var i = 0; i < data.Length; i += size)
            yield return new S2CChunk(i, flow, data[i..Math.Min(data.Length, i + size)]);
    }

    [Fact]
    public void DefaultLengthModeIsFixedMinus4() =>
        Assert.Equal(V4, new FrameDecoderOptions().LengthMode);

    [Fact]
    public void HeartbeatExampleFromTheRealStreamParses()
    {
        // 0E 00 36 + 8 bytes: varint 14, payload 10 (2-byte opcode + 8-byte epoch millis).
        byte[] bytes = [0x0E, 0x00, 0x36, 1, 2, 3, 4, 5, 6, 7, 8];
        var frames = new FrameDecoder().Feed(bytes);
        Assert.Equal((ushort)0x0036, Assert.Single(frames).Opcode);
        Assert.Equal(8, frames[0].Body.Length);
    }

    [Fact]
    public void DetectorPicksFixedMinus4OnAV4Stream()
    {
        var scores = LengthModeDetector.Detect(TestFrames.Sequence(V4, 80, seed: 5));
        Assert.Equal(V4, scores[0].Mode);
        Assert.Equal(80, scores[0].Frames);
        Assert.Contains(scores, s => s.Mode == LengthMode.Fixed(-8));
        Assert.Contains(scores, s => s.Mode == LengthMode.Fixed(8));
        Assert.Contains(scores, s => s.Mode == LengthMode.IncludesPrefix);
        Assert.Contains(scores, s => s.Mode == LengthMode.ExcludesPrefix);
    }

    [Fact]
    public void MixedFramesAndABundleSplitAcrossChunksDecodeCleanly()
    {
        byte[] inner = [.. TestFrames.Build(V4, 0x1e56, 1, 2, 3), .. TestFrames.Heartbeat(V4), .. TestFrames.Build(V4, 0x3436, new byte[600])];
        var bundle = TestFrames.BlockBundle(inner);
        byte[] stream =
        [
            .. TestFrames.Heartbeat(V4),
            .. TestFrames.Build(V4, 0x3336, 9),
            .. TestFrames.Build(V4, 0xFFFF, bundle[2..]),
            .. TestFrames.Build(V4, 0x0438, new byte[2000]),
        ];

        // Awkward chunking (7 bytes at a time) splits the frame headers and the bundle.
        var processor = new StreamProcessor();
        var frames = new List<Frame>();
        var records = new A2CapRecord[] { new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 1, $"{Game} -> {Client}") }
            .Concat(Chunks(1, stream, 7));
        foreach (var record in records)
            if (processor.Process(record) is { } decoded)
                frames.AddRange(decoded.Frames);

        Assert.Equal([(ushort)0x0036, (ushort)0x3336, (ushort)0x1e56, (ushort)0x0036, (ushort)0x3436, (ushort)0x0438], frames.Select(f => f.Opcode));
        Assert.Equal([false, false, true, true, true, false], frames.Select(f => f.Bundled));
        var s = processor.Stats;
        Assert.Equal(0, s.Desyncs);
        Assert.Equal(0, s.BundleFailures);
        Assert.Equal(1, s.Bundles);
        Assert.Equal(3, s.InnerFrames);
        Assert.Equal(2, s.Heartbeats); // one outer, one inside the bundle
        Assert.Equal(0, s.FlowsRejected);
        Assert.Equal(FlowClass.Game, Assert.Single(processor.Flows).Class);
    }

    [Fact]
    public void TlsLookingFlowIsRejectedWithoutDesyncs()
    {
        var processor = new StreamProcessor();
        var frames = 0;
        byte[] tls = [0x16, 0x03, 0x03, 0x00, 0x40, .. new byte[200]];
        A2CapRecord[] records =
        [
            new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 1, $"{Game} -> {Client}"),
            new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 2, $"{Tls} -> {Client}"),
            new S2CChunk(1, 2, tls[..2]), // header split across chunks
            new S2CChunk(2, 2, tls[2..]),
            new S2CChunk(3, 2, [0x17, 0x03, 0x03, 0, 5, 1, 2, 3, 4, 5]),
            new S2CChunk(4, 1, [.. TestFrames.Heartbeat(V4), .. TestFrames.Build(V4, 0x3336, 1)]),
        ];
        foreach (var r in records)
            if (processor.Process(r) is { } d) frames += d.Frames.Count;

        Assert.Equal(2, frames);
        var s = processor.Stats;
        Assert.Equal(0, s.Desyncs);
        Assert.Equal(1, s.FlowsRejected);
        var flows = processor.Flows;
        Assert.Equal(FlowClass.Tls, flows.Single(f => f.FlowId == 2).Class);
        Assert.True(flows.Single(f => f.FlowId == 2).Rejected);
        Assert.Equal(FlowClass.Game, flows.Single(f => f.FlowId == 1).Class);
    }

    [Fact]
    public void FlowWithNoValidFrameInTheFirst4KbIsRejectedNotDesynced()
    {
        var junk = new byte[8000];
        new Random(3).NextBytes(junk);
        junk[0] = 0xFF; // invalid varint start, not a TLS header
        var processor = new StreamProcessor();
        processor.Process(new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 1, "x"));
        foreach (var c in Chunks(1, junk, 1400))
            Assert.Null(processor.Process(c));

        Assert.Equal(0, processor.Stats.Desyncs);
        Assert.Equal(1, processor.Stats.FlowsRejected);
        Assert.Equal(FlowClass.Unknown, processor.Flows.Single().Class);
    }

    [Fact]
    public void LocatorIgnoresWebPorts()
    {
        var local = new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50000);
        TcpTableRow Row(int port) => new(7, 5, local, new IPEndPoint(IPAddress.Parse("193.202.112.61"), port));
        var picked = ConnectionLocator.Select([Row(443), Row(80), Row(13328), Row(13700)], [7]);
        Assert.Equal([13328, 13700], picked.Select(c => c.Remote.Port));
    }

    [Fact]
    public void FilterIsStableAndExcludesWebPorts()
    {
        Assert.Equal("tcp and not port 80 and not port 443", NpcapSource.BuildFilter());
        Assert.Equal("tcp and not port 80 and not port 443 and not port 5222", NpcapSource.BuildFilter([5222, 443]));
    }

    private static TcpSegment Seg(IPEndPoint from, IPEndPoint to, long ticks, byte[] data, TcpFlags flags = TcpFlags.Ack, uint seq = 1000) =>
        new(ticks, from, to, seq, flags, data, Direction.ServerToClient);

    [Fact]
    public void GateHoldsAnUnknownFlowAndReleasesItFromItsFirstByteWhenTheTableCatchesUp()
    {
        var gate = new FlowGate();
        var first = Seg(Game, Client, 1, [1, 2, 3], TcpFlags.Syn | TcpFlags.Ack, 99);
        var second = Seg(Game, Client, 2, new byte[100], TcpFlags.Ack, 100);
        Assert.Empty(gate.Offer(first));
        Assert.Empty(gate.Offer(second));

        var released = gate.SetKnown([Game]);
        Assert.Equal([first, second], released);
        Assert.Single(gate.Offer(Seg(Game, Client, 3, [9], seq: 200)));
    }

    [Fact]
    public void GateAcceptsByHeartbeatAndDropsTlsAndStaleFlows()
    {
        var gate = new FlowGate(TimeSpan.FromSeconds(3));
        var reasons = new List<string>();
        gate.Accepted += (_, r) => reasons.Add(r);

        Assert.Empty(gate.Offer(Seg(Tls, Client, 1, [0x16, 0x03, 0x03, 0, 9, 0x0E, 0x00, 0x36, 1, 1, 1])));
        Assert.Empty(gate.Offer(Seg(Tls, Client, 2, [0x0E, 0x00, 0x36, 1, 1, 1]))); // stays dropped
        Assert.Equal(1, gate.IgnoredTls);

        Assert.Single(gate.Offer(Seg(Game, Client, 1, TestFrames.Heartbeat(V4))));
        Assert.Equal(["heartbeat"], reasons);

        var quiet = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 9000);
        Assert.Empty(gate.Offer(Seg(quiet, Client, 1, [1, 2, 3, 4])));
        gate.Expire(TimeSpan.FromSeconds(4).Ticks);
        Assert.Equal(1, gate.IgnoredTimeout);
        Assert.Empty(gate.SetKnown([quiet])); // too late: it was dropped
    }

    [Fact]
    public void GateWithoutHeartbeatFallbackNeedsTheTable()
    {
        var gate = new FlowGate(heartbeatFallback: false);
        Assert.Empty(gate.Offer(Seg(Game, Client, 1, TestFrames.Heartbeat(V4))));
        Assert.Single(gate.SetKnown([Game]));
    }
}
