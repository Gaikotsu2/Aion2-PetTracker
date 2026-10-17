using PetTracker.Core.Protocol;
using PetTracker.Tests.Domain;

namespace PetTracker.Tests.Protocol;

public class PetOpcodeDetectorTests
{
    private static readonly int[] CatalogIds = Enumerable.Range(1001, 220).ToArray();
    private static readonly long T0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static long At(double seconds) => T0 + (long)(seconds * TimeSpan.TicksPerSecond);

    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Snapshot(int registered = 120, int progress = 60, uint idOffset = 1001) =>
        PetBytes.Snapshot(
            Enumerable.Range(0, registered).Select(i => (idOffset + (uint)i, (uint)(1 + i % 3))).ToArray(),
            Enumerable.Range(0, progress).Select(i => (idOffset + (uint)i, (uint)(i % 24))).ToArray());

    /// <summary>Same 23-byte layout as the real level change: 2 fixed bytes, id, level, u8, u32, u32, u32.</summary>
    private static byte[] Level(uint id, uint level) =>
        [0x92, 0x09, .. U32(id), .. U32(level), 2, .. U32(6), .. U32(2200), .. U32(0)];

    private static byte[] Souls(uint id, uint value) => [1, .. U32(id), .. U32(value)];

    private static ObservedFrame Frame(double seconds, ushort opcode, byte[] body) => new(At(seconds), new Frame(opcode, body));

    private static ObservedFrame Noise(Random rng, double seconds, ushort opcode)
    {
        var body = new byte[rng.Next(0, 80)];
        rng.NextBytes(body);
        return Frame(seconds, opcode, body);
    }

    /// <summary>A login plus one soul drop, one registration and one L2-L3 level-up, with the given (arbitrary) opcodes.</summary>
    private static List<ObservedFrame> Session(Random rng, ushort snapshot, ushort gained, ushort levelA, ushort levelB, ushort consumed, ushort beat)
    {
        var frames = new List<ObservedFrame>();
        for (var i = 0; i < 30; i++)
            frames.Add(Frame(i * 0.3, beat, BitConverter.GetBytes(1_790_000_000_000L + i * 300)));
        for (var i = 0; i < 40; i++)
            frames.Add(Noise(rng, 2 + i * 0.1, (ushort)(0x2000 + rng.Next(0, 200))));
        frames.Add(Frame(8, snapshot, Snapshot()));
        frames.Add(Frame(30.0, gained, Souls(1100, 1)));
        frames.Add(Frame(30.1, levelA, Level(1100, 1)));
        frames.Add(Frame(30.1, consumed, Souls(1100, 5)));
        frames.Add(Frame(60.0, gained, Souls(1050, 1)));
        frames.Add(Frame(70.0, gained, Souls(1060, 1)));
        frames.Add(Frame(80.0, gained, Souls(1090, 1)));
        frames.Add(Frame(80.1, levelB, Level(1090, 3)));
        frames.Add(Frame(80.1, consumed, Souls(1090, 75)));
        return [.. frames.OrderBy(f => f.Ticks)];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void FindsEveryMessageUnderShuffledOpcodes(int seed)
    {
        var rng = new Random(seed);
        var pool = Enumerable.Range(0x4100, 0x4000).Select(i => (ushort)i).OrderBy(_ => rng.Next()).Take(6).ToArray();
        var frames = Session(rng, pool[0], pool[1], pool[2], pool[3], pool[4], pool[5]);

        var detection = new PetOpcodeDetector(CatalogIds).Detect(frames);
        var map = detection.Mapping();

        Assert.Equal([pool[0]], map[MessageNames.PetCollectionSnapshot]);
        Assert.Equal([pool[1]], map[MessageNames.PetSoulsGained]);
        Assert.Equal([pool[4]], map[MessageNames.PetSoulsConsumed]);
        Assert.Equal(new[] { pool[2], pool[3] }.Order(), map[MessageNames.PetLevelChanged].Order());
        Assert.Equal([pool[5]], map[MessageNames.Heartbeat]);
        Assert.True(detection.HasConfidentSnapshot);
    }

    [Fact]
    public void ReportsEvidenceAndSampleBytes()
    {
        var frames = Session(new Random(9), 0x0A0A, 0x0B0B, 0x0C0C, 0x0D0D, 0x0E0E, 0x0F0F);
        var snapshot = new PetOpcodeDetector(CatalogIds).Detect(frames).For(MessageNames.PetCollectionSnapshot).Single();
        Assert.Equal(0x0A0A, snapshot.Opcode);
        Assert.Equal(1, snapshot.Matches);
        Assert.Equal(1, snapshot.Frames);
        Assert.Contains("N=120", snapshot.Evidence);
        Assert.StartsWith("9904000078E9030000", snapshot.SampleHex); // u32, varint 120, first id 1001
    }

    [Fact]
    public void ModerateAmountOfNoiseProposesNothing()
    {
        var rng = new Random(3);
        var frames = Enumerable.Range(0, 2000).Select(i => Noise(rng, i * 0.05, (ushort)rng.Next(0x100, 0x400))).ToList();
        var detection = new PetOpcodeDetector(CatalogIds).Detect(frames);
        Assert.Empty(detection.Mapping());
        Assert.False(detection.HasConfidentSnapshot);
    }

    [Fact]
    public void SnapshotNeedsMostIdsToBeInTheCatalog()
    {
        // Same shape, but the ids are far outside the catalog.
        var frames = new List<ObservedFrame> { Frame(1, 0x1234, Snapshot(idOffset: 50_000)) };
        Assert.Empty(new PetOpcodeDetector(CatalogIds).Detect(frames).Mapping());
    }

    [Fact]
    public void SnapshotListsOutsideTheLimitsAreRejected()
    {
        var tooSmall = Snapshot(registered: 10, progress: 5);
        var progressTooHigh = PetBytes.Snapshot(
            Enumerable.Range(0, 100).Select(i => (1001u + (uint)i, 1u)).ToArray(), [(1001u, 80u)]);
        var levelFour = PetBytes.Snapshot(
            Enumerable.Range(0, 100).Select(i => (1001u + (uint)i, 4u)).ToArray(), []);
        var detector = new PetOpcodeDetector(CatalogIds);
        foreach (var body in new[] { tooSmall, progressTooHigh, levelFour })
            Assert.Empty(detector.Detect([Frame(1, 0x1234, body)]).Mapping());
    }

    [Fact]
    public void SnapshotWithTrailingBytesAndAFewUnknownIdsStillMatches()
    {
        var registered = Enumerable.Range(0, 100).Select(i => (i < 5 ? 5000u + (uint)i : 1001u + (uint)i, 1u)).ToArray(); // 5% unknown
        var body = PetBytes.Snapshot(registered, [(1001u, 3u)], [6, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var detection = new PetOpcodeDetector(CatalogIds).Detect([Frame(1, 0x4321, body)]);
        Assert.Equal([(ushort)0x4321], detection.Mapping()[MessageNames.PetCollectionSnapshot]);
    }

    [Fact]
    public void LargeValueFollowingALevelChangeIsConsumedAndTheSameValueAloneIsGained()
    {
        var frames = new List<ObservedFrame>
        {
            // value 5 with no level change before it: a plain gain (for example a chest paying 5 souls)
            Frame(1.0, 0x5001, Souls(1010, 5)),
            Frame(2.0, 0x5001, Souls(1011, 2)),
            Frame(10.0, 0x6001, Level(1020, 1)),
            Frame(10.0, 0x5002, Souls(1020, 5)),
            Frame(20.0, 0x6002, Level(1021, 2)),
            Frame(20.2, 0x5002, Souls(1021, 25)),
        };
        var map = new PetOpcodeDetector(CatalogIds).Detect(frames).Mapping();
        Assert.Equal([(ushort)0x5001], map[MessageNames.PetSoulsGained]);
        Assert.Equal([(ushort)0x5002], map[MessageNames.PetSoulsConsumed]);
    }

    [Fact]
    public void ValueMoreThanASecondAfterTheLevelChangeIsNotConsumed()
    {
        var frames = new List<ObservedFrame>
        {
            Frame(10.0, 0x6001, Level(1020, 1)),
            Frame(12.5, 0x5002, Souls(1020, 5)),
            Frame(14.0, 0x5002, Souls(1020, 25)),
        };
        var map = new PetOpcodeDetector(CatalogIds).Detect(frames).Mapping();
        Assert.False(map.ContainsKey(MessageNames.PetSoulsConsumed));
        Assert.Equal([(ushort)0x5002], map[MessageNames.PetSoulsGained]);
    }

    [Fact]
    public void ValueForAnotherPetIsNotConsumed()
    {
        var frames = new List<ObservedFrame>
        {
            Frame(10.0, 0x6001, Level(1020, 1)),
            Frame(10.1, 0x5002, Souls(1030, 5)),
        };
        Assert.False(new PetOpcodeDetector(CatalogIds).Detect(frames).Mapping().ContainsKey(MessageNames.PetSoulsConsumed));
    }

    [Fact]
    public void LevelChangeNeedsLength23AndACatalogIdAndLevel1To3()
    {
        var detector = new PetOpcodeDetector(CatalogIds);
        var good = Level(1005, 2);
        Assert.True(detector.Detect([Frame(1, 0x7001, good)]).Mapping().ContainsKey(MessageNames.PetLevelChanged));
        foreach (var bad in new byte[][] { Level(1005, 0), Level(1005, 4), Level(9999, 2), [.. good, 0] })
            Assert.False(detector.Detect([Frame(1, 0x7001, bad)]).Mapping().ContainsKey(MessageNames.PetLevelChanged));
    }

    [Fact]
    public void SoulListShapeIsStrict()
    {
        var detector = new PetOpcodeDetector(CatalogIds);
        foreach (var bad in new byte[][]
                 {
                     Souls(1005, 0),                       // value 0
                     Souls(1005, 201),                     // value too big
                     Souls(7777, 3),                       // not in the catalog
                     [2, .. U32(1005), .. U32(1)],         // count says 2, one entry
                     [0],                                  // n = 0
                 })
            Assert.Empty(detector.Detect([Frame(1, 0x8001, bad)]).Mapping());

        byte[] two = [2, .. U32(1005), .. U32(1), .. U32(1006), .. U32(2)];
        Assert.Equal([(ushort)0x8001], detector.Detect([Frame(1, 0x8001, two)]).Mapping()[MessageNames.PetSoulsGained]);
    }

    [Fact]
    public void ConfidenceFallsWhenMostFramesOfAnOpcodeDoNotMatch()
    {
        var frames = new List<ObservedFrame> { Frame(1, 0x9001, Souls(1005, 1)) };
        for (var i = 0; i < 20; i++)
            frames.Add(Frame(2 + i, 0x9001, [1, 2, 3, 4]));
        var detection = new PetOpcodeDetector(CatalogIds).Detect(frames);
        Assert.True(detection.For(MessageNames.PetSoulsGained).Single().Confidence < OpcodeDetection.DefaultMinConfidence);
        Assert.Empty(detection.Mapping());
    }

    [Fact]
    public void DetectedMapIsUsableByTheParser()
    {
        var frames = Session(new Random(11), 0x3A01, 0x3A02, 0x3A03, 0x3A04, 0x3A05, 0x3A06);
        var registry = new OpcodeRegistry(new PetOpcodeDetector(CatalogIds).Detect(frames).Mapping());
        var messages = PetTracker.Core.Messages.PetMessageParser.Extract(registry, frames.Select(f => f.Frame)).ToList();
        Assert.Single(messages.OfType<PetTracker.Core.Messages.PetCollectionSnapshotMessage>());
        Assert.Equal(4, messages.OfType<PetTracker.Core.Messages.PetSoulsGainedMessage>().Count());
        Assert.Equal(2, messages.OfType<PetTracker.Core.Messages.PetLevelChangedMessage>().Count());
        Assert.Equal(2, messages.OfType<PetTracker.Core.Messages.PetSoulsConsumedMessage>().Count());
    }
}
