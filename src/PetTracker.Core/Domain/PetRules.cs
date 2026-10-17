namespace PetTracker.Core.Domain;

/// <summary>How the souls a level change consumes are worked out. Change <see cref="PetRules.ConsumeMode"/> to adjust the hypothesis.</summary>
public enum ConsumeMode
{
    /// <summary>progress -= the value in the PetSoulsConsumed message (the working hypothesis); the threshold is the fallback when it never arrives.</summary>
    ReportedValue,
    /// <summary>progress -= the threshold(s) of the level(s) left behind; the reported value is only cross-checked.</summary>
    Threshold,
}

/// <summary>Everything about the pet rules that is a hypothesis or a number the game may change.</summary>
public sealed record PetRules
{
    /// <summary>Souls needed to leave a level, indexed by the current level: Lv0 (unregistered) 5, L1 25, L2 75. L3 is the maximum.</summary>
    public IReadOnlyList<int> Thresholds { get; init; } = [5, 25, 75];

    public ConsumeMode ConsumeMode { get; init; } = ConsumeMode.ReportedValue;

    /// <summary>Messages of one burst (0D90, 0990, 0E90) may arrive in any order within this window.</summary>
    public TimeSpan BurstWindow { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxLevel => Thresholds.Count;

    /// <summary>Souls needed to leave <paramref name="level"/>, or null at the maximum level.</summary>
    public int? ThresholdFor(int level) => level >= 0 && level < Thresholds.Count ? Thresholds[level] : null;

    /// <summary>Souls that leaving <paramref name="fromLevel"/> up to <paramref name="toLevel"/> costs by threshold.</summary>
    public int ThresholdCost(int fromLevel, int toLevel)
    {
        var total = 0;
        for (var l = Math.Max(fromLevel, 0); l < toLevel && l < Thresholds.Count; l++)
            total += Thresholds[l];
        return total;
    }
}
