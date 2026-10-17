using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using PetTracker.Core.Protocol;

namespace PetTracker.Tests;

internal static class TestFrames
{
    public static byte[] Build(LengthMode mode, ushort opcode, params byte[] body)
    {
        var payload = new byte[2 + body.Length];
        payload[0] = (byte)(opcode >> 8);
        payload[1] = (byte)opcode;
        body.CopyTo(payload, 2);

        ulong declared;
        switch (mode.Kind)
        {
            case LengthKind.ExcludesPrefix:
                declared = (ulong)payload.Length;
                break;
            case LengthKind.IncludesPrefix:
                declared = (ulong)payload.Length + 1;
                while (Varint.SizeOf(declared) + payload.Length != (int)declared)
                    declared++;
                break;
            default:
                declared = (ulong)(payload.Length - mode.Adjustment);
                break;
        }
        return [.. Varint.Encode(declared), .. payload];
    }

    /// <summary>Always starts with wire bytes 0E 00 36, with the body sized for the given mode.</summary>
    public static byte[] Heartbeat(LengthMode mode) => Build(mode, 0x0036, new byte[mode.Kind switch
    {
        LengthKind.IncludesPrefix => 11,
        LengthKind.Fixed => 12 + mode.Adjustment,
        _ => 12,
    }]);

    /// <summary>Random frames whose first opcode is 3336.</summary>
    public static byte[] Sequence(LengthMode mode, int count, int seed = 1)
    {
        var rng = new Random(seed);
        var all = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            var body = new byte[rng.Next(0, 300)];
            rng.NextBytes(body);
            var opcode = i == 0 ? (ushort)0x3336 : (ushort)(0x1000 + rng.Next(0, 0x40) * 0x100 + (i % 2 == 0 ? 0x36 : 0x38));
            all.AddRange(Build(mode, opcode, body));
        }
        return [.. all];
    }

    public static byte[] BlockBundle(byte[] innerFrames)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(innerFrames.Length)];
        var n = LZ4Codec.Encode(innerFrames, target);
        return [0xFF, 0xFF, .. BitConverter.GetBytes((uint)innerFrames.Length), .. target[..n]];
    }

    public static byte[] FrameBundle(byte[] innerFrames)
    {
        using var ms = new MemoryStream();
        using (var encoder = LZ4Stream.Encode(ms, LZ4Level.L00_FAST, leaveOpen: true))
            encoder.Write(innerFrames);
        return [0xFF, 0xFF, .. BitConverter.GetBytes((uint)innerFrames.Length), .. ms.ToArray()];
    }
}
