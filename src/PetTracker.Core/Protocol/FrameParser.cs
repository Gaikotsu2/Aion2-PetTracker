namespace PetTracker.Core.Protocol;

public enum ParseStatus { Ok, NeedMore, Invalid }

public static class FrameParser
{
    /// <summary>Parses one frame at the start of <paramref name="data"/>.</summary>
    public static ParseStatus TryParse(ReadOnlySpan<byte> data, LengthMode mode, int maxFrameSize, out ushort opcode, out int bodyOffset, out int total)
    {
        opcode = 0;
        bodyOffset = 0;
        total = 0;

        switch (Varint.TryRead(data, out var declared, out var prefix))
        {
            case VarintStatus.Incomplete: return ParseStatus.NeedMore;
            case VarintStatus.Invalid: return ParseStatus.Invalid;
        }

        var payload = mode.PayloadSize(declared, prefix);
        if (payload < 2 || payload > maxFrameSize)
            return ParseStatus.Invalid;

        total = prefix + (int)payload;
        if (data.Length < total)
            return ParseStatus.NeedMore;

        opcode = (ushort)((data[prefix] << 8) | data[prefix + 1]);
        bodyOffset = prefix + 2;
        return ParseStatus.Ok;
    }

    /// <summary>Counts how many frames chain back to back from the start of <paramref name="data"/>.</summary>
    public static int CountChain(ReadOnlySpan<byte> data, LengthMode mode, int maxFrameSize)
    {
        var count = 0;
        while (!data.IsEmpty && TryParse(data, mode, maxFrameSize, out _, out _, out var total) == ParseStatus.Ok)
        {
            count++;
            data = data[total..];
        }
        return count;
    }
}
