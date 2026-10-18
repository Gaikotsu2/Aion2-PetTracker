using PetTracker.Core.Messages;

namespace PetTracker.Core.Domain;

/// <summary>
/// The player's pet state, rebuilt from a snapshot and moved by live messages. Not thread-safe: call it from one thread.
/// The messages of one burst may arrive in any order; the arithmetic is commutative and the consistency checks wait until
/// the burst window has passed (<see cref="Tick"/>, or <see cref="Flush"/> at the end of a replay).
/// </summary>
public sealed class PetCollection(PetRules? rules = null)
{
    private sealed class Entry(int id)
    {
        public int Id = id;
        public int Level;
        public int Progress;
        public bool NeedsResync;
        public DateTime LastUpdated;
        public PetState ToState() => new(Id, Level, Progress, Level > 0, LastUpdated, NeedsResync);
    }

    private readonly record struct Pending(int OldLevel, int NewLevel, DateTime At);
    private readonly record struct EarlyConsumed(int Value, DateTime At);

    private readonly Dictionary<int, Entry> _pets = [];
    private readonly Dictionary<int, Pending> _pending = [];
    private readonly Dictionary<int, EarlyConsumed> _early = [];
    private readonly Dictionary<int, DateTime> _settleAt = [];

    public PetRules Rules { get; } = rules ?? new PetRules();

    public PetCollectionSnapshotMessage? LastSnapshot { get; private set; }
    public DateTime? SnapshotAt { get; private set; }
    public int Count => _pets.Count;

    public IReadOnlyList<PetState> Pets => _pets.Values.OrderBy(e => e.Id).Select(e => e.ToState()).ToList();

    public PetState? Get(int petId) => _pets.TryGetValue(petId, out var e) ? e.ToState() : null;

    /// <summary>Replaces everything with persisted state (no snapshot message is kept).</summary>
    public void Restore(IEnumerable<PetState> states)
    {
        Clear();
        foreach (var s in states)
            _pets[s.Id] = new Entry(s.Id) { Level = s.Level, Progress = s.Progress, NeedsResync = s.NeedsResync, LastUpdated = s.LastUpdated };
    }

    public IReadOnlyList<DomainEvent> Apply(PetMessage message, DateTime now)
    {
        var events = new List<DomainEvent>();
        Settle(now, force: false, events);
        switch (message)
        {
            case PetCollectionSnapshotMessage snapshot: ApplySnapshot(snapshot, now, events); break;
            case PetSoulsGainedMessage gained: ApplyGained(gained, now, events); break;
            case PetLevelChangedMessage changed: ApplyLevelChanged(changed, now, events); break;
            case PetSoulsConsumedMessage consumed: ApplyConsumed(consumed, now, events); break;
        }
        return events;
    }

    /// <summary>Finishes bursts whose window has passed: threshold fallback, orphan checks, consistency checks.</summary>
    public IReadOnlyList<DomainEvent> Tick(DateTime now)
    {
        var events = new List<DomainEvent>();
        Settle(now, force: false, events);
        return events;
    }

    /// <summary>Settles everything regardless of time (end of a replay, before saving).</summary>
    public IReadOnlyList<DomainEvent> Flush()
    {
        var events = new List<DomainEvent>();
        Settle(DateTime.MaxValue, force: true, events);
        return events;
    }

    private void Clear()
    {
        _pets.Clear();
        _pending.Clear();
        _early.Clear();
        _settleAt.Clear();
    }

    private void ApplySnapshot(PetCollectionSnapshotMessage s, DateTime now, List<DomainEvent> events)
    {
        Clear();
        foreach (var r in s.Registered)
            _pets[(int)r.PetId] = new Entry((int)r.PetId) { Level = (int)Math.Min(r.Level, (uint)Rules.MaxLevel), LastUpdated = now };

        foreach (var p in s.Progress)
        {
            var id = (int)p.PetId;
            if (!_pets.TryGetValue(id, out var entry))
                _pets[id] = entry = new Entry(id) { LastUpdated = now };
            entry.Progress = (int)Math.Min(p.Progress, int.MaxValue);
        }

        LastSnapshot = s;
        SnapshotAt = now;
        events.Add(new SnapshotLoaded(now, s.Registered.Count, _pets.Count));
    }

    private void ApplyGained(PetSoulsGainedMessage m, DateTime now, List<DomainEvent> events)
    {
        foreach (var e in m.Entries)
        {
            var entry = GetOrAdd((int)e.PetId);
            var amount = (int)Math.Min(e.Amount, int.MaxValue);
            entry.Progress += amount;
            entry.LastUpdated = now;
            events.Add(new SoulGained(now, entry.Id, amount, entry.Progress, Rules.ThresholdFor(entry.Level)));
            Touch(entry.Id, now);
        }
    }

    private void ApplyLevelChanged(PetLevelChangedMessage m, DateTime now, List<DomainEvent> events)
    {
        var entry = GetOrAdd((int)m.PetId);
        var oldLevel = entry.Level;
        var newLevel = (int)Math.Min(m.NewLevel, int.MaxValue);
        entry.Level = newLevel;
        entry.LastUpdated = now;
        Touch(entry.Id, now);

        if (newLevel <= oldLevel)
        {
            if (newLevel < oldLevel)
                Flag(entry, now, events, $"level change to L{newLevel} but the pet was L{oldLevel}");
            return;
        }

        events.Add(oldLevel == 0 ? new PetRegistered(now, entry.Id) : new PetLevelUp(now, entry.Id, oldLevel, newLevel));
        _pending[entry.Id] = new Pending(oldLevel, newLevel, now);

        // 0E90 arrived before 09 90 in the same burst.
        if (_early.Remove(entry.Id, out var early))
        {
            if (now - early.At <= Rules.BurstWindow)
                Consume(entry, early.Value, now, events);
            else
                Flag(entry, now, events, $"consumed value {early.Value} arrived long before the level change");
        }
    }

    private void ApplyConsumed(PetSoulsConsumedMessage m, DateTime now, List<DomainEvent> events)
    {
        foreach (var e in m.Entries)
        {
            var entry = GetOrAdd((int)e.PetId);
            var value = (int)Math.Min(e.Amount, int.MaxValue);
            Touch(entry.Id, now);
            if (_pending.ContainsKey(entry.Id))
                Consume(entry, value, now, events);
            else
                _early[entry.Id] = new EarlyConsumed(value, now);
        }
    }

    /// <summary>Resolves a pending level change with the value the server reported.</summary>
    private void Consume(Entry entry, int reported, DateTime now, List<DomainEvent> events)
    {
        var pending = _pending[entry.Id];
        _pending.Remove(entry.Id);

        var expected = Rules.ThresholdCost(pending.OldLevel, pending.NewLevel);
        if (reported != expected)
            events.Add(new Inconsistency(now, entry.Id,
                $"reported consumed souls {reported} differ from the threshold cost {expected} for L{pending.OldLevel}->L{pending.NewLevel} (ConsumeMode {Rules.ConsumeMode})",
                NeedsResync: false));

        entry.Progress -= Rules.ConsumeMode == ConsumeMode.ReportedValue ? reported : expected;
        entry.LastUpdated = now;
        events.Add(new ProgressAdjusted(now, entry.Id, entry.Progress));
        Touch(entry.Id, now);
    }

    private void Settle(DateTime now, bool force, List<DomainEvent> events)
    {
        var window = Rules.BurstWindow;

        foreach (var (id, p) in _pending.ToList())
        {
            if (!force && now - p.At <= window)
                continue;
            // No 0E90 in the burst. With enough souls to pay the threshold, assume the message was missed and subtract it.
            // Without them the pet was granted directly: buying a new pet in the shop sends only 09 90 (shop-pet.a2cap).
            _pending.Remove(id);
            var entry = _pets[id];
            var cost = Rules.ThresholdCost(p.OldLevel, p.NewLevel);
            if (entry.Progress >= cost)
            {
                entry.Progress -= cost;
                events.Add(new ProgressAdjusted(p.At + window, id, entry.Progress));
            }
            else
            {
                events.Add(new PetAcquired(p.At + window, id, p.NewLevel));
            }
            Touch(id, p.At + window);
        }

        foreach (var (id, early) in _early.ToList())
        {
            if (!force && now - early.At <= window)
                continue;
            _early.Remove(id);
            Flag(_pets[id], early.At + window, events, $"consumed value {early.Value} without a level change");
        }

        foreach (var (id, due) in _settleAt.ToList())
        {
            if (!force && due > now)
                continue;
            if (!force && (_pending.ContainsKey(id) || _early.ContainsKey(id)))
                continue;
            _settleAt.Remove(id);
            Check(_pets[id], due, events);
        }
    }

    private void Check(Entry entry, DateTime at, List<DomainEvent> events)
    {
        if (entry.Progress < 0)
        {
            Flag(entry, at, events, $"negative progress {entry.Progress}, reset to 0");
            entry.Progress = 0;
            events.Add(new ProgressAdjusted(at, entry.Id, 0));
        }

        if (Rules.ThresholdFor(entry.Level) is { } threshold && entry.Progress >= threshold)
            Flag(entry, at, events, $"progress {entry.Progress} reached the L{entry.Level} threshold {threshold} without a level-up");
    }

    private static void Flag(Entry entry, DateTime at, List<DomainEvent> events, string message)
    {
        entry.NeedsResync = true;
        events.Add(new Inconsistency(at, entry.Id, message, NeedsResync: true));
    }

    private void Touch(int petId, DateTime now)
    {
        var due = now + Rules.BurstWindow;
        if (!_settleAt.TryGetValue(petId, out var existing) || existing < due)
            _settleAt[petId] = due;
    }

    private Entry GetOrAdd(int id)
    {
        if (!_pets.TryGetValue(id, out var entry))
            _pets[id] = entry = new Entry(id);
        return entry;
    }
}
