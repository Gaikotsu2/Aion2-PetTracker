using System.Buffers.Binary;

namespace PetTracker.Core.Capture;

/// <summary>One packet from an offline capture file, with its timestamp as UTC ticks.</summary>
public readonly record struct CapturedPacket(long Ticks, int LinkType, byte[] Data);

/// <summary>
/// Minimal managed reader for classic libpcap and pcapng files. It needs no Npcap or other native library, so
/// offline analysis works on any machine. A truncated final record (a capture that was killed) simply ends the read.
/// </summary>
public static class CaptureFileReader
{
    private const int MaxPacket = 16 * 1024 * 1024;
    private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;

    public static IEnumerable<CapturedPacket> ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024);
        foreach (var packet in Read(stream))
            yield return packet;
    }

    public static IEnumerable<CapturedPacket> Read(Stream stream)
    {
        var magic = new byte[4];
        if (!TryReadExactly(stream, magic))
            throw new InvalidDataException("File is too short to be a capture.");
        var m = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        return m switch
        {
            0xA1B2C3D4 or 0xA1B23C4D => ReadClassic(stream, littleEndian: true, nanos: m == 0xA1B23C4D),
            0xD4C3B2A1 or 0x4D3CB2A1 => ReadClassic(stream, littleEndian: false, nanos: m == 0x4D3CB2A1),
            0x0A0D0D0A => ReadPcapNg(stream),
            _ => throw new InvalidDataException("Not a pcap or pcapng capture file (unknown magic)."),
        };
    }

    private static IEnumerable<CapturedPacket> ReadClassic(Stream stream, bool littleEndian, bool nanos)
    {
        var rest = new byte[20];
        if (!TryReadExactly(stream, rest))
            throw new InvalidDataException("pcap header is truncated.");
        var network = (int)(U32(rest.AsSpan(16), littleEndian) & 0x0FFFFFFF);
        var header = new byte[16];
        while (TryReadExactly(stream, header))
        {
            var sec = U32(header, littleEndian);
            var frac = U32(header.AsSpan(4), littleEndian);
            var length = U32(header.AsSpan(8), littleEndian);
            if (length > MaxPacket)
                throw new InvalidDataException($"pcap record claims {length} bytes; the file is corrupt.");
            var data = new byte[length];
            if (!TryReadExactly(stream, data))
                yield break;
            var ticks = EpochTicks + sec * TimeSpan.TicksPerSecond + (nanos ? frac / 100 : frac * 10L);
            yield return new CapturedPacket(ticks, network, data);
        }
    }

    private sealed class Iface(int linkType, long unitsPerSecond)
    {
        public int LinkType { get; } = linkType;
        public long UnitsPerSecond { get; } = unitsPerSecond;
    }

    private static IEnumerable<CapturedPacket> ReadPcapNg(Stream stream)
    {
        // The first 4 bytes (the section header block type) are already consumed.
        var le = true;
        var interfaces = new List<Iface>();
        var first = true;
        var blockHeader = new byte[8];
        var lastTicks = 0L;
        while (true)
        {
            uint type;
            if (first)
            {
                type = 0x0A0D0D0A;
                if (!TryReadExactly(stream, blockHeader.AsSpan(4, 4)))
                    yield break;
            }
            else
            {
                if (!TryReadExactly(stream, blockHeader))
                    yield break;
                type = U32(blockHeader, le);
            }

            // The section header defines the byte order, so read its magic before trusting any length.
            if (type == 0x0A0D0D0A)
            {
                var bom = new byte[4];
                if (!TryReadExactly(stream, bom))
                    yield break;
                le = BinaryPrimitives.ReadUInt32LittleEndian(bom) == 0x1A2B3C4D;
                if (!le && BinaryPrimitives.ReadUInt32BigEndian(bom) != 0x1A2B3C4D)
                    throw new InvalidDataException("pcapng section header has a bad byte-order magic.");
                var shbLength = U32(blockHeader.AsSpan(4), le);
                if (shbLength < 16 || shbLength > MaxPacket)
                    throw new InvalidDataException("pcapng section header is corrupt.");
                if (!Skip(stream, shbLength - 12))
                    yield break;
                interfaces.Clear();
                first = false;
                continue;
            }
            first = false;

            var total = U32(blockHeader.AsSpan(4), le);
            if (total < 12 || total > MaxPacket + 64 || total % 4 != 0)
                throw new InvalidDataException("pcapng block has an invalid length; the file is corrupt.");
            var body = new byte[total - 12];
            if (!TryReadExactly(stream, body))
                yield break;
            if (!Skip(stream, 4))
                yield break;

            switch (type)
            {
                case 1 when body.Length >= 8:
                    interfaces.Add(new Iface(U16(body, le), ParseResolution(body.AsSpan(8), le)));
                    break;
                case 6 when body.Length >= 20:
                {
                    var id = (int)U32(body, le);
                    var units = ((long)U32(body.AsSpan(4), le) << 32) | U32(body.AsSpan(8), le);
                    var captured = (int)U32(body.AsSpan(12), le);
                    if (id >= interfaces.Count || captured < 0 || 20 + captured > body.Length)
                        break;
                    var iface = interfaces[id];
                    lastTicks = EpochTicks + (long)((Int128)units * TimeSpan.TicksPerSecond / iface.UnitsPerSecond);
                    yield return new CapturedPacket(lastTicks, iface.LinkType, body.AsSpan(20, captured).ToArray());
                    break;
                }
                case 3 when body.Length >= 4 && interfaces.Count > 0: // simple packet block: no timestamp
                {
                    var original = (int)U32(body, le);
                    var captured = Math.Min(original, body.Length - 4);
                    yield return new CapturedPacket(lastTicks, interfaces[0].LinkType, body.AsSpan(4, captured).ToArray());
                    break;
                }
            }
        }
    }

    /// <summary>Reads the if_tsresol option (code 9); the default is microseconds.</summary>
    private static long ParseResolution(ReadOnlySpan<byte> options, bool le)
    {
        long units = 1_000_000;
        while (options.Length >= 4)
        {
            var code = U16(options, le);
            var length = U16(options[2..], le);
            if (code == 0)
                break;
            var padded = (length + 3) & ~3;
            if (options.Length < 4 + length)
                break;
            if (code == 9 && length >= 1)
            {
                var v = options[4];
                units = (v & 0x80) == 0 ? Pow(10, Math.Min((int)v, 18)) : 1L << Math.Min(v & 0x7F, 62);
            }
            if (options.Length < 4 + padded)
                break;
            options = options[(4 + padded)..];
        }
        return units;
    }

    private static long Pow(long b, int n)
    {
        var r = 1L;
        for (var i = 0; i < n; i++) r *= b;
        return r;
    }

    private static uint U32(ReadOnlySpan<byte> s, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
    private static int U16(ReadOnlySpan<byte> s, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private static bool Skip(Stream stream, long count)
    {
        var scratch = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            var n = stream.Read(scratch, 0, (int)Math.Min(count, scratch.Length));
            if (n == 0)
                return false;
            count -= n;
        }
        return true;
    }
}
