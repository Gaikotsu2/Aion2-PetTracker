using PetTracker.Core.Domain;
using PetTracker.Core.Protocol;
using PetTracker.Core.Verification;
using Xunit.Abstractions;

namespace PetTracker.Tests.Domain;

public class ReplayVerifierTests(ITestOutputHelper output)
{
    private static readonly OpcodeRegistry Registry = OpcodeRegistry.LoadDefault();
    private static readonly long T0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static PetCatalog Catalog()
    {
        var catalog = new PetCatalog();
        var pets = Enumerable.Range(1001, 60).Select(i => $$"""{ "id": {{i}}, "name": "Pet {{i}}", "genus": "{{(i >= 1056 ? "Special" : "Fera")}}" }""");
        catalog.LoadBase("{ \"pets\": [" + string.Join(",", pets) + "] }");
        return catalog;
    }

    private static ObservedFrame F(double seconds, string name, byte[] body) =>
        new(T0 + (long)(seconds * TimeSpan.TicksPerSecond), PetBytes.F(name, body, Registry));

    /// <summary>Capture 1: snapshot (1001 L1 4/25, 1002 unregistered 4). Capture 2: 1002 gets 1 soul and registers.</summary>
    private static List<IReadOnlyList<ObservedFrame>> Captures() =>
    [
        new List<ObservedFrame>
        {
            F(1, MessageNames.PetCollectionSnapshot, PetBytes.Snapshot([(1001, 1), (1003, 2)], [(1001, 4), (1002, 4), (1003, 30)])),
        },
        new List<ObservedFrame>
        {
            F(100, MessageNames.PetSoulsGained, PetBytes.Amounts(1002, 1)),
            F(100.1, MessageNames.PetLevelChanged, PetBytes.LevelChange(0x0992, 1002, 1)),
            F(100.1, MessageNames.PetSoulsConsumed, PetBytes.Amounts(1002, 5)),
        },
    ];

    [Fact]
    public void ReplayMatchesWhatTheAppWouldShow()
    {
        var result = ReplayVerifier.Replay(Captures(), Registry, Catalog());
        Assert.Equal(new GameCollectionCount(3, 55, 0, 5), result.GameCount);
        var pet = result.Pets.Single(p => p.Id == 1002);
        Assert.Equal(1, pet.Level);
        Assert.Equal(0, pet.Progress);
    }

    [Fact]
    public void ExpectationFileMatchesAfterEachCapture()
    {
        var expectation = ReplayExpectation.Parse("""
            {
              "checkpoints": [ { "after": 1, "gameCount": { "registered": 2, "total": 55 } } ],
              "final": {
                "gameCount": { "registered": 3, "total": 55 },
                "special": { "registered": 0, "total": 5 },
                "pets": {
                  "1001": { "level": 1, "progress": 4 },
                  "1002": { "level": 1, "progress": 0 },
                  "1003": { "level": 2, "progress": 30 }
                }
              }
            }
            """);
        Assert.Empty(ReplayVerifier.Verify(expectation, Captures(), Registry, Catalog()));
    }

    [Fact]
    public void MismatchesAreReportedPerField()
    {
        var expectation = ReplayExpectation.Parse("""
            {
              "checkpoints": [ { "after": 1, "gameCount": { "registered": 3, "total": 55 } } ],
              "final": { "gameCount": { "registered": 9, "total": 55 }, "pets": { "1001": { "level": 2, "progress": 4 }, "1002": { "progress": 7 }, "x": {} } }
            }
            """);

        var problems = ReplayVerifier.Verify(expectation, Captures(), Registry, Catalog());

        Assert.Contains(problems, p => p.Contains("after 1 capture(s): game count expected 3/55, got 2/55"));
        Assert.Contains(problems, p => p.Contains("game count expected 9/55"));
        Assert.Contains(problems, p => p.Contains("pet 1001 level expected 2, got 1"));
        Assert.Contains(problems, p => p.Contains("pet 1002 progress expected 7, got 0"));
        Assert.Contains(problems, p => p.Contains("\"x\" is not a pet id"));
    }

    [Fact]
    public void WrongOpcodeMapFailsVerification()
    {
        var expectation = ReplayExpectation.Parse("""{ "final": { "gameCount": { "registered": 3, "total": 55 } } }""");
        var renumbered = OpcodeOverrides.Merge(Registry, new Dictionary<string, IReadOnlyList<ushort>> { [MessageNames.PetCollectionSnapshot] = [0x7777] });

        Assert.NotEmpty(ReplayVerifier.Verify(expectation, Captures(), renumbered, Catalog()));
    }

    [Fact]
    public void RecordedExpectationPassesAgainstItself()
    {
        var recorded = ReplayVerifier.Record(["a.a2cap", "b.a2cap"], Captures(), Registry, Catalog());
        Assert.Single(recorded.Checkpoints!);
        var reloaded = ReplayExpectation.Parse(recorded.ToJson());
        Assert.Equal(["a.a2cap", "b.a2cap"], reloaded.Captures);
        Assert.Empty(ReplayVerifier.Verify(reloaded, Captures(), Registry, Catalog()));
    }

    [Fact]
    public void TheCommittedExampleFileParses()
    {
        var example = ReplayExpectation.Load(Path.Combine(RepoRoot(), "docs", "expect.example.json"));
        Assert.Equal(3, example.Captures!.Count);
        Assert.Equal(162, example.Final!.GameCount!.Registered);
        Assert.Equal(3, example.Final.Pets!["1207"].Level);
        Assert.Equal(161, example.Checkpoints!.Single().GameCount!.Registered);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BannedSymbols.txt")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    /// <summary>
    /// Runs the same logic as `pt-research verify` over captures/*.a2cap with every captures/expect*.json, but only when those
    /// files exist (captures/ is gitignored: they are personal recordings). xUnit v2 cannot skip at runtime, so without them this
    /// test returns early and says so in the output.
    /// </summary>
    [Fact]
    public void PersonalCapturesMatchTheirExpectationsIfPresent()
    {
        var folder = Path.Combine(RepoRoot(), "captures");
        var expectFiles = Directory.Exists(folder) ? Directory.GetFiles(folder, "expect*.json") : [];
        if (expectFiles.Length == 0)
        {
            output.WriteLine("SKIPPED: no captures/expect*.json in this checkout (captures are personal and not in git).");
            return;
        }

        var catalog = PetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "pets.json"), null);
        var ran = 0;
        foreach (var expectFile in expectFiles)
        {
            var expectation = ReplayExpectation.Load(expectFile);
            // captures/ first, then the repo root where the recordings are usually made (both are gitignored)
            var paths = (expectation.Captures ?? []).Select(n => File.Exists(Path.Combine(folder, n)) ? Path.Combine(folder, n) : Path.Combine(RepoRoot(), n)).ToList();
            if (paths.Count == 0 || paths.Any(p => !File.Exists(p)))
            {
                output.WriteLine($"SKIPPED {Path.GetFileName(expectFile)}: capture file(s) not present in captures/ or the repo root.");
                continue;
            }

            var captures = paths.Select(p => ReplayVerifier.LoadFrames(p)).ToList();
            var problems = ReplayVerifier.Verify(expectation, captures, Registry, catalog);
            output.WriteLine($"{Path.GetFileName(expectFile)}: {paths.Count} capture(s), {problems.Count} mismatch(es)");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            ran++;
        }
        output.WriteLine(ran == 0 ? "SKIPPED: no expectation had its captures present." : $"Verified {ran} expectation file(s).");
    }
}
