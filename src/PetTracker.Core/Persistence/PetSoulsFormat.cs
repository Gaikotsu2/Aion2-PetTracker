using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PetTracker.Core.Domain;

namespace PetTracker.Core.Persistence;

/// <summary>
/// What a PetSouls.dev profile holds besides the levels: starred pets (targets), non-pet collectibles (other) and the
/// watch list. The tracker has no data for these; they are kept from the last PetSouls import so an export does not wipe them
/// (importing on the site replaces the whole profile).
/// </summary>
public sealed class PetSoulsExtras
{
    public List<int> Targets { get; set; } = [];
    public Dictionary<string, int> Other { get; set; } = [];
    public List<string> Watch { get; set; } = [];
}

/// <summary>One PetSouls.dev profile. Levels are 1..3 by pet id; unregistered pets are absent.</summary>
public sealed record PetSoulsProfile(
    IReadOnlyDictionary<int, int> Levels,
    IReadOnlyList<int> Targets,
    IReadOnlyDictionary<string, int> Other,
    IReadOnlyList<string> Watch)
{
    /// <summary>Entries dropped while parsing (bad key, level outside 1..3, wrong type).</summary>
    public int Skipped { get; init; }

    public PetSoulsExtras ToExtras() => new()
    {
        Targets = Targets.ToList(),
        Other = new Dictionary<string, int>(Other),
        Watch = Watch.ToList(),
    };
}

/// <summary>
/// The PetSouls.dev import/export code: Base64 of the UTF-8 JSON <c>{"levels":{},"targets":[],"other":{},"watch":[]}</c>
/// (the site encodes with <c>btoa(unescape(encodeURIComponent(JSON.stringify(p))))</c>). Its file import takes the same object
/// as plain JSON, wrapped as <c>{"profile":...}</c>, or its own backup <c>{"profiles":{...},"active":"..."}</c>.
/// </summary>
public static class PetSoulsFormat
{
    public const int MaxLevel = 3;

    private static readonly string[] ProfileKeys = ["levels", "targets", "other", "watch"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static PetSoulsProfile FromCollection(IEnumerable<PetState> pets, PetSoulsExtras? extras = null) => new(
        pets.Where(p => p.Level >= 1).OrderBy(p => p.Id).ToDictionary(p => p.Id, p => Math.Min(p.Level, MaxLevel)),
        extras?.Targets.Distinct().ToList() ?? [],
        extras is null ? new Dictionary<string, int>() : new Dictionary<string, int>(extras.Other),
        extras?.Watch.Distinct().ToList() ?? []);

    /// <summary>The code to paste into PetSouls.dev (Settings → Import).</summary>
    public static string ToCode(PetSoulsProfile profile) => Convert.ToBase64String(Serialize(profile, indented: false));

    /// <summary>The profile as a JSON file the site's file import accepts.</summary>
    public static string ToFileJson(PetSoulsProfile profile) => Encoding.UTF8.GetString(Serialize(profile, indented: true));

    private static byte[] Serialize(PetSoulsProfile profile, bool indented)
    {
        // Same key order and escaping as JSON.stringify, so a round trip gives the site's own code back byte for byte.
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("levels");
            foreach (var (id, level) in profile.Levels.OrderBy(kv => kv.Key))
                w.WriteNumber(id.ToString(System.Globalization.CultureInfo.InvariantCulture), level);
            w.WriteEndObject();
            w.WriteStartArray("targets");
            foreach (var id in profile.Targets)
                w.WriteNumberValue(id);
            w.WriteEndArray();
            w.WriteStartObject("other");
            foreach (var (name, level) in profile.Other)
                w.WriteNumber(name, level);
            w.WriteEndObject();
            w.WriteStartArray("watch");
            foreach (var entry in profile.Watch)
                w.WriteStringValue(entry);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Reads a PetSouls code or JSON file. Never throws; <paramref name="error"/> says what is wrong.</summary>
    public static bool TryParse(string? text, out PetSoulsProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Nothing to import: paste a PetSouls code or choose a file.";
            return false;
        }

        string json;
        if (text.StartsWith('{'))
        {
            json = text;
        }
        else if (DecodeBase64(text) is { } decoded)
        {
            json = decoded;
        }
        else
        {
            error = "This is not a PetSouls code (it is not valid Base64).";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (FindProfile(doc.RootElement) is not { } root)
            {
                error = "This does not look like a PetSouls profile (no levels, targets, other or watch).";
                return false;
            }
            profile = ReadProfile(root);
            return true;
        }
        catch (JsonException ex)
        {
            error = "The code decodes, but not to valid JSON: " + ex.Message;
            return false;
        }
    }

    private static string? DecodeBase64(string text)
    {
        var sb = new StringBuilder(text.Length + 3);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;
            sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }
        while (sb.Length % 4 != 0)
            sb.Append('=');
        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(sb.ToString()));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsProfile(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && ProfileKeys.Any(k => e.TryGetProperty(k, out _));

    /// <summary>A bare profile, <c>{"profile":...}</c>, or the site's backup (its active profile, else its only one).</summary>
    private static JsonElement? FindProfile(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.String
                && profiles.TryGetProperty(active.GetString()!, out var chosen) && IsProfile(chosen))
                return chosen;
            var all = profiles.EnumerateObject().Select(p => p.Value).Where(IsProfile).ToList();
            return all.Count >= 1 ? all[0] : null;
        }
        if (root.TryGetProperty("profile", out var wrapped) && IsProfile(wrapped))
            return wrapped;
        return IsProfile(root) ? root : null;
    }

    /// <summary>Keeps what the site itself keeps: numeric pet keys and levels 1..3 (rounded), integer targets, string watch entries.</summary>
    private static PetSoulsProfile ReadProfile(JsonElement root)
    {
        var skipped = 0;
        var levels = new SortedDictionary<int, int>();
        if (root.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in lv.EnumerateObject())
            {
                if (int.TryParse(p.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                    && Level(p.Value) is { } level)
                    levels[id] = level;
                else
                    skipped++;
            }
        }

        var targets = new List<int>();
        if (root.TryGetProperty("targets", out var tg) && tg.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tg.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var id))
                {
                    if (!targets.Contains(id))
                        targets.Add(id);
                }
                else
                {
                    skipped++;
                }
            }
        }

        var other = new Dictionary<string, int>();
        if (root.TryGetProperty("other", out var ot))
        {
            if (ot.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ot.EnumerateObject())
                {
                    if (Level(p.Value) is { } level)
                        other[p.Name] = level;
                    else
                        skipped++;
                }
            }
            else if (ot.ValueKind == JsonValueKind.Array)
            {
                // Older site versions stored "other" as a list of names (level 1).
                foreach (var name in ot.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!))
                    other[name] = 1;
            }
        }

        var watch = new List<string>();
        if (root.TryGetProperty("watch", out var wt) && wt.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wt.EnumerateArray())
            {
                if (w.ValueKind == JsonValueKind.String)
                {
                    var s = w.GetString()!;
                    if (!watch.Contains(s))
                        watch.Add(s);
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new PetSoulsProfile(new Dictionary<int, int>(levels), targets, other, watch) { Skipped = skipped };
    }

    private static int? Level(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var d))
            return null;
        var rounded = (int)Math.Round(d, MidpointRounding.AwayFromZero);
        return d >= 1 && d <= MaxLevel ? rounded : null;
    }
}
