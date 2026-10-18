namespace PetTracker.Core.Domain;

public abstract record DomainEvent(DateTime Timestamp);

public sealed record SoulGained(DateTime Timestamp, int PetId, int Amount, int NewProgress, int? Threshold) : DomainEvent(Timestamp);

public sealed record PetRegistered(DateTime Timestamp, int PetId) : DomainEvent(Timestamp);

/// <summary>A level change that consumed no souls: the pet was granted directly (shop purchase of a new pet).</summary>
public sealed record PetAcquired(DateTime Timestamp, int PetId, int Level) : DomainEvent(Timestamp);

public sealed record PetLevelUp(DateTime Timestamp, int PetId, int OldLevel, int NewLevel) : DomainEvent(Timestamp);

/// <summary>Progress changed because a level change consumed souls (not shown in the live feed).</summary>
public sealed record ProgressAdjusted(DateTime Timestamp, int PetId, int NewProgress) : DomainEvent(Timestamp);

public sealed record SnapshotLoaded(DateTime Timestamp, int Count, int TotalKnown) : DomainEvent(Timestamp);

/// <summary>Something did not add up. When <see cref="NeedsResync"/> is set the pet stays flagged until the next snapshot.</summary>
public sealed record Inconsistency(DateTime Timestamp, int? PetId, string Message, bool NeedsResync) : DomainEvent(Timestamp);

/// <summary>A note for events.jsonl that is not about a pet (opcode detection, applied overrides, framing trouble, startup).</summary>
public sealed record SystemNote(DateTime Timestamp, string Kind, string Message) : DomainEvent(Timestamp);
