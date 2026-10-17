namespace PetTracker.Core.Domain;

public abstract record DomainEvent(DateTime Timestamp);

public sealed record SoulGained(DateTime Timestamp, int PetId, int Amount, int NewProgress, int? Threshold) : DomainEvent(Timestamp);

public sealed record PetRegistered(DateTime Timestamp, int PetId) : DomainEvent(Timestamp);

public sealed record PetLevelUp(DateTime Timestamp, int PetId, int OldLevel, int NewLevel) : DomainEvent(Timestamp);

/// <summary>Progress changed because a level change consumed souls (not shown in the live feed).</summary>
public sealed record ProgressAdjusted(DateTime Timestamp, int PetId, int NewProgress) : DomainEvent(Timestamp);

public sealed record SnapshotLoaded(DateTime Timestamp, int Count, int TotalKnown) : DomainEvent(Timestamp);

/// <summary>Something did not add up. When <see cref="NeedsResync"/> is set the pet stays flagged until the next snapshot.</summary>
public sealed record Inconsistency(DateTime Timestamp, int? PetId, string Message, bool NeedsResync) : DomainEvent(Timestamp);
