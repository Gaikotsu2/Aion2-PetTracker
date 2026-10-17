using System.Buffers.Binary;
using System.Text;

namespace PetTracker.Core.Protocol;

/// <summary>Cursor over a frame body. Reads little-endian unless noted; throws <see cref="InvalidDataException"/> past the end.</summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _data;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; private set; }
    public readonly int Remaining => _data.Length - Position;
    public readonly bool AtEnd => Position >= _data.Length;

    public byte ReadU8() => Take(1)[0];
    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public ushort ReadU16BigEndian() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public uint ReadU32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long ReadI64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public ulong ReadVarint()
    {
        if (Varint.TryRead(_data[Position..], out var value, out var length) != VarintStatus.Ok)
            throw new InvalidDataException($"Bad varint at offset {Position}.");
        Position += length;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    public string ReadUtf16(int charCount) => Encoding.Unicode.GetString(Take(charCount * 2));
    public string ReadUtf8(int byteCount) => Encoding.UTF8.GetString(Take(byteCount));

    public void Skip(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"Need {count} bytes at offset {Position}, only {Remaining} left.");
        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }
}
