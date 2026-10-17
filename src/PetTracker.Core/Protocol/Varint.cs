namespace PetTracker.Core.Protocol;

public enum VarintStatus { Ok, Incomplete, Invalid }

/// <summary>Unsigned LEB128.</summary>
public static class Varint
{
    public const int MaxLength = 10;

    public static VarintStatus TryRead(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (i >= MaxLength)
                return VarintStatus.Invalid;

            var b = data[i];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                length = i + 1;
                return VarintStatus.Ok;
            }
        }
        return data.Length >= MaxLength ? VarintStatus.Invalid : VarintStatus.Incomplete;
    }

    public static int Write(ulong value, Span<byte> destination)
    {
        var i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[i++] = (byte)value;
        return i;
    }

    public static byte[] Encode(ulong value)
    {
        Span<byte> buffer = stackalloc byte[MaxLength];
        return buffer[..Write(value, buffer)].ToArray();
    }

    public static int SizeOf(ulong value)
    {
        var n = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            n++;
        }
        return n;
    }
}
