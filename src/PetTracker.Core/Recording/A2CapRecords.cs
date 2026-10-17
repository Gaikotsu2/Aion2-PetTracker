namespace PetTracker.Core.Recording;

public enum A2CapKind : byte
{
    S2CChunk = 1,
    Marker = 2,
    ConnectionEvent = 3,
}

public enum ConnectionEventKind : byte
{
    /// <summary>Flow seen from its SYN, so its stream starts frame-aligned.</summary>
    FlowOpened = 1,
    /// <summary>Flow first seen mid-stream; the decoder must scan for the heartbeat.</summary>
    FlowJoined = 2,
    FlowClosed = 3,
    /// <summary>TCP gap: bytes were lost, the decoder must resync.</summary>
    Gap = 4,
    /// <summary>The set of game connections (from the TCP table) changed.</summary>
    ConnectionsChanged = 5,
    /// <summary>Fallback: flow locked from the heartbeat alone.</summary>
    HeartbeatLocked = 6,
    Info = 7,
}

public abstract record A2CapRecord(long Ticks);

/// <summary>Reassembled server-to-client bytes of one TCP flow.</summary>
public sealed record S2CChunk(long Ticks, int FlowId, byte[] Data) : A2CapRecord(Ticks);

public sealed record Marker(long Ticks, int Number, string? Note = null) : A2CapRecord(Ticks);

public sealed record ConnectionEvent(long Ticks, ConnectionEventKind Kind, int FlowId, string Detail) : A2CapRecord(Ticks);
