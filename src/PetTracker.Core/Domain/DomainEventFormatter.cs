namespace PetTracker.Core.Domain;

/// <summary>Human-readable lines for the live feed and the replay output.</summary>
public static class DomainEventFormatter
{
    /// <summary>The line for an event, or null for events that are not shown (progress bookkeeping).</summary>
    public static string? Describe(DomainEvent e, PetCatalog catalog) => e switch
    {
        SoulGained g => g.Threshold is { } t
            ? $"+{g.Amount} Soul — {catalog.DisplayName(g.PetId)} {Math.Max(g.NewProgress, 0)}/{t}"
            : $"+{g.Amount} Soul — {catalog.DisplayName(g.PetId)} (max level)",
        PetRegistered r => $"Registered — {catalog.DisplayName(r.PetId)}",
        PetLevelUp l => $"Level {l.NewLevel} — {catalog.DisplayName(l.PetId)}",
        PetAcquired a => $"Acquired directly — {catalog.DisplayName(a.PetId)} (no souls consumed)",
        SnapshotLoaded s => $"Snapshot — {s.Count} registered, {s.TotalKnown} known",
        Inconsistency i => $"Check — {(i.PetId is { } id ? catalog.DisplayName(id) + ": " : "")}{i.Message}",
        _ => null,
    };

    /// <summary>The pet an event is about, if any.</summary>
    public static int? PetIdOf(DomainEvent e) => e switch
    {
        SoulGained g => g.PetId,
        PetRegistered r => r.PetId,
        PetLevelUp l => l.PetId,
        PetAcquired a => a.PetId,
        ProgressAdjusted p => p.PetId,
        Inconsistency i => i.PetId,
        _ => null,
    };
}

/// <summary>Counts per level and the unregistered pets of a collection.</summary>
public sealed record CollectionSummary(int Registered, int TotalKnown, IReadOnlyList<int> PerLevel, IReadOnlyList<PetState> Unregistered)
{
    public static CollectionSummary From(IEnumerable<PetState> pets, int maxLevel = 3)
    {
        var all = pets.ToList();
        var perLevel = new int[maxLevel];
        foreach (var p in all.Where(p => p.Level >= 1))
            perLevel[Math.Min(p.Level, maxLevel) - 1]++;
        return new CollectionSummary(all.Count(p => p.Level >= 1), all.Count, perLevel, all.Where(p => p.Level == 0).OrderBy(p => p.Id).ToList());
    }

    /// <summary>"162 registered, L1 82 / L2 39 / L3 41, unregistered 1091:4 1195:4 1208:2"</summary>
    public override string ToString() =>
        $"{Registered} registered, {string.Join(" / ", PerLevel.Select((n, i) => $"L{i + 1} {n}"))}, unregistered " +
        (Unregistered.Count == 0 ? "none" : string.Join(" ", Unregistered.Select(p => $"{p.Id}:{p.Progress}")));
}
