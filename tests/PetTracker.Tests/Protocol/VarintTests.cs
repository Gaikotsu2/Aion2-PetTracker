using PetTracker.Core.Protocol;

namespace PetTracker.Tests.Protocol;

public class VarintTests
{
    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(14UL, new byte[] { 0x0E })]
    [InlineData(127UL, new byte[] { 0x7F })]
    [InlineData(128UL, new byte[] { 0x80, 0x01 })]
    [InlineData(300UL, new byte[] { 0xAC, 0x02 })]
    [InlineData(16384UL, new byte[] { 0x80, 0x80, 0x01 })]
    [InlineData(ulong.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    public void RoundTrips(ulong value, byte[] encoded)
    {
        Assert.Equal(encoded, Varint.Encode(value));
        Assert.Equal(VarintStatus.Ok, Varint.TryRead(encoded, out var read, out var length));
        Assert.Equal(value, read);
        Assert.Equal(encoded.Length, length);
        Assert.Equal(encoded.Length, Varint.SizeOf(value));
    }

    [Fact]
    public void TruncatedIsIncomplete() =>
        Assert.Equal(VarintStatus.Incomplete, Varint.TryRead([0x80, 0x80], out _, out _));

    [Fact]
    public void OverlongIsInvalid() =>
        Assert.Equal(VarintStatus.Invalid, Varint.TryRead(Enumerable.Repeat((byte)0x80, 11).ToArray(), out _, out _));

    [Fact]
    public void ReaderParsesMixedFields()
    {
        byte[] data = [0x01, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0xAC, 0x02, 0x68, 0, 0x69, 0];
        var r = new PacketReader(data);
        Assert.Equal(1, r.ReadU8());
        Assert.Equal(0x1234, r.ReadU16());
        Assert.Equal(0x12345678u, r.ReadU32());
        Assert.Equal(300UL, r.ReadVarint());
        Assert.Equal("hi", r.ReadUtf16(2));
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void ReaderThrowsPastEnd() =>
        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new PacketReader([1]);
            r.ReadU16();
        });
}
