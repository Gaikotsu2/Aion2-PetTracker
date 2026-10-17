namespace PetTracker.Core.Protocol;

/// <summary>What clicking "Apply" on the detected-codes banner does.</summary>
/// <param name="Overrides">The new contents of opcodes.json (existing overrides, plus every detected name that differs from the current map).</param>
/// <param name="Registry">The effective map after the change (defaults merged with <paramref name="Overrides"/>).</param>
/// <param name="FramesToReplay">Buffered frames to run through the new map, in order.</param>
public sealed record OpcodeRepairPlan(
    IReadOnlyDictionary<string, IReadOnlyList<ushort>> Overrides, OpcodeRegistry Registry, IReadOnlyList<ObservedFrame> FramesToReplay);

public static class OpcodeRepair
{
    /// <summary>
    /// Frames the old map already handled are not replayed (that would count them twice). The exception: when the old map did not
    /// understand the snapshot, everything from the snapshot onward is replayed, because the snapshot replaces the whole state and
    /// the frames after it belong on top of it.
    /// </summary>
    public static OpcodeRepairPlan Plan(OpcodeDetection detection, OpcodeRegistry defaults,
        IReadOnlyDictionary<string, IReadOnlyList<ushort>> existingOverrides, IReadOnlyList<ObservedFrame> buffered)
    {
        var old = OpcodeOverrides.Merge(defaults, existingOverrides);
        var overrides = new Dictionary<string, IReadOnlyList<ushort>>(existingOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, opcodes) in detection.Mapping())
        {
            if (!old.OpcodesFor(name).Order().SequenceEqual(opcodes.Order()))
                overrides[name] = opcodes;
        }
        var applied = OpcodeOverrides.Merge(defaults, overrides);

        var snapshotOps = detection.For(MessageNames.PetCollectionSnapshot).Select(c => c.Opcode).ToHashSet();
        var snapshotWasUnknown = !snapshotOps.Any(op => old.Is(MessageNames.PetCollectionSnapshot, op));
        var start = snapshotWasUnknown ? Math.Max(0, buffered.ToList().FindIndex(f => snapshotOps.Contains(f.Frame.Opcode))) : 0;

        var replay = new List<ObservedFrame>();
        for (var i = start; i < buffered.Count; i++)
        {
            var op = buffered[i].Frame.Opcode;
            var handledBefore = !snapshotWasUnknown
                && old.NamesFor(op).Intersect(applied.NamesFor(op)).Any(n => n != MessageNames.Heartbeat);
            if (!handledBefore && applied.NamesFor(op).Any(n => n != MessageNames.Heartbeat))
                replay.Add(buffered[i]);
        }
        return new OpcodeRepairPlan(overrides, applied, replay);
    }
}
