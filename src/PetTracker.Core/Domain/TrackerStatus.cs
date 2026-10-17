namespace PetTracker.Core.Domain;

public enum TrackerStatusKind { NoNpcap, WaitingForGame, Connected, SnapshotLoaded, UnknownVersion, Desync }

public sealed record TrackerStatus(TrackerStatusKind Kind, string Text)
{
    /// <summary>Connected for this long with heartbeats but no snapshot and no pet events means the opcodes probably changed.</summary>
    public static readonly TimeSpan UnknownVersionAfter = TimeSpan.FromSeconds(60);

    public static TrackerStatus Evaluate(TrackerStatusInput i)
    {
        if (i.NpcapMissing)
            return new(TrackerStatusKind.NoNpcap, "No Npcap");
        if (i.Desync)
            return new(TrackerStatusKind.Desync, "Desync");
        if (i.SnapshotAt is { } snapshot)
            return new(TrackerStatusKind.SnapshotLoaded, $"Snapshot loaded {snapshot.ToLocalTime():HH:mm}");
        if (i.Heartbeats > 0)
        {
            if (i.FirstHeartbeatAt is { } first && i.Now - first > UnknownVersionAfter && !i.PetEventSeen)
                return new(TrackerStatusKind.UnknownVersion, "Unknown version");
            return new(TrackerStatusKind.Connected, "Connected");
        }
        return i.Connections > 0
            ? new(TrackerStatusKind.Connected, "Connected")
            : new(TrackerStatusKind.WaitingForGame, "Waiting for game");
    }
}

/// <param name="Desync">Framing errors were seen recently.</param>
/// <param name="SnapshotAt">When a pet snapshot arrived in this session (not when persisted state was loaded).</param>
/// <param name="PetEventSeen">Any pet message arrived in this session.</param>
public sealed record TrackerStatusInput(
    bool NpcapMissing, bool Desync, long Heartbeats, int Connections,
    DateTime? FirstHeartbeatAt, DateTime? SnapshotAt, bool PetEventSeen, DateTime Now);
