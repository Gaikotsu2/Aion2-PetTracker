namespace PetTracker.Core.Domain;

/// <summary>Souls gained since the tracker started (or <see cref="Reset"/>), in total and per pet.</summary>
public sealed class SessionStats(DateTime startedAt)
{
    private readonly Dictionary<int, int> _perPet = [];

    /// <summary>Elapsed time is never counted as less than this, so one early soul does not read as thousands per hour.</summary>
    public static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(1);

    public DateTime StartedAt { get; private set; } = startedAt;
    public int SoulsThisSession { get; private set; }
    public IReadOnlyDictionary<int, int> PerPet => _perPet;

    public int GainsFor(int petId) => _perPet.GetValueOrDefault(petId);

    public void Observe(DomainEvent e)
    {
        if (e is not SoulGained g)
            return;
        SoulsThisSession += g.Amount;
        _perPet[g.PetId] = _perPet.GetValueOrDefault(g.PetId) + g.Amount;
    }

    public double SoulsPerHour(DateTime now)
    {
        var elapsed = now - StartedAt;
        if (elapsed < MinimumElapsed)
            elapsed = MinimumElapsed;
        return SoulsThisSession / elapsed.TotalHours;
    }

    public void Reset(DateTime now)
    {
        StartedAt = now;
        SoulsThisSession = 0;
        _perPet.Clear();
    }
}
