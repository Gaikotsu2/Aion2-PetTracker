using System.Text.Json;
using System.Text.Json.Serialization;
using PetTracker.Core.Domain;
using PetTracker.Core.Messages;
using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Core.Verification;

public sealed class ExpectedCount
{
    public int Registered { get; set; }
    public int Total { get; set; }
    public override string ToString() => $"{Registered}/{Total}";
}

/// <summary>Expected state of one pet. Level 0 = unregistered. MAX is level 3 with progress 0. Omitted fields are not checked.</summary>
public sealed class ExpectedPet
{
    public int? Level { get; set; }
    public int? Progress { get; set; }
}

/// <summary>What the collection should look like at some point. Everything is optional.</summary>
public class ExpectedState
{
    /// <summary>The count as the game shows it, for example 161 of 200 (Special pets excluded).</summary>
    public ExpectedCount? GameCount { get; set; }
    public ExpectedCount? Special { get; set; }
    /// <summary>Pet id to expected level/progress; only the listed ids are checked.</summary>
    public Dictionary<string, ExpectedPet>? Pets { get; set; }
}

/// <summary>The state after the first <see cref="After"/> captures (1-based) have been replayed and flushed.</summary>
public sealed class ExpectedCheckpoint : ExpectedState
{
    public int After { get; set; }
}

/// <summary>An expectation file: see docs/expect.example.json.</summary>
public sealed class ReplayExpectation
{
    /// <summary>Capture file names (resolved next to the expectation file). Optional for the CLI, which takes them as arguments.</summary>
    public List<string>? Captures { get; set; }
    public ExpectedState? Final { get; set; }
    public List<ExpectedCheckpoint>? Checkpoints { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ReplayExpectation Parse(string json) =>
        JsonSerializer.Deserialize<ReplayExpectation>(json, Options) ?? throw new InvalidDataException("Expectation file is empty.");

    public static ReplayExpectation Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public sealed record ReplayResult(IReadOnlyList<PetState> Pets, GameCollectionCount GameCount);

/// <summary>Replays decoded captures through the registry, parser and collection, exactly as the app does, and checks the result.</summary>
public static class ReplayVerifier
{
    /// <summary>Decodes the S2C frames of an .a2cap file in order, with the time each chunk arrived.</summary>
    public static IReadOnlyList<ObservedFrame> LoadFrames(string path, FrameDecoderOptions? options = null)
    {
        using var reader = A2CapReader.Open(path);
        var processor = new StreamProcessor(options);
        var frames = new List<ObservedFrame>();
        foreach (var record in reader.ReadAll())
        {
            if (processor.Process(record) is { } decoded)
            {
                foreach (var frame in decoded.Frames)
                    frames.Add(new ObservedFrame(decoded.Ticks, frame));
            }
        }
        return frames;
    }

    public static ReplayResult Replay(IEnumerable<IReadOnlyList<ObservedFrame>> captures, OpcodeRegistry registry, PetCatalog catalog, PetRules? rules = null)
    {
        var collection = new PetCollection(rules);
        foreach (var capture in captures)
        {
            foreach (var f in capture)
            {
                var now = new DateTime(f.Ticks, DateTimeKind.Utc);
                foreach (var message in PetMessageParser.Extract(registry, [f.Frame]))
                    collection.Apply(message, now);
                collection.Tick(now);
            }
        }
        collection.Flush();
        return new ReplayResult(collection.Pets, catalog.GameCount(collection.Pets));
    }

    /// <summary>Mismatches between <paramref name="expected"/> and the result; empty when everything matches.</summary>
    public static List<string> Compare(ReplayResult actual, ExpectedState expected, string label = "final")
    {
        var problems = new List<string>();
        if (expected.GameCount is { } gc && (gc.Registered != actual.GameCount.Registered || gc.Total != actual.GameCount.Total))
            problems.Add($"{label}: game count expected {gc}, got {actual.GameCount.CollectionText}");
        if (expected.Special is { } sp && (sp.Registered != actual.GameCount.SpecialRegistered || sp.Total != actual.GameCount.SpecialTotal))
            problems.Add($"{label}: Special count expected {sp}, got {actual.GameCount.SpecialText}");

        var byId = actual.Pets.ToDictionary(p => p.Id);
        foreach (var (key, pet) in expected.Pets ?? [])
        {
            if (!int.TryParse(key, out var id))
            {
                problems.Add($"{label}: \"{key}\" is not a pet id");
                continue;
            }
            byId.TryGetValue(id, out var state);
            var level = state?.Level ?? 0;
            var progress = state?.Progress ?? 0;
            if (pet.Level is { } l && l != level)
                problems.Add($"{label}: pet {id} level expected {l}, got {level}");
            if (pet.Progress is { } p && p != progress)
                problems.Add($"{label}: pet {id} progress expected {p}, got {progress}");
        }
        return problems;
    }

    /// <summary>Runs every checkpoint (each replays the first k captures again) and then the final expectations.</summary>
    public static List<string> Verify(ReplayExpectation expectation, IReadOnlyList<IReadOnlyList<ObservedFrame>> captures,
        OpcodeRegistry registry, PetCatalog catalog, PetRules? rules = null)
    {
        var problems = new List<string>();
        foreach (var checkpoint in expectation.Checkpoints ?? [])
        {
            if (checkpoint.After < 1 || checkpoint.After > captures.Count)
            {
                problems.Add($"checkpoint after {checkpoint.After}: only {captures.Count} capture(s) given");
                continue;
            }
            problems.AddRange(Compare(Replay(captures.Take(checkpoint.After), registry, catalog, rules), checkpoint, $"after {checkpoint.After} capture(s)"));
        }
        if (expectation.Final is { } final)
            problems.AddRange(Compare(Replay(captures, registry, catalog, rules), final));
        return problems;
    }

    /// <summary>Builds an expectation from what the replay produces now: every pet, and the game count after each capture.</summary>
    public static ReplayExpectation Record(IReadOnlyList<string> captureNames, IReadOnlyList<IReadOnlyList<ObservedFrame>> captures,
        OpcodeRegistry registry, PetCatalog catalog, PetRules? rules = null)
    {
        static ExpectedCount Count(int r, int t) => new() { Registered = r, Total = t };
        var expectation = new ReplayExpectation { Captures = captureNames.ToList(), Checkpoints = [] };
        for (var k = 1; k < captures.Count; k++)
        {
            var partial = Replay(captures.Take(k), registry, catalog, rules);
            expectation.Checkpoints.Add(new ExpectedCheckpoint
            {
                After = k,
                GameCount = Count(partial.GameCount.Registered, partial.GameCount.Total),
                Special = Count(partial.GameCount.SpecialRegistered, partial.GameCount.SpecialTotal),
            });
        }
        var final = Replay(captures, registry, catalog, rules);
        expectation.Final = new ExpectedState
        {
            GameCount = Count(final.GameCount.Registered, final.GameCount.Total),
            Special = Count(final.GameCount.SpecialRegistered, final.GameCount.SpecialTotal),
            Pets = final.Pets.ToDictionary(p => p.Id.ToString(), p => new ExpectedPet { Level = p.Level, Progress = p.Progress }),
        };
        return expectation;
    }
}
