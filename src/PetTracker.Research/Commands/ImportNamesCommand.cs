using System.CommandLine;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetTracker.Core.Domain;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

/// <summary>
/// One-time, polite import of pet ids, names and genera from the TH.GL community database (aion2.th.gl/db/pets).
/// This is the only place in the solution that uses the network for anything but passive capture, and the App never calls it.
/// Policy: a single GET of the list page (the genus is the section header, so no per-pet pages are needed), a descriptive
/// User-Agent, at least one second between requests and a hard cap of 220 requests.
/// </summary>
internal static class ImportNamesCommand
{
    public const string ListUrl = "https://aion2.th.gl/db/pets";
    public const string Credit = "Data: TH.GL (aion2.th.gl), game data © NCSOFT";
    private const int MaxRequests = 220;
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);

    private static readonly (string Genus, int Count)[] Expected = [("Special", 9), ("Cogni", 41), ("Natura", 48), ("Fera", 65), ("Varian", 46)];
    private const int ExpectedTotal = 209;

    public static Command Build(CommonOptions common)
    {
        _ = common;
        var output = new Option<string>("--out") { Description = "Catalog file to merge into.", DefaultValueFactory = _ => Path.Combine("data", "pets.json") };
        var fromFile = new Option<string?>("--from-file") { Description = "Parse a saved copy of the list page instead of fetching it (offline use)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the summary but write nothing." };
        var command = new Command("import-names", "Fill data/pets.json with pet ids, names and genera from the TH.GL community database (one polite request).")
        {
            output, fromFile, dryRun,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var outPath = result.GetValue(output)!;
            var savedPath = result.GetValue(fromFile);
            var dry = result.GetValue(dryRun);

            string html;
            DateTime fetched;
            if (savedPath is not null)
            {
                if (!File.Exists(savedPath))
                    throw new FileNotFoundException($"File not found: {savedPath}");
                html = File.ReadAllText(savedPath);
                fetched = File.GetLastWriteTimeUtc(savedPath);
                Console.WriteLine($"Parsing {savedPath} (no network).");
            }
            else
            {
                html = Fetch(ListUrl);
                fetched = DateTime.UtcNow;
            }

            var parsed = PetListParser.Parse(html);
            foreach (var w in parsed.Warnings)
                ConsoleOut.Warn("  " + w);
            if (parsed.Pets.Count == 0)
                throw new InvalidDataException("No pets found. The page layout may have changed.");
            if (parsed.Pets.Any(p => string.IsNullOrWhiteSpace(p.Genus)))
                throw new InvalidDataException("A pet has no genus; the page layout changed and per-pet pages would be needed.");

            var existing = File.Exists(outPath) ? File.ReadAllText(outPath) : null;
            var (json, report) = CatalogMerge.Merge(existing, parsed.Pets);

            Console.WriteLine();
            Console.WriteLine($"Imported {parsed.Pets.Count} pets. Catalog now has {report.Total}.");
            var perGenus = parsed.Pets.GroupBy(p => p.Genus).ToDictionary(g => g.Key, g => g.Count());
            foreach (var (genus, count) in perGenus.OrderBy(kv => kv.Key))
                Console.WriteLine($"  {genus,-8} {count}");
            Console.WriteLine($"  new {report.New}, changed {report.Changed}, conflicts {report.Conflicts.Count}");
            foreach (var c in report.Conflicts)
                ConsoleOut.Warn("  conflict: " + c);

            // Sanity checks against what the page itself says and what the user verified by hand.
            foreach (var (genus, declared) in parsed.DeclaredCounts)
                if (perGenus.GetValueOrDefault(genus) != declared)
                    ConsoleOut.Warn($"  WARNING: section {genus} says {declared} but {perGenus.GetValueOrDefault(genus)} links were parsed.");
            foreach (var (genus, count) in Expected)
                if (perGenus.GetValueOrDefault(genus) != count)
                    ConsoleOut.Warn($"  WARNING: expected {count} {genus} pets, found {perGenus.GetValueOrDefault(genus)}.");
            if (parsed.Pets.Count != ExpectedTotal)
                ConsoleOut.Warn($"  WARNING: expected {ExpectedTotal} pets in total, found {parsed.Pets.Count}.");
            foreach (var genus in perGenus.Keys.Where(g => Expected.All(e => e.Genus != g)))
                ConsoleOut.Warn($"  WARNING: unexpected genus \"{genus}\".");

            if (dry)
            {
                Console.WriteLine("Dry run: nothing written.");
                return 0;
            }

            File.WriteAllText(outPath, json);
            var sourcePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "pets.source.json");
            var source = new JsonObject
            {
                ["source"] = ListUrl,
                ["fetchedUtc"] = fetched.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["count"] = parsed.Pets.Count,
                ["credit"] = Credit,
            };
            File.WriteAllText(sourcePath, source.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            Console.WriteLine($"Wrote {outPath} and {sourcePath}.");
            return report.Conflicts.Count > 0 ? 3 : 0;
        }));
        return command;
    }

    private static int _requests;
    private static DateTime _lastRequest = DateTime.MinValue;

    private static string Fetch(string url)
    {
        if (_requests >= MaxRequests)
            throw new InvalidOperationException($"Request cap of {MaxRequests} reached.");
        var wait = _lastRequest + MinDelay - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            Thread.Sleep(wait);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Aion2-PetTracker-research/0.1 (one-time catalog import; github.com/Aion2-PetTracker)");
        _requests++;
        _lastRequest = DateTime.UtcNow;
        Console.WriteLine($"GET {url} (request {_requests}/{MaxRequests})");
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
