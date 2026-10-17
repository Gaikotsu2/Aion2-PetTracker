using PetTracker.Core.Protocol;

namespace PetTracker.Tests.Protocol;

public class FrameDecoderTests
{
    public static TheoryData<LengthMode> Modes => new()
    {
        LengthMode.ExcludesPrefix,
        LengthMode.IncludesPrefix,
        LengthMode.Fixed(2),
        LengthMode.Fixed(-1),
    };

    private static FrameDecoder Decoder(LengthMode mode) =>
        new(new FrameDecoderOptions { LengthMode = mode });

    [Theory, MemberData(nameof(Modes))]
    public void ChainsFramesInEachMode(LengthMode mode)
    {
        var frames = Decoder(mode).Feed(TestFrames.Sequence(mode, 40));
        Assert.Equal(40, frames.Count);
        Assert.Equal(0x3336, frames[0].Opcode);
    }

    [Theory, MemberData(nameof(Modes))]
    public void HandlesFramesSplitAcrossChunks(LengthMode mode)
    {
        var stream = TestFrames.Sequence(mode, 25, seed: 7);
        var whole = Decoder(mode).Feed(stream);

        var decoder = Decoder(mode);
        var split = new List<Frame>();
        var rng = new Random(3);
        for (var i = 0; i < stream.Length;)
        {
            var n = Math.Min(rng.Next(1, 40), stream.Length - i);
            split.AddRange(decoder.Feed(stream.AsSpan(i, n)));
            i += n;
        }

        Assert.Equal(whole.Count, split.Count);
        Assert.All(whole.Zip(split), p =>
        {
            Assert.Equal(p.First.Opcode, p.Second.Opcode);
            Assert.Equal(p.First.Body, p.Second.Body);
        });
        Assert.Equal(0, decoder.Stats.Desyncs);
    }

    [Fact]
    public void ByteAtATimeWorks()
    {
        var mode = LengthMode.ExcludesPrefix;
        var decoder = Decoder(mode);
        var count = TestFrames.Sequence(mode, 10).Sum(b => decoder.Feed([b]).Count);
        Assert.Equal(10, count);
    }

    [Fact]
    public void DesyncResyncsOnHeartbeat()
    {
        var mode = LengthMode.ExcludesPrefix;
        var decoder = Decoder(mode);
        byte[] garbage = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        var frames = decoder.Feed([.. garbage, .. TestFrames.Heartbeat(mode), .. TestFrames.Build(mode, 0x1e56, 1, 2, 3)]);

        Assert.Equal(1, decoder.Stats.Desyncs);
        Assert.Equal(2, frames.Count);
        Assert.Equal(0x0036, frames[0].Opcode);
        Assert.Equal(0x1e56, frames[1].Opcode);
    }

    [Fact]
    public void StartScanningSkipsToHeartbeatWithoutCountingDesync()
    {
        var mode = LengthMode.ExcludesPrefix;
        var decoder = new FrameDecoder(new FrameDecoderOptions { LengthMode = mode }, startScanning: true);
        var frames = decoder.Feed([0x05, 0x06, 0x07, .. TestFrames.Heartbeat(mode), .. TestFrames.Build(mode, 0x3336, 9)]);
        Assert.Equal(2, frames.Count);
        Assert.Equal(0, decoder.Stats.Desyncs);
        Assert.Equal(1, decoder.Stats.Heartbeats);
    }

    [Fact]
    public void HeartbeatSplitAcrossChunksIsFound()
    {
        var mode = LengthMode.ExcludesPrefix;
        var decoder = new FrameDecoder(new FrameDecoderOptions { LengthMode = mode }, startScanning: true);
        var heartbeat = TestFrames.Heartbeat(mode);
        Assert.Empty(decoder.Feed([0x01, 0x02, .. heartbeat[..2]]));
        Assert.Single(decoder.Feed(heartbeat[2..]));
    }

    [Fact]
    public void ResyncDropsBufferedPartialFrame()
    {
        var mode = LengthMode.ExcludesPrefix;
        var decoder = Decoder(mode);
        var frame = TestFrames.Build(mode, 0x3336, new byte[50]);
        Assert.Empty(decoder.Feed(frame[..20]));
        decoder.Resync();
        var frames = decoder.Feed([.. frame[20..], .. TestFrames.Heartbeat(mode)]);
        Assert.Single(frames);
        Assert.Equal(0x0036, frames[0].Opcode);
    }

    [Fact]
    public void DetectorPicksTheRightMode()
    {
        foreach (var mode in new[] { LengthMode.ExcludesPrefix, LengthMode.IncludesPrefix, LengthMode.Fixed(2) })
        {
            var best = LengthModeDetector.Detect(TestFrames.Sequence(mode, 60, seed: 11))[0];
            Assert.Equal(mode, best.Mode);
            Assert.Equal(60, best.Frames);
        }
    }

    [Fact]
    public void DetectorFindsStreamThatStartsMidFrame()
    {
        var mode = LengthMode.IncludesPrefix;
        byte[] junk = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var best = LengthModeDetector.Detect([.. junk, .. TestFrames.Heartbeat(mode), .. TestFrames.Sequence(mode, 30)])[0];
        Assert.Equal(mode, best.Mode);
        Assert.Equal(11, best.StartOffset);
    }

    [Theory]
    [InlineData("excludes-prefix")]
    [InlineData("includes-prefix")]
    [InlineData("fixed:2")]
    [InlineData("fixed:-1")]
    public void LengthModeParsesItsOwnName(string text) =>
        Assert.Equal(text, LengthMode.Parse(text).ToString());
}
