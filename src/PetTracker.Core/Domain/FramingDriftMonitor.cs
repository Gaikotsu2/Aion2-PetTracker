namespace PetTracker.Core.Domain;

/// <summary>
/// Watches the running desync count. More than <see cref="Threshold"/> new desyncs inside <see cref="Window"/> means the frame
/// format itself probably changed (not just a lost packet), so the status tooltip suggests running
/// "pt-research frames --auto-detect". Once suspected it stays suspected until <see cref="Reset"/>. Never changes the framing.
/// </summary>
public sealed class FramingDriftMonitor(int threshold = 5, TimeSpan? window = null)
{
    public const string Hint = "Framing changed? Run pt-research frames --auto-detect";

    private readonly Queue<DateTime> _events = new();
    private long _last;

    public int Threshold { get; } = threshold;
    public TimeSpan Window { get; } = window ?? TimeSpan.FromSeconds(30);
    public bool Suspected { get; private set; }

    /// <summary>Feeds the running desync total. Returns true only on the call that first raises the suspicion.</summary>
    public bool Observe(long desyncTotal, DateTime now)
    {
        if (desyncTotal < _last)
            _last = 0; // the pipeline restarted
        for (var i = _last; i < desyncTotal && _events.Count <= 10_000; i++)
            _events.Enqueue(now);
        _last = desyncTotal;

        while (_events.Count > 0 && now - _events.Peek() > Window)
            _events.Dequeue();

        if (Suspected || _events.Count <= Threshold)
            return false;
        Suspected = true;
        return true;
    }

    /// <summary>The running total fell (flows that only produced noise were dropped): forget the suspicion and count again from here.</summary>
    public void Rebase(long desyncTotal)
    {
        _events.Clear();
        _last = desyncTotal;
        Suspected = false;
    }

    public void Reset()
    {
        _events.Clear();
        _last = 0;
        Suspected = false;
    }
}
