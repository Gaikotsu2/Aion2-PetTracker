using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PetTracker.Core.Domain;

public sealed record ImportedPet(int Id, string Name, string Genus);

/// <param name="Pets">Every pet link found, in page order.</param>
/// <param name="DeclaredCounts">The count shown next to each section header (e.g. Special 9).</param>
/// <param name="Warnings">Links without a name, duplicate ids, links outside any section.</param>
public sealed record ParsedPetList(IReadOnlyList<ImportedPet> Pets, IReadOnlyDictionary<string, int> DeclaredCounts, IReadOnlyList<string> Warnings);

/// <summary>
/// Parses a saved copy of the TH.GL pet list page (aion2.th.gl/db/pets). The page groups pets in sections:
/// <c>&lt;h2&gt;Genus&lt;span&gt;count&lt;/span&gt;&lt;/h2&gt;</c> followed by links <c>href="/db/pets/pet_NNNN"</c> whose
/// <c>span.truncate</c> holds the name. The id in the link is the pet id the game server sends. No network access here.
/// </summary>
public static partial class PetListParser
{
    [GeneratedRegex("""<h2[^>]*>(?<genus>[^<]+)<span[^>]*>(?<count>\d+)</span></h2>|<a\b[^>]*href="/db/pets/pet_(?<id>\d+)"[^>]*>(?<body>.*?)</a>""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex("""<span[^>]*class="[^"]*\btruncate\b[^"]*"[^>]*>(?<name>[^<]*)</span>""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NameSpan();

    public static ParsedPetList Parse(string html)
    {
        var pets = new List<ImportedPet>();
        var declared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var seen = new HashSet<int>();
        string? genus = null;

        foreach (Match m in Token().Matches(html))
        {
            if (m.Groups["genus"].Success)
            {
                genus = WebUtility.HtmlDecode(m.Groups["genus"].Value).Trim();
                declared[genus] = int.Parse(m.Groups["count"].Value);
                continue;
            }

            var id = int.Parse(m.Groups["id"].Value);
            var name = NameSpan().Match(m.Groups["body"].Value) is { Success: true } n ? WebUtility.HtmlDecode(n.Groups["name"].Value).Trim() : "";
            if (name.Length == 0)
            {
                warnings.Add($"pet_{id}: no name found, skipped");
                continue;
            }
            if (genus is null)
            {
                warnings.Add($"pet_{id} ({name}): link outside any genus section, skipped");
                continue;
            }
            if (!seen.Add(id))
            {
                warnings.Add($"pet_{id} ({name}): listed more than once, first one kept");
                continue;
            }
            pets.Add(new ImportedPet(id, name, genus));
        }
        return new ParsedPetList(pets, declared, warnings);
    }
}

public sealed record MergeReport(int Total, int Imported, int New, int Changed, IReadOnlyList<string> Conflicts, IReadOnlyDictionary<string, int> PerGenus);

public static class CatalogMerge
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Merges imported pets into the existing data/pets.json content. Imported names and genera win (they replace community
    /// guesses); fields the import does not carry (notes, anything else) and entries not in the import are kept.
    /// A conflict is an existing non-empty name or genus that the import contradicts.
    /// </summary>
    public static (string Json, MergeReport Report) Merge(string? existingJson, IReadOnlyList<ImportedPet> imported)
    {
        var entries = new SortedDictionary<int, JsonObject>();
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            var root = JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var array = root as JsonArray ?? (root as JsonObject)?["pets"] as JsonArray ?? [];
            foreach (var node in array)
                if (node is JsonObject o && o["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id))
                    entries[id] = o;
        }

        int added = 0, changed = 0;
        var conflicts = new List<string>();
        foreach (var pet in imported)
        {
            if (!entries.TryGetValue(pet.Id, out var o))
            {
                entries[pet.Id] = new JsonObject { ["id"] = pet.Id, ["name"] = pet.Name, ["genus"] = pet.Genus };
                added++;
                continue;
            }

            var oldName = Text(o["name"]);
            var oldGenus = Text(o["genus"]);
            var differs = false;
            if (oldName is not null && !string.Equals(oldName, pet.Name, StringComparison.OrdinalIgnoreCase))
                conflicts.Add($"pet {pet.Id}: name was \"{oldName}\", import says \"{pet.Name}\"");
            if (oldGenus is not null && !string.Equals(oldGenus, pet.Genus, StringComparison.OrdinalIgnoreCase))
                conflicts.Add($"pet {pet.Id} ({pet.Name}): genus was {oldGenus}, import says {pet.Genus}");
            differs |= !string.Equals(oldName, pet.Name, StringComparison.Ordinal) || !string.Equals(oldGenus, pet.Genus, StringComparison.Ordinal);

            // Rebuild with id, name, genus first; keep every other field the file already had.
            var rebuilt = new JsonObject { ["id"] = pet.Id, ["name"] = pet.Name, ["genus"] = pet.Genus };
            foreach (var (key, value) in o.ToList())
                if (key is not "id" and not "name" and not "genus")
                    rebuilt[key] = value?.DeepClone();
            entries[pet.Id] = rebuilt;
            if (differs)
                changed++;
        }

        var perGenus = entries.Values.GroupBy(o => Text(o["genus"]) ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var lines = entries.Values.Select(o => "  " + o.ToJsonString(Compact));
        var json = "[\n" + string.Join(",\n", lines) + "\n]\n";
        return (json, new MergeReport(entries.Count, imported.Count, added, changed, conflicts, perGenus));
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
