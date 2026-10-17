using System.Net;

namespace PetTracker.Core.Capture;

public enum Direction { Unknown, ServerToClient, ClientToServer }

[Flags]
public enum TcpFlags : byte
{
    None = 0,
    Fin = 0x01,
    Syn = 0x02,
    Rst = 0x04,
    Psh = 0x08,
    Ack = 0x10,
}

/// <summary>One captured TCP segment. Payload is only retained for server-to-client segments.</summary>
public sealed record TcpSegment(
    long Ticks,
    IPEndPoint Source,
    IPEndPoint Destination,
    uint Sequence,
    TcpFlags Flags,
    byte[] Payload,
    Direction Direction);

public readonly record struct FlowKey(IPEndPoint Source, IPEndPoint Destination)
{
    public FlowKey Reversed => new(Destination, Source);
    public override string ToString() => $"{Source} -> {Destination}";
}

/// <summary>An established TCP connection owned by the game process.</summary>
public sealed record GameConnection(int Pid, IPEndPoint Local, IPEndPoint Remote)
{
    public override string ToString() => $"pid {Pid} {Local} <-> {Remote}";
}
