using PetTracker.Core.Domain;
using PetTracker.Core.Messages;
using PetTracker.Core.Persistence;
using PetTracker.Core.Protocol;

namespace PetTracker.Tests.Domain;

internal static class PetBytes
{
    public static byte[] U32(uint v) => BitConverter.GetBytes(v);

    public static byte[] Snapshot((uint id, uint level)[] registered, (uint id, uint progress)[] progress, byte[]? tail = null)
    {
        var b = new List<byte>();
        b.AddRange(U32(0x499));
        b.AddRange(Varint.Encode((ulong)registered.Length));
        foreach (var (id, level) in registered) { b.AddRange(U32(id)); b.AddRange(U32(id)); b.AddRange(U32(level)); }
        b.AddRange(Varint.Encode((ulong)progress.Length));
        foreach (var (id, p) in progress) { b.AddRange(U32(id)); b.AddRange(U32(p)); }
        b.AddRange(tail ?? [0x06, 0xAA, 0x01]);
        return [.. b];
    }

    public static byte[] Amounts(uint id, uint amount) => [1, .. U32(id), .. U32(amount)];

    public static byte[] LevelChange(ushort header, uint id, uint level) =>
        [.. BitConverter.GetBytes(header), .. U32(id), .. U32(level), 2, .. U32(6), .. U32(2200), 0, 0, 0, 0, 0, 0];

    public static Frame F(string name, byte[] body, OpcodeRegistry r) => new(r.OpcodesFor(name)[0], body);
}

public class PetParserTests
{
    private static readonly OpcodeRegistry Registry = OpcodeRegistry.LoadDefault();

    [Fact]
    public void ParsesSnapshotLikeTheRealCapture()
    {
        var body = PetBytes.Snapshot([(1002, 1), (1004, 1), (1005, 3)], [(1002, 6), (1004, 7), (1005, 0)]);
        var msg = Assert.IsType<PetCollectionSnapshotMessage>(PetMessageParser.Parse(Registry, new Frame(0x0090, body)));
        Assert.Equal(3, msg.Registered.Count);
        Assert.Equal(new PetRegistration(1005, 1005, 3), msg.Registered[2]);
        Assert.Equal(new PetProgress(1004, 7), msg.Progress[1]);
        Assert.Equal([0x06, 0xAA, 0x01], msg.Tail);
    }

    [Fact]
    public void SnapshotWithTwoByteVarintCount()
    {
        // 134 entries needs the two-byte varint 86 01.
        var progress = Enumerable.Range(0, 134).Select(i => ((uint)(1000 + i), (uint)1)).ToArray();
        var msg = PetMessageParser.ParseSnapshot(PetBytes.Snapshot([], progress));
        Assert.Equal(134, msg.Progress.Count);
    }

    [Fact]
    public void TruncatedSnapshotIsRejected()
    {
        var body = PetBytes.Snapshot([(1002, 1)], []);
        Assert.Throws<InvalidDataException>(() => PetMessageParser.ParseSnapshot(body.AsSpan(0, 10)));
        Assert.False(PetMessageParser.TryParse(Registry, new Frame(0x0090, body[..10]), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ParsesSoulsGainedAndConsumed()
    {
        var gained = Assert.IsType<PetSoulsGainedMessage>(PetMessageParser.Parse(Registry, new Frame(0x0D90, Convert.FromHexString("01AB04000001000000"))));
        Assert.Equal(new PetAmount(1195, 1), Assert.Single(gained.Entries));
        var consumed = Assert.IsType<PetSoulsConsumedMessage>(PetMessageParser.Parse(Registry, new Frame(0x0E90, Convert.FromHexString("01AB04000005000000"))));
        Assert.Equal(new PetAmount(1195, 5), Assert.Single(consumed.Entries));
    }

    [Fact]
    public void ParsesBothLevelChangeOpcodes()
    {
        var reg = Assert.IsType<PetLevelChangedMessage>(PetMessageParser.Parse(Registry,
            new Frame(0x0990, Convert.FromHexString("9209AB04000001000000020600000098080000000000000000"))));
        Assert.Equal((ushort)0x0992, reg.Header);
        Assert.Equal(1195u, reg.PetId);
        Assert.Equal(1u, reg.NewLevel);
        Assert.Equal(15, reg.Extra.Length);

        var insight = Assert.IsType<PetLevelChangedMessage>(PetMessageParser.Parse(Registry,
            new Frame(0x1490, Convert.FromHexString("0000B7040000030000000406000000B8240000" + "00000000"))));
        Assert.Equal(1207u, insight.PetId);
        Assert.Equal(3u, insight.NewLevel);
    }

    [Fact]
    public void UnrelatedOpcodesAreIgnored() =>
        Assert.Null(PetMessageParser.Parse(Registry, new Frame(0x3336, [1, 2, 3])));
}

public class PetCollectionTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double s) => T0.AddSeconds(s);

    private static PetCollectionSnapshotMessage Snap((uint, uint)[] reg, (uint, uint)[] prog) =>
        PetMessageParser.ParseSnapshot(PetBytes.Snapshot(reg, prog));

    private static PetSoulsGainedMessage Gain(uint id, uint n) => new([new PetAmount(id, n)]);
    private static PetLevelChangedMessage Level(uint id, uint level) => new(0x0992, id, level, []);
    private static PetSoulsConsumedMessage Consumed(uint id, uint n) => new([new PetAmount(id, n)]);

    [Fact]
    public void SnapshotSemantics()
    {
        var c = new PetCollection();
        var events = c.Apply(Snap([(1002, 1), (1004, 1), (1005, 3)], [(1002, 6), (1004, 7), (1005, 0), (1195, 4), (1091, 4)]), T0);

        var loaded = Assert.IsType<SnapshotLoaded>(Assert.Single(events));
        Assert.Equal(3, loaded.Count);
        Assert.Equal(5, loaded.TotalKnown);
        Assert.Equal(new PetState(1002, 1, 6, true, T0, false), c.Get(1002));
        Assert.Equal(3, c.Get(1005)!.Level);
        var unregistered = c.Get(1195)!;
        Assert.False(unregistered.Registered);
        Assert.Equal(4, unregistered.Progress);

        var summary = CollectionSummary.From(c.Pets);
        Assert.Equal("3 registered, L1 2 / L2 0 / L3 1, unregistered 1091:4 1195:4", summary.ToString());
    }

    [Fact]
    public void ListOnePetMissingFromListTwoHasZeroProgress()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 2)], []), T0);
        Assert.Equal(0, c.Get(1002)!.Progress);
    }

    [Fact]
    public void SnapshotReplacesStoredState()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 1)], [(1002, 3), (1195, 4)]), T0);
        c.Apply(Gain(1195, 1), At(1));
        c.Apply(Snap([(1004, 2)], []), At(100));
        Assert.Null(c.Get(1002));
        Assert.Null(c.Get(1195));
        Assert.Single(c.Pets);
    }

    [Fact]
    public void SoulGainedEventCarriesProgressAndThreshold()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 1)], [(1002, 3)]), T0);
        var g = Assert.IsType<SoulGained>(Assert.Single(c.Apply(Gain(1002, 2), At(5))));
        Assert.Equal(new SoulGained(At(5), 1002, 2, 5, 25), g);
    }

    [Fact]
    public void R3SequenceRegistersThenCountsTheNextSoul()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 1)], [(1195, 4)]), T0);

        var all = new List<DomainEvent>();
        all.AddRange(c.Apply(Gain(1195, 1), At(29.829)));
        all.AddRange(c.Apply(Level(1195, 1), At(29.927)));
        all.AddRange(c.Apply(Consumed(1195, 5), At(29.927)));
        all.AddRange(c.Apply(Gain(1195, 1), At(69.777)));
        all.AddRange(c.Flush());

        var pet = c.Get(1195)!;
        Assert.Equal(1, pet.Level);
        Assert.Equal(1, pet.Progress);
        Assert.True(pet.Registered);
        Assert.False(pet.NeedsResync);
        Assert.DoesNotContain(all, e => e is Inconsistency { NeedsResync: true });
        Assert.Contains(all, e => e is PetRegistered { PetId: 1195 });
        Assert.Equal(new SoulGained(At(69.777), 1195, 1, 1, 25), all.OfType<SoulGained>().Last());
    }

    [Fact]
    public void R4SequenceLevelsToMaxAndConsumesSeventyFive()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1207, 2)], [(1207, 74)]), T0);
        c.Apply(Gain(1207, 1), At(10));
        c.Apply(Level(1207, 3), At(10.1));
        c.Apply(Consumed(1207, 75), At(10.1));
        var events = c.Flush();

        var pet = c.Get(1207)!;
        Assert.Equal(3, pet.Level);
        Assert.Equal(0, pet.Progress);
        Assert.Null(c.Rules.ThresholdFor(pet.Level));
        Assert.DoesNotContain(events, e => e is Inconsistency);
    }

    [Fact]
    public void OutOfOrderBurstGivesTheSameResult()
    {
        // 0E90 and 0990 first, 0D90 last: progress dips below zero for a moment but settles to 1... 4 + 1 - 5 = 0, then +1 later.
        var c = new PetCollection();
        c.Apply(Snap([], [(1195, 4)]), T0);
        c.Apply(Consumed(1195, 5), At(10.0));
        c.Apply(Level(1195, 1), At(10.1));
        c.Apply(Gain(1195, 1), At(10.2));
        var events = c.Flush();

        Assert.Equal(1, c.Get(1195)!.Level);
        Assert.Equal(0, c.Get(1195)!.Progress);
        Assert.DoesNotContain(events, e => e is Inconsistency);
    }

    [Fact]
    public void MissingConsumedMessageFallsBackToTheThreshold()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 1)], [(1002, 24)]), T0);
        c.Apply(Gain(1002, 2), At(1));
        c.Apply(Level(1002, 2), At(1.1));
        c.Tick(At(5));
        Assert.Equal(1, c.Get(1002)!.Progress); // 26 - 25
        Assert.Equal(2, c.Get(1002)!.Level);
        Assert.False(c.Get(1002)!.NeedsResync);
    }

    [Fact]
    public void ShopPurchaseOfNewPetRegistersWithoutConsuming()
    {
        // shop-pet.a2cap M4: buying Drana Swarm (not owned) sends only 09 90, with no 0D 90 and no 0E 90.
        var c = new PetCollection();
        c.Apply(Snap([(1098, 1)], [(1098, 7)]), T0);
        var all = new List<DomainEvent>();
        all.AddRange(c.Apply(Level(1129, 1), At(71.901)));
        all.AddRange(c.Flush());

        var pet = c.Get(1129)!;
        Assert.Equal(1, pet.Level);
        Assert.Equal(0, pet.Progress);
        Assert.False(pet.NeedsResync);
        Assert.DoesNotContain(all, e => e is Inconsistency);
        Assert.Contains(all, e => e is PetRegistered { PetId: 1129 });
        Assert.Contains(all, e => e is PetAcquired { PetId: 1129, Level: 1 });
    }

    [Fact]
    public void ShopPurchaseOfOwnedPetAddsFiveSouls()
    {
        // shop-pet.a2cap M3: buying Modified Karnif again sends 0D 90 with +5 souls.
        var c = new PetCollection();
        c.Apply(Snap([(1098, 1)], [(1098, 7)]), T0);
        c.Apply(Gain(1098, 5), At(51.101));
        var events = c.Flush();

        Assert.Equal(12, c.Get(1098)!.Progress);
        Assert.Equal(1, c.Get(1098)!.Level);
        Assert.DoesNotContain(events, e => e is Inconsistency);
    }

    [Fact]
    public void ThresholdModeIgnoresTheReportedValue()
    {
        var c = new PetCollection(new PetRules { ConsumeMode = ConsumeMode.Threshold });
        c.Apply(Snap([], [(1195, 5)]), T0);
        c.Apply(Level(1195, 1), At(1));
        var events = c.Apply(Consumed(1195, 3), At(1));
        Assert.Equal(0, c.Get(1195)!.Progress);
        Assert.Contains(events, e => e is Inconsistency { NeedsResync: false });
    }

    [Fact]
    public void ReachingTheThresholdWithoutLevelUpFlagsResync()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 1)], [(1002, 24)]), T0);
        c.Apply(Gain(1002, 1), At(1));
        var events = c.Tick(At(5));
        var flag = Assert.IsType<Inconsistency>(Assert.Single(events));
        Assert.True(flag.NeedsResync);
        Assert.True(c.Get(1002)!.NeedsResync);

        // A snapshot clears the flag.
        c.Apply(Snap([(1002, 2)], []), At(100));
        Assert.False(c.Get(1002)!.NeedsResync);
    }

    [Fact]
    public void NegativeProgressIsFlooredAndFlagged()
    {
        var c = new PetCollection();
        c.Apply(Snap([], [(1195, 1)]), T0);
        c.Apply(Level(1195, 1), At(1));
        c.Apply(Consumed(1195, 5), At(1));
        var events = c.Flush();
        Assert.Equal(0, c.Get(1195)!.Progress);
        Assert.Contains(events, e => e is Inconsistency { NeedsResync: true } i && i.Message.Contains("negative"));
    }

    [Fact]
    public void LevelUpOfRegisteredPetRaisesPetLevelUp()
    {
        var c = new PetCollection();
        c.Apply(Snap([(1002, 1)], [(1002, 25)]), T0);
        var events = c.Apply(Level(1002, 2), At(1));
        Assert.Equal(new PetLevelUp(At(1), 1002, 1, 2), Assert.Single(events));
    }

    [Fact]
    public void RestoredStateKeepsWorking()
    {
        var c = new PetCollection();
        c.Restore([new PetState(1195, 1, 1, true, T0, false)]);
        c.Apply(Gain(1195, 1), At(1));
        Assert.Equal(2, c.Get(1195)!.Progress);
    }

    [Fact]
    public void SessionStatsCountSoulsAndRate()
    {
        var stats = new SessionStats(T0);
        stats.Observe(new SoulGained(T0, 1195, 2, 2, 5));
        stats.Observe(new SoulGained(T0, 1002, 1, 1, 25));
        stats.Observe(new PetRegistered(T0, 1195));
        Assert.Equal(3, stats.SoulsThisSession);
        Assert.Equal(2, stats.GainsFor(1195));
        Assert.Equal(6, stats.SoulsPerHour(T0.AddMinutes(30)), 3);
    }

    [Fact]
    public void StatusPill()
    {
        TrackerStatus S(bool npcap = false, bool desync = false, long hb = 0, int conns = 0, double firstAgo = 0, DateTime? snap = null, bool pet = false) =>
            TrackerStatus.Evaluate(new TrackerStatusInput(npcap, desync, hb, conns, hb > 0 ? T0.AddSeconds(-firstAgo) : null, snap, pet, T0));

        Assert.Equal(TrackerStatusKind.NoNpcap, S(npcap: true).Kind);
        Assert.Equal(TrackerStatusKind.WaitingForGame, S().Kind);
        Assert.Equal(TrackerStatusKind.Connected, S(hb: 5, firstAgo: 10).Kind);
        Assert.Equal(TrackerStatusKind.UnknownVersion, S(hb: 500, firstAgo: 90).Kind);
        Assert.Equal(TrackerStatusKind.Connected, S(hb: 500, firstAgo: 90, pet: true).Kind);
        Assert.Equal(TrackerStatusKind.SnapshotLoaded, S(hb: 500, firstAgo: 90, snap: T0).Kind);
        Assert.Equal(TrackerStatusKind.Desync, S(hb: 5, desync: true).Kind);
    }
}

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CollectionRoundTrip()
    {
        var paths = AppPaths.For(_dir);
        var c = new PetCollection();
        c.Apply(PetMessageParser.ParseSnapshot(PetBytes.Snapshot([(1002, 2)], [(1002, 40), (1195, 4)])), new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc));

        var store = new CollectionStore(paths.Collection);
        store.Save(c);
        store.Save(c); // overwrite goes through the atomic move
        Assert.False(File.Exists(paths.Collection + ".tmp"));

        var restored = new PetCollection();
        store.LoadInto(restored);
        Assert.Equal(c.Pets, restored.Pets);
    }

    [Fact]
    public void MissingOrCorruptCollectionLoadsEmpty()
    {
        var path = Path.Combine(_dir, "collection.json");
        Assert.Empty(new CollectionStore(path).Load());
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new CollectionStore(path).Load());
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var store = new SettingsStore(AppPaths.For(_dir).Settings);
        Assert.Equal(0.92, store.Load().Opacity);
        var s = new AppSettings { Opacity = 0.5, Scale = 1.25, ClickThrough = true, PinnedPets = [1195, 1207], AdapterOverride = "Ethernet", OverlayLeft = 10, OverlayTop = 20 };
        s.Hotkeys.ToggleOverlay = "Ctrl+Alt+O";
        store.Save(s);

        var back = store.Load();
        Assert.Equal(0.5, back.Opacity);
        Assert.Equal(1.25, back.Scale);
        Assert.True(back.ClickThrough);
        Assert.Equal([1195, 1207], back.PinnedPets);
        Assert.Equal("Ethernet", back.AdapterOverride);
        Assert.Equal("Ctrl+Alt+O", back.Hotkeys.ToggleOverlay);
        Assert.Equal("Ctrl+Shift+L", back.Hotkeys.ToggleClickThrough);
    }

    [Fact]
    public void EventLogAppendsLines()
    {
        var log = new EventLog(AppPaths.For(_dir).Events);
        var t = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        log.Append(new SoulGained(t, 1195, 1, 5, 5));
        log.Append([new PetRegistered(t, 1195), new Inconsistency(t, 1195, "x", true)]);

        Assert.Equal(3, File.ReadAllLines(log.Path).Length);
        var entries = log.ReadAll();
        Assert.Equal(["SoulGained", "PetRegistered", "Inconsistency"], entries.Select(e => e.Type));
        Assert.Equal(5, entries[0].Progress);
        Assert.True(entries[2].NeedsResync);
    }

    [Fact]
    public void CatalogMergesBaseWithOverrides()
    {
        var paths = AppPaths.For(_dir);
        Directory.CreateDirectory(_dir);
        var basePath = Path.Combine(_dir, "pets.json");
        File.WriteAllText(basePath, """[{"id":1195,"name":"Dracuni Herbalist","genus":"Cogni"},{"id":1002,"name":"Base Name","genus":"Fera"}]""");
        File.WriteAllText(paths.PetNames, """{"1002":"My Name","1207":{"name":"Agrint","genus":"Natura"}}""");

        var catalog = PetCatalog.Load(basePath, paths.PetNames);
        Assert.Equal("Dracuni Herbalist", catalog.NameOf(1195));
        Assert.Equal("My Name", catalog.NameOf(1002));
        Assert.Equal("Fera", catalog.GenusOf(1002));
        Assert.Equal("Natura", catalog.GenusOf(1207));
        Assert.Equal("Pet #1300", catalog.DisplayName(1300));

        catalog.SetName(1300, "Fresh");
        var reloaded = PetCatalog.Load(basePath, paths.PetNames);
        Assert.Equal("Fresh", reloaded.NameOf(1300));
        Assert.Equal("Natura", reloaded.GenusOf(1207));

        reloaded.SetName(1300, "");
        Assert.Null(PetCatalog.Load(basePath, paths.PetNames).NameOf(1300));
    }

    [Fact]
    public void NameListParsesAndSuggests()
    {
        var list = PetNameList.Parse(["# comment", "Agrint\tNatura", "Thin Agrint\tNatura", "Black Smoke Molt\t", ""]);
        Assert.Equal(3, list.Entries.Count);
        Assert.Null(list.Entries[2].Genus);
        Assert.Equal(["Agrint", "Thin Agrint"], list.Suggest("agr").Select(e => e.Name));
    }

    [Fact]
    public void ShippedFilesAreValid()
    {
        var catalog = PetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "pets.json"), null);
        Assert.Equal("Dracuni Herbalist", catalog.NameOf(1195));
        Assert.Equal("Cogni", catalog.GenusOf(1195));
        Assert.True(PetNameList.LoadDefault().Entries.Count > 50);
    }
}
