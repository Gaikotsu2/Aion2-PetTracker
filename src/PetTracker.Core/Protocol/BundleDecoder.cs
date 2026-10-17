using System.Buffers.Binary;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace PetTracker.Core.Protocol;

public enum BundleFormat { None, Block, Frame }

/// <summary>
/// Bundle = FF FF + u32 (probably the decompressed size) + LZ4 data. Tries LZ4 block first, then the LZ4 frame format,
/// and remembers which one worked.
/// </summary>
public sealed class BundleDecoder
{
    public const int HeaderLength = 6;
    public const int MaxDecompressedSize = 16 * 1024 * 1024;

    public BundleFormat LastFormat { get; private set; }
    public long BlockCount { get; private set; }
    public long FrameCount { get; private set; }
    public long FailureCount { get; private set; }

    public static bool LooksLikeBundle(ReadOnlySpan<byte> data) =>
        data.Length > HeaderLength && data[0] == 0xFF && data[1] == 0xFF;

    public bool TryDecompress(ReadOnlySpan<byte> bundle, out byte[] data, out BundleFormat format)
    {
        data = [];
        format = BundleFormat.None;
        if (!LooksLikeBundle(bundle))
            return false;

        var payload = bundle[HeaderLength..];
        var sizeLe = BinaryPrimitives.ReadUInt32LittleEndian(bundle[2..]);
        var sizeBe = BinaryPrimitives.ReadUInt32BigEndian(bundle[2..]);

        if (TryBlock(payload, sizeLe, out data) || TryBlock(payload, sizeBe, out data))
            format = BundleFormat.Block;
        else if (TryFrame(payload, out data) || TryFrame(bundle[2..], out data))
            format = BundleFormat.Frame;

        if (format == BundleFormat.None)
        {
            FailureCount++;
            return false;
        }

        LastFormat = format;
        if (format == BundleFormat.Block) BlockCount++; else FrameCount++;
        return true;
    }

    private static bool TryBlock(ReadOnlySpan<byte> payload, uint size, out byte[] data)
    {
        data = [];
        if (size == 0 || size > MaxDecompressedSize)
            return false;
        var target = new byte[size];
        int written;
        try { written = LZ4Codec.Decode(payload, target); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException) { return false; }
        if (written != size)
            return false;
        data = target;
        return true;
    }

    private static bool TryFrame(ReadOnlySpan<byte> payload, out byte[] data)
    {
        data = [];
        try
        {
            using var input = new MemoryStream(payload.ToArray());
            using var decoder = LZ4Stream.Decode(input);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxDecompressedSize)
                    return false;
            }
            if (output.Length == 0)
                return false;
            data = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
