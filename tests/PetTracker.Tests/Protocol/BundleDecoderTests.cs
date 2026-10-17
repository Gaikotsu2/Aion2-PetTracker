using PetTracker.Core.Protocol;

namespace PetTracker.Tests.Protocol;

public class BundleDecoderTests
{
    private static readonly LengthMode Mode = LengthMode.ExcludesPrefix;

    private static byte[] Inner() =>
        [.. TestFrames.Build(Mode, 0x1e56, 1, 2, 3, 4, 5), .. TestFrames.Build(Mode, 0x3436, new byte[200]), .. TestFrames.Build(Mode, 0x0438)];

    [Fact]
    public void DecompressesBlockFormat()
    {
        var ok = new BundleDecoder().TryDecompress(TestFrames.BlockBundle(Inner()), out var data, out var format);
        Assert.True(ok);
        Assert.Equal(BundleFormat.Block, format);
        Assert.Equal(Inner(), data);
    }

    [Fact]
    public void FallsBackToFrameFormat()
    {
        var decoder = new BundleDecoder();
        Assert.True(decoder.TryDecompress(TestFrames.FrameBundle(Inner()), out var data, out var format));
        Assert.Equal(BundleFormat.Frame, format);
        Assert.Equal(Inner(), data);
        Assert.Equal(BundleFormat.Frame, decoder.LastFormat);
    }

    [Fact]
    public void RejectsGarbage()
    {
        var decoder = new BundleDecoder();
        Assert.False(decoder.TryDecompress([0xFF, 0xFF, 1, 0, 0, 0, 9, 9, 9, 9, 9, 9], out _, out _));
        Assert.Equal(1, decoder.FailureCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BundleInsideFrameBodyYieldsInnerFrames(bool frameFormat)
    {
        var bundle = frameFormat ? TestFrames.FrameBundle(Inner()) : TestFrames.BlockBundle(Inner());
        var decoder = new FrameDecoder(new FrameDecoderOptions { LengthMode = Mode });

        var frames = decoder.Feed([.. TestFrames.Build(Mode, 0x2a2a, bundle), .. TestFrames.Build(Mode, 0x3336, 7)]);

        Assert.Equal([(ushort)0x1e56, (ushort)0x3436, (ushort)0x0438, (ushort)0x3336], frames.Select(f => f.Opcode));
        Assert.Equal([true, true, true, false], frames.Select(f => f.Bundled));
        Assert.Equal(frameFormat ? 1 : 0, decoder.Stats.BundlesFrame);
        Assert.Equal(frameFormat ? 0 : 1, decoder.Stats.BundlesBlock);
    }

    [Fact]
    public void BundleAsTheFrameItself()
    {
        var bundle = TestFrames.BlockBundle(Inner());
        var decoder = new FrameDecoder(new FrameDecoderOptions { LengthMode = Mode });
        var frames = decoder.Feed(Varint.Encode((ulong)bundle.Length).Concat(bundle).ToArray());
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.True(f.Bundled));
    }

    [Fact]
    public void BundleBytesSplitAcrossChunks()
    {
        var wire = TestFrames.Build(Mode, 0x2a2a, TestFrames.BlockBundle(Inner()));
        var decoder = new FrameDecoder(new FrameDecoderOptions { LengthMode = Mode });
        Assert.Empty(decoder.Feed(wire[..30]));
        Assert.Equal(3, decoder.Feed(wire[30..]).Count);
    }
}
