using System.Net;
using PetTracker.Core.Capture;

namespace PetTracker.Tests.Capture;

public class PacketParserTests
{
    private static byte[] EthernetIPv4Tcp(byte[] payload, uint seq, byte flags)
    {
        var tcp = new byte[20];
        tcp[0] = 0x1E; tcp[1] = 0x61;                 // src port 7777
        tcp[2] = 0xC3; tcp[3] = 0x50;                 // dst port 50000
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(seq)).CopyTo(tcp, 4);
        tcp[12] = 0x50;
        tcp[13] = flags;

        var total = 20 + tcp.Length + payload.Length;
        var ip = new byte[20];
        ip[0] = 0x45;
        ip[2] = (byte)(total >> 8); ip[3] = (byte)total;
        ip[8] = 64; ip[9] = 6;
        IPAddress.Parse("203.0.113.5").GetAddressBytes().CopyTo(ip, 12);
        IPAddress.Parse("192.168.1.20").GetAddressBytes().CopyTo(ip, 16);

        var ethernet = new byte[14];
        ethernet[12] = 0x08;
        byte[] padding = [0, 0, 0, 0];
        return [.. ethernet, .. ip, .. tcp, .. payload, .. padding];
    }

    [Fact]
    public void ParsesEthernetIPv4TcpAndIgnoresPadding()
    {
        var frame = EthernetIPv4Tcp([1, 2, 3], 0xAABBCCDD, 0x18);
        Assert.True(TcpPacketParser.TryParse(frame, TcpPacketParser.LinkEthernet, out var p));

        Assert.Equal(IPAddress.Parse("203.0.113.5"), p.SourceAddress);
        Assert.Equal(7777, p.SourcePort);
        Assert.Equal(50000, p.DestinationPort);
        Assert.Equal(0xAABBCCDDu, p.Sequence);
        Assert.Equal(TcpFlags.Psh | TcpFlags.Ack, p.Flags);
        Assert.Equal(3, p.PayloadLength);
        Assert.Equal([1, 2, 3], frame.AsSpan(p.PayloadOffset, p.PayloadLength).ToArray());
    }

    [Fact]
    public void RejectsNonTcp()
    {
        var frame = EthernetIPv4Tcp([], 1, 0x10);
        frame[14 + 9] = 17;
        Assert.False(TcpPacketParser.TryParse(frame, TcpPacketParser.LinkEthernet, out _));
    }

    [Fact]
    public void HeartbeatLockerLocksTheFlowThatCarriesTheSignature()
    {
        var locker = new HeartbeatLocker();
        var server = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 7777);
        var client = new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50000);

        Assert.Equal(Direction.Unknown, locker.Classify(client, server, new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(Direction.ServerToClient, locker.Classify(server, client, new byte[] { 9, 0x0E, 0x00, 0x36, 0 }));
        Assert.Equal(Direction.ServerToClient, locker.Classify(server, client, new byte[] { 1 }));
        Assert.Equal(Direction.ClientToServer, locker.Classify(client, server, new byte[] { 1 }));
    }
}
