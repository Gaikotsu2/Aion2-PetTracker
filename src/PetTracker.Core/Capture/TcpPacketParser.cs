using System.Buffers.Binary;
using System.Net;

namespace PetTracker.Core.Capture;

public readonly record struct ParsedTcp(
    IPAddress SourceAddress,
    IPAddress DestinationAddress,
    ushort SourcePort,
    ushort DestinationPort,
    uint Sequence,
    TcpFlags Flags,
    int PayloadOffset,
    int PayloadLength);

/// <summary>Minimal Ethernet/IPv4/IPv6/TCP header parser over a raw captured frame.</summary>
public static class TcpPacketParser
{
    public const int LinkNull = 0;
    public const int LinkEthernet = 1;
    public const int LinkRawIp = 101;
    public const int LinkLinuxSll = 113;

    public static bool TryParse(ReadOnlySpan<byte> frame, int linkType, out ParsedTcp result)
    {
        result = default;
        int ip;
        int etherType;
        switch (linkType)
        {
            case LinkEthernet:
                if (frame.Length < 14) return false;
                ip = 14;
                etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
                while (etherType is 0x8100 or 0x88A8 && frame.Length >= ip + 4)
                {
                    etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(ip + 2)..]);
                    ip += 4;
                }
                break;
            case LinkNull:
                if (frame.Length < 5) return false;
                ip = 4;
                etherType = (frame[ip] >> 4) == 4 ? 0x0800 : 0x86DD;
                break;
            case LinkRawIp:
                if (frame.IsEmpty) return false;
                ip = 0;
                etherType = (frame[0] >> 4) == 4 ? 0x0800 : 0x86DD;
                break;
            case LinkLinuxSll:
                if (frame.Length < 16) return false;
                ip = 16;
                etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[14..]);
                break;
            default:
                return false;
        }

        return etherType switch
        {
            0x0800 => TryParseIPv4(frame, ip, out result),
            0x86DD => TryParseIPv6(frame, ip, out result),
            _ => false,
        };
    }

    private static bool TryParseIPv4(ReadOnlySpan<byte> f, int ip, out ParsedTcp result)
    {
        result = default;
        if (f.Length < ip + 20 || f[ip] >> 4 != 4) return false;
        var ihl = (f[ip] & 0x0F) * 4;
        if (ihl < 20 || f[ip + 9] != 6) return false;
        if ((BinaryPrimitives.ReadUInt16BigEndian(f[(ip + 6)..]) & 0x3FFF) != 0) return false;

        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(f[(ip + 2)..]);
        var end = Math.Min(f.Length, ip + totalLength);
        var source = new IPAddress(f.Slice(ip + 12, 4));
        var destination = new IPAddress(f.Slice(ip + 16, 4));
        return TryParseTcp(f, ip + ihl, end, source, destination, out result);
    }

    private static bool TryParseIPv6(ReadOnlySpan<byte> f, int ip, out ParsedTcp result)
    {
        result = default;
        if (f.Length < ip + 40 || f[ip] >> 4 != 6 || f[ip + 6] != 6) return false;

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(f[(ip + 4)..]);
        var end = Math.Min(f.Length, ip + 40 + payloadLength);
        var source = new IPAddress(f.Slice(ip + 8, 16));
        var destination = new IPAddress(f.Slice(ip + 24, 16));
        return TryParseTcp(f, ip + 40, end, source, destination, out result);
    }

    private static bool TryParseTcp(ReadOnlySpan<byte> f, int tcp, int end, IPAddress source, IPAddress destination, out ParsedTcp result)
    {
        result = default;
        if (end < tcp + 20) return false;
        var headerLength = (f[tcp + 12] >> 4) * 4;
        if (headerLength < 20 || tcp + headerLength > end) return false;

        result = new ParsedTcp(
            Normalize(source),
            Normalize(destination),
            BinaryPrimitives.ReadUInt16BigEndian(f[tcp..]),
            BinaryPrimitives.ReadUInt16BigEndian(f[(tcp + 2)..]),
            BinaryPrimitives.ReadUInt32BigEndian(f[(tcp + 4)..]),
            (TcpFlags)(f[tcp + 13] & 0x1F),
            tcp + headerLength,
            end - tcp - headerLength);
        return true;
    }

    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
