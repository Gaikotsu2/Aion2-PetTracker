using System.Text.Json;
using System.Text.Json.Nodes;
using PetTracker.Core.Persistence;

namespace PetTracker.Core.Domain;

public sealed record PetInfo(int Id, string? Name = null, string? Genus = null, string? Notes = null);

/// <summary>
/// Pet names and genera. data/pets.json is the shipped base; %AppData%\Aion2PetTracker\pet-names.json holds the user's own
/// names, which win over the base. An override is either a plain string (the name) or an object with name/genus/notes.
/// </summary>
public sealed class PetCatalog
{
    private readonly Dictionary<int, PetInfo> _base = [];
    private readonly Dictionary<int, PetInfo> _overrides = [];
    private readonly Dictionary<int, string> _sources = []; // base only: maintained by hand in data/pets.json
    private readonly string? _overridesPath;

    public PetCatalog(string? overridesPath = null) => _overridesPath = overridesPath;

    public event Action? Changed;

    public static PetCatalog Load(string? basePath, string? overridesPath)
    {
        var catalog = new PetCatalog(overridesPath);
        if (basePath is not null && File.Exists(basePath))
            catalog.LoadBase(File.ReadAllText(basePath));
        if (overridesPath is not null && File.Exists(overridesPath))
            catalog.LoadOverrides(File.ReadAllText(overridesPath));
        return catalog;
    }

    public static PetCatalog LoadDefault(string? dataFolder = null) =>
        Load(Path.Combine(AppContext.BaseDirectory, "data", "pets.json"), AppPaths.For(dataFolder).PetNames);

    public void LoadBase(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        // Either a plain array, or an object wrapper { "meta": ..., "pets": [...] } (unknown top-level keys are ignored).
        foreach (var node in root as JsonArray ?? (root as JsonObject)?["pets"] as JsonArray ?? [])
        {
            if (node is JsonObject o && o["id"] is { } id)
            {
                _base[id.GetValue<int>()] = new PetInfo(id.GetValue<int>(), Str(o["name"]), Str(o["genus"]), Str(o["notes"]));
                if (PetSource.Normalize(Str(o["source"])) is { } source)
                    _sources[id.GetValue<int>()] = source;
            }
        }
    }

    public void LoadOverrides(string json)
    {
        _overrides.Clear();
        if (JsonNode.Parse(json) is not JsonObject root)
            return;
        foreach (var (key, value) in root)
        {
            if (!int.TryParse(key, out var id))
                continue;
            if (value is JsonObject o)
                _overrides[id] = new PetInfo(id, Str(o["name"]), Str(o["genus"]), Str(o["notes"]));
            else if (Str(value) is { } name)
                _overrides[id] = new PetInfo(id, name);
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public IEnumerable<int> KnownIds => _base.Keys.Union(_overrides.Keys).Order();

    /// <summary>Base merged with the user's overrides (override fields win where set).</summary>
    public PetInfo? Get(int id)
    {
        _base.TryGetValue(id, out var b);
        _overrides.TryGetValue(id, out var o);
        if (b is null && o is null)
            return null;
        return new PetInfo(id, o?.Name ?? b?.Name, o?.Genus ?? b?.Genus, o?.Notes ?? b?.Notes);
    }

    public string? NameOf(int id) => Get(id)?.Name;

    public string? GenusOf(int id) => Get(id)?.Genus;

    /// <summary>The name, or "Pet #id" when it has none.</summary>
    public string DisplayName(int id) => NameOf(id) ?? $"Pet #{id}";

    public bool HasName(int id) => NameOf(id) is not null;

    /// <summary>True for Special pets (cash shop / events / other non-gameplay sources).</summary>
    public bool IsSpecial(int id) => PetGenus.IsSpecial(GenusOf(id));

    /// <summary>"Dungeon" or "Shop" for pets limited to one place (the "source" field in data/pets.json); null otherwise.</summary>
    public string? SourceOf(int id) => _sources.GetValueOrDefault(id);

    public bool IsDungeon(int id) => PetSource.IsDungeon(SourceOf(id));

    public bool IsShop(int id) => PetSource.IsShop(SourceOf(id));

    /// <summary>Special pets are not part of the game's "Collection Status x/200".</summary>
    public bool CountsTowardCollection(int id) => PetGenus.CountsTowardCollection(GenusOf(id));

    /// <summary>
    /// The game-style count: registered non-Special pets out of all non-Special pets (catalog plus anything in the collection),
    /// and the Special pets separately.
    /// </summary>
    public GameCollectionCount GameCount(IEnumerable<PetState> pets)
    {
        var states = pets.ToDictionary(p => p.Id);
        int registered = 0, total = 0, specialRegistered = 0, specialTotal = 0;
        foreach (var id in KnownIds.Concat(states.Keys).Distinct())
        {
            var isRegistered = states.TryGetValue(id, out var s) && s.Level >= 1;
            if (CountsTowardCollection(id))
            {
                total++;
                if (isRegistered) registered++;
            }
            else
            {
                specialTotal++;
                if (isRegistered) specialRegistered++;
            }
        }
        return new GameCollectionCount(registered, total, specialRegistered, specialTotal);
    }

    /// <summary>Sets (or with an empty name, clears) the user's name for a pet and saves pet-names.json.</summary>
    public void SetName(int id, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name is null)
        {
            if (_overrides.TryGetValue(id, out var existing) && existing.Genus is not null)
                _overrides[id] = existing with { Name = null };
            else
                _overrides.Remove(id);
        }
        else
        {
            _overrides[id] = _overrides.TryGetValue(id, out var existing) ? existing with { Name = name } : new PetInfo(id, name);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>The user's own entries from pet-names.json.</summary>
    public IReadOnlyList<PetInfo> Overrides => _overrides.Values.OrderBy(p => p.Id).ToList();

    /// <summary>Replaces all of the user's own entries (restoring a backup) and saves pet-names.json.</summary>
    public void ReplaceOverrides(IEnumerable<PetInfo> overrides)
    {
        _overrides.Clear();
        foreach (var info in overrides)
        {
            var clean = new PetInfo(info.Id, Clean(info.Name), Clean(info.Genus), Clean(info.Notes));
            if (clean.Name is not null || clean.Genus is not null || clean.Notes is not null)
                _overrides[info.Id] = clean;
        }
        Save();
        Changed?.Invoke();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void Save()
    {
        if (_overridesPath is null)
            return;
        var root = new JsonObject();
        foreach (var (id, info) in _overrides.OrderBy(kv => kv.Key))
        {
            if (info.Genus is null && info.Notes is null)
            {
                if (info.Name is not null)
                    root[id.ToString()] = info.Name;
            }
            else
            {
                root[id.ToString()] = new JsonObject { ["name"] = info.Name, ["genus"] = info.Genus, ["notes"] = info.Notes };
            }
        }
        AtomicFile.WriteAllText(_overridesPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Autocomplete source: data/pet-names.txt, one "name&lt;TAB&gt;genus" per line (genus optional, # starts a comment).</summary>
public sealed class PetNameList
{
    public sealed record Entry(string Name, string? Genus);

    public IReadOnlyList<Entry> Entries { get; }

    public PetNameList(IEnumerable<Entry> entries) => Entries = entries.ToList();

    public static PetNameList Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return new PetNameList([]);
        return Parse(File.ReadAllLines(path));
    }

    public static PetNameList LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "data", "pet-names.txt"));

    public static PetNameList Parse(IEnumerable<string> lines)
    {
        var entries = new List<Entry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split('\t');
            var name = parts[0].Trim();
            var genus = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
            if (name.Length > 0)
                entries.Add(new Entry(name, genus));
        }
        return new PetNameList(entries);
    }

    public string? GenusFor(string? name) =>
        name is null ? null : Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.Genus;

    /// <summary>Names starting with the text first, then names containing it.</summary>
    public IReadOnlyList<Entry> Suggest(string text, int max = 8)
    {
        text = text.Trim();
        if (text.Length == 0)
            return [];
        return Entries
            .Where(e => e.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }
}
