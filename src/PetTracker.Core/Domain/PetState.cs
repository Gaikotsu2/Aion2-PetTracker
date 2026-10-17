namespace PetTracker.Core.Domain;

/// <summary>Immutable view of one pet. Level 0 = unregistered; Progress = souls toward the next level.</summary>
public sealed record PetState(int Id, int Level, int Progress, bool Registered, DateTime LastUpdated, bool NeedsResync);
