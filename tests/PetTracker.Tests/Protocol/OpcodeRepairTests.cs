using PetTracker.Core.Domain;
using PetTracker.Core.Messages;
using PetTracker.Core.Protocol;
using PetTracker.Tests.Domain;

namespace PetTracker.Tests.Protocol;

/// <summary>The "Apply" path of the unknown-version fallback: detect by shape, write overrides, replay the buffer.</summary>
public class OpcodeRepairTests
{
    private static readonly OpcodeRegistry Defaults = OpcodeRegistry.LoadDefault();
    private static readonly int[] Ids = Enumerable.Range(1001, 220).ToArray();
    private static readonly long T0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static ObservedFrame F(double s, ushort op, byte[] body) => new(T0 + (long)(s * TimeSpan.TicksPerSecond), new Frame(op, body));

    private static byte[] Snapshot() => PetBytes.Snapshot(
        Enumerable.Range(0, 120).Select(i => (1001u + (uint)i, 1u)).ToArray(),
        Enumerable.Range(0, 60).Select(i => (1001u + (uint)i, 10u)).ToArray());

    /// <summary>Everything renumbered: a patched game.</summary>
    private static List<ObservedFrame> PatchedStream() =>
    [
        F(1, 0x5555, Snapshot()),
        F(20, 0x5601, PetBytes.Amounts(1005, 1)),
        F(30, 0x5601, PetBytes.Amounts(1006, 2)),
    ];

    private static PetCollection Replay(IEnumerable<ObservedFrame> frames, OpcodeRegistry registry)
    {
        var collection = new PetCollection();
        foreach (var f in frames)
            foreach (var m in PetMessageParser.Extract(registry, [f.Frame]))
                collection.Apply(m, new DateTime(f.Ticks, DateTimeKind.Utc));
        collection.Flush();
        return collection;
    }

    [Fact]
    public void WithTheOldMapNothingIsUnderstood()
    {
        Assert.Equal(0, Replay(PatchedStream(), Defaults).Count);
    }

    [Fact]
    public void ApplyingTheDetectionLoadsTheSnapshotAndTheLaterGains()
    {
        var frames = PatchedStream();
        var detection = new PetOpcodeDetector(Ids).Detect(frames);
        Assert.True(detection.HasConfidentSnapshot);

        var plan = OpcodeRepair.Plan(detection, Defaults, OpcodeOverrides.None, frames);

        Assert.Equal([(ushort)0x5555], plan.Overrides[MessageNames.PetCollectionSnapshot]);
        Assert.Equal([(ushort)0x5601], plan.Overrides[MessageNames.PetSoulsGained]);
        Assert.Equal(3, plan.FramesToReplay.Count);

        var collection = Replay(plan.FramesToReplay, plan.Registry);
        Assert.Equal(120, collection.Pets.Count(p => p.Level >= 1));
        Assert.Equal(11, collection.Get(1005)!.Progress); // 10 from the snapshot + 1
        Assert.Equal(12, collection.Get(1006)!.Progress);
        Assert.NotNull(collection.SnapshotAt);
    }

    [Fact]
    public void ExistingOverridesForOtherNamesAreKept()
    {
        var existing = new Dictionary<string, IReadOnlyList<ushort>> { [MessageNames.PetSoulsConsumed] = [0x7F7F] };
        var frames = PatchedStream();
        var plan = OpcodeRepair.Plan(new PetOpcodeDetector(Ids).Detect(frames), Defaults, existing, frames);
        Assert.Equal([(ushort)0x7F7F], plan.Overrides[MessageNames.PetSoulsConsumed]);
        Assert.Equal([(ushort)0x5555], plan.Overrides[MessageNames.PetCollectionSnapshot]);
    }

    [Fact]
    public void FramesTheOldMapAlreadyHandledAreNotCountedTwice()
    {
        // Only the snapshot moved; 0D90 still means "souls gained" and was already applied before the click.
        var frames = new List<ObservedFrame>
        {
            F(1, 0x5555, Snapshot()),
            F(20, 0x0D90, PetBytes.Amounts(1005, 1)),
        };
        var detection = new PetOpcodeDetector(Ids).Detect(frames);

        var plan = OpcodeRepair.Plan(detection, Defaults, OpcodeOverrides.None, frames);

        // The snapshot was not understood, so everything from it on is replayed: the snapshot resets the state and the gain goes on top, once.
        Assert.Equal(2, plan.FramesToReplay.Count);
        var collection = Replay(plan.FramesToReplay, plan.Registry);
        Assert.Equal(11, collection.Get(1005)!.Progress);
    }

    [Fact]
    public void WhenTheSnapshotWasKnownOnlyUnhandledFramesAreReplayed()
    {
        // The snapshot opcode is fine; gains moved to a new opcode. The snapshot and level frames must not be applied again.
        var frames = new List<ObservedFrame>
        {
            F(1, 0x0090, Snapshot()),
            F(20, 0x5601, PetBytes.Amounts(1005, 1)),
            F(30, 0x5601, PetBytes.Amounts(1005, 1)),
            F(40, 0x5601, PetBytes.Amounts(1006, 1)),
        };
        var plan = OpcodeRepair.Plan(new PetOpcodeDetector(Ids).Detect(frames), Defaults, OpcodeOverrides.None, frames);

        Assert.Equal(3, plan.FramesToReplay.Count);
        Assert.All(plan.FramesToReplay, f => Assert.Equal(0x5601, f.Frame.Opcode));
        Assert.False(plan.Overrides.ContainsKey(MessageNames.PetCollectionSnapshot));
    }
}
