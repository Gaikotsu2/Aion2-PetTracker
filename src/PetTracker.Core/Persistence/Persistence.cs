using System.Text.Json;
using System.Text.Json.Serialization;
using PetTracker.Core.Domain;

namespace PetTracker.Core.Persistence;

/// <summary>Where the app keeps its files: %AppData%\Aion2PetTracker unless a folder is given (tests).</summary>
public sealed record AppPaths(string Folder)
{
    public string Collection => Path.Combine(Folder, "collection.json");
    public string Events => Path.Combine(Folder, "events.jsonl");
    public string Settings => Path.Combine(Folder, "settings.json");
    public string PetNames => Path.Combine(Folder, "pet-names.json");
    /// <summary>User opcode override, merged over data/opcodes.default.json.</summary>
    public string Opcodes => Path.Combine(Folder, "opcodes.json");
    /// <summary>Automatic backups written before an import or a restore replaces the collection.</summary>
    public string Backups => Path.Combine(Folder, "backups");

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2PetTracker");

    public static AppPaths For(string? folder = null) => new(folder ?? DefaultFolder);
}

public static class AtomicFile
{
    /// <summary>Writes to a temp file next to the target, then moves it over the target, so a crash never leaves half a file.</summary>
    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>collection.json: the last known state of every pet, written atomically.</summary>
public sealed class CollectionStore(string path)
{
    public sealed record Stored(int Version, DateTime SavedAt, DateTime? SnapshotAt, uint? SnapshotHeader, string? SnapshotTailBase64, List<StoredPet> Pets);

    public sealed record StoredPet(int Id, int Level, int Progress, bool Registered, DateTime LastUpdated, bool NeedsResync);

    public string Path { get; } = path;

    public void Save(PetCollection collection, DateTime? savedAt = null)
    {
        var pets = collection.Pets.Select(p => new StoredPet(p.Id, p.Level, p.Progress, p.Registered, p.LastUpdated, p.NeedsResync)).ToList();
        var snapshot = collection.LastSnapshot;
        var stored = new Stored(1, savedAt ?? DateTime.UtcNow, collection.SnapshotAt, snapshot?.Unknown,
            snapshot is null ? null : Convert.ToBase64String(snapshot.Tail), pets);
        AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(stored, Json.Options));
    }

    /// <summary>The stored pets, or an empty list when there is no file or it is unreadable.</summary>
    public IReadOnlyList<PetState> Load()
    {
        if (!File.Exists(Path))
            return [];
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path), Json.Options);
            return stored?.Pets.Select(p => new PetState(p.Id, p.Level, p.Progress, p.Level > 0, p.LastUpdated, p.NeedsResync)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void LoadInto(PetCollection collection) => collection.Restore(Load());
}

/// <summary>events.jsonl: one JSON line per domain event, append-only (history and souls per hour).</summary>
public sealed class EventLog(string path)
{
    public sealed record Entry(DateTime T, string Type, int? PetId = null, int? Amount = null, int? Progress = null,
        int? Threshold = null, int? Level = null, int? OldLevel = null, int? Count = null, int? Total = null,
        string? Message = null, bool? NeedsResync = null);

    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public static Entry ToEntry(DomainEvent e) => e switch
    {
        SoulGained g => new Entry(g.Timestamp, nameof(SoulGained), g.PetId, g.Amount, g.NewProgress, g.Threshold),
        PetRegistered r => new Entry(r.Timestamp, nameof(PetRegistered), r.PetId),
        PetLevelUp l => new Entry(l.Timestamp, nameof(PetLevelUp), l.PetId, Level: l.NewLevel, OldLevel: l.OldLevel),
        PetAcquired a => new Entry(a.Timestamp, nameof(PetAcquired), a.PetId, Level: a.Level),
        ProgressAdjusted p => new Entry(p.Timestamp, nameof(ProgressAdjusted), p.PetId, Progress: p.NewProgress),
        SnapshotLoaded s => new Entry(s.Timestamp, nameof(SnapshotLoaded), Count: s.Count, Total: s.TotalKnown),
        Inconsistency i => new Entry(i.Timestamp, nameof(Inconsistency), i.PetId, Message: i.Message, NeedsResync: i.NeedsResync),
        SystemNote n => new Entry(n.Timestamp, n.Kind, Message: n.Message),
        _ => new Entry(e.Timestamp, e.GetType().Name),
    };

    public void Append(IEnumerable<DomainEvent> events)
    {
        var lines = events.Select(e => JsonSerializer.Serialize(ToEntry(e), Line)).ToList();
        if (lines.Count == 0)
            return;
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            File.AppendAllLines(Path, lines);
        }
    }

    public void Append(DomainEvent e) => Append([e]);

    public IReadOnlyList<Entry> ReadAll()
    {
        if (!File.Exists(Path))
            return [];
        lock (_gate)
        {
            return File.ReadAllLines(Path)
                .Where(l => l.Length > 0)
                .Select(l => JsonSerializer.Deserialize<Entry>(l, Line))
                .OfType<Entry>()
                .ToList();
        }
    }
}

public sealed class HotkeySettings
{
    public string ToggleOverlay { get; set; } = "Ctrl+Shift+P";
    public string ToggleClickThrough { get; set; } = "Ctrl+Shift+L";
}

public sealed class AppSettings
{
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayWidth { get; set; } = 330;
    public double OverlayHeight { get; set; } = 440;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public bool OverlayVisible { get; set; } = true;
    public bool ClickThrough { get; set; }
    public List<int> PinnedPets { get; set; } = [];
    public string? AdapterOverride { get; set; }
    public HotkeySettings Hotkeys { get; set; } = new();
    /// <summary>Targets, other and watch from the last PetSouls import, sent back on export; null until then.</summary>
    public PetSoulsExtras? PetSouls { get; set; }
}

public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public AppSettings Load()
    {
        if (!File.Exists(Path))
            return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json.Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) => AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(settings, Json.Options));
}
