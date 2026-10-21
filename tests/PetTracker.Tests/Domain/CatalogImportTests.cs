using System.Text.Json.Nodes;
using PetTracker.Core;
using PetTracker.Core.Domain;

namespace PetTracker.Tests.Domain;

public class CatalogImportTests
{
    // A small hand-written page with the same structure as aion2.th.gl/db/pets: one <h2> per genus with a count span,
    // then one <a href="/db/pets/pet_NNNN"> per pet whose name sits in a span.truncate. Other links must be ignored.
    private const string Fixture = """
        <html><body><a href="/db">Database</a><h1>Pets</h1>
        <button type="button">Special</button><button type="button">Cogni</button>
        <h2 class="x">Special<span class="text-slate-500">2</span></h2><div class="grid">
          <span class="inline-flex"><a class="group" href="/db/pets/pet_1001"><span class="min-w-0"><span class="block truncate transition-colors">Black Dragon</span></span></a></span>
          <span class="inline-flex"><a class="group" href="/db/pets/pet_1204"><img alt="" src="x.webp"/><span class="min-w-0"><span class="block truncate">Pagati</span></span></a></span>
        </div>
        <h2 class="x">Cogni<span class="text-slate-500">2</span></h2><div class="grid">
          <span><a href="/db/pets/pet_1005"><span class="block truncate">Klaw</span></a></span>
          <span><a href="/db/pets/pet_1080"><span class="block truncate">Sentinel K&#x27;nash</span></a></span>
        </div>
        <h2 class="x">Natura<span class="text-slate-500">1</span></h2><div class="grid">
          <span><a href="/db/pets/pet_1136"><span class="block truncate">Rafflesia</span></a></span>
        </div>
        </body></html>
        """;

    [Fact]
    public void ParserReadsIdNameAndGenusFromSections()
    {
        var parsed = PetListParser.Parse(Fixture);

        Assert.Empty(parsed.Warnings);
        Assert.Equal(5, parsed.Pets.Count);
        Assert.Equal(new ImportedPet(1001, "Black Dragon", "Special"), parsed.Pets[0]);
        Assert.Equal(new ImportedPet(1204, "Pagati", "Special"), parsed.Pets[1]);
        Assert.Equal(new ImportedPet(1005, "Klaw", "Cogni"), parsed.Pets[2]);
        Assert.Equal("Sentinel K'nash", parsed.Pets[3].Name);
        Assert.Equal(new ImportedPet(1136, "Rafflesia", "Natura"), parsed.Pets[4]);
        Assert.Equal(2, parsed.DeclaredCounts["Special"]);
        Assert.Equal(1, parsed.DeclaredCounts["Natura"]);
    }

    [Fact]
    public void ParserWarnsAboutDuplicatesAndNamelessLinks()
    {
        var html = Fixture + """<h2>Fera<span>2</span></h2><a href="/db/pets/pet_1005"><span class="block truncate">Klaw</span></a><a href="/db/pets/pet_9999"></a>""";
        var parsed = PetListParser.Parse(html);

        Assert.Equal(5, parsed.Pets.Count);
        Assert.Equal(2, parsed.Warnings.Count);
    }

    [Fact]
    public void MergeAddsNewPetsAndImportedValuesWin()
    {
        var existing = """[ {"id":1136,"name":"Rafflesia"}, {"id":1195,"name":"Dracuni Herbalist","genus":"Cogni","notes":"mine"}, {"id":1207,"name":"Agrint","genus":"Natura"} ]""";
        ImportedPet[] import =
        [
            new(1136, "Rafflesia", "Natura"),
            new(1195, "Dracuni Herbalist", "Cogni"),
            new(1207, "Agrint", "Natura"),
            new(1005, "Klaw", "Cogni"),
        ];

        var (json, report) = CatalogMerge.Merge(existing, import);

        Assert.Equal(4, report.Total);
        Assert.Equal(1, report.New);
        Assert.Equal(1, report.Changed); // 1136 gained its genus
        Assert.Empty(report.Conflicts);
        var array = JsonNode.Parse(json)!.AsArray();
        Assert.Equal([1005, 1136, 1195, 1207], array.Select(n => n!["id"]!.GetValue<int>()));
        Assert.Equal("Natura", array[1]!["genus"]!.GetValue<string>());
        Assert.Equal("mine", array[2]!["notes"]!.GetValue<string>()); // fields the import lacks are kept
    }

    [Fact]
    public void MergeReportsConflictsAndTheImportWins()
    {
        var existing = """[ {"id":1195,"name":"Herbalist Guess","genus":"Natura"}, {"id":1500,"name":"Not In Import"} ]""";
        var (json, report) = CatalogMerge.Merge(existing, [new ImportedPet(1195, "Dracuni Herbalist", "Cogni")]);

        Assert.Equal(2, report.Conflicts.Count);
        Assert.Equal(1, report.Changed);
        var array = JsonNode.Parse(json)!.AsArray();
        Assert.Equal("Dracuni Herbalist", array[0]!["name"]!.GetValue<string>());
        Assert.Equal("Cogni", array[0]!["genus"]!.GetValue<string>());
        Assert.Equal(1500, array[1]!["id"]!.GetValue<int>()); // untouched entries stay
    }

    [Fact]
    public void MergeWithoutExistingFileAndIsStableWhenRepeated()
    {
        ImportedPet[] import = [new(1002, "Krall Laborer", "Cogni"), new(1001, "Black Dragon", "Special")];
        var (first, r1) = CatalogMerge.Merge(null, import);
        var (second, r2) = CatalogMerge.Merge(first, import);

        Assert.Equal(2, r1.New);
        Assert.Equal(first, second);
        Assert.Equal(0, r2.New + r2.Changed + r2.Conflicts.Count);
    }

    [Fact]
    public void LoaderAcceptsArrayAndWrappedObject()
    {
        var a = new PetCatalog();
        a.LoadBase("""[{"id":1005,"name":"Klaw","genus":"Cogni"}]""");
        var b = new PetCatalog();
        b.LoadBase("""{"meta":{"source":"x"},"pets":[{"id":1005,"name":"Klaw","genus":"Cogni"}]}""");
        Assert.Equal("Klaw", a.NameOf(1005));
        Assert.Equal("Klaw", b.NameOf(1005));
    }

    [Fact]
    public void UserOverridesAlwaysWinOverImportedNames()
    {
        var catalog = new PetCatalog();
        catalog.LoadBase("""[{"id":1005,"name":"Klaw","genus":"Cogni"}]""");
        catalog.LoadOverrides("""{"1005":"My Klaw"}""");

        Assert.Equal("My Klaw", catalog.NameOf(1005));
        Assert.Equal("Cogni", catalog.GenusOf(1005));
    }

    private static PetState State(int id, int level) => new(id, level, 0, level > 0, DateTime.UnixEpoch, false);

    [Fact]
    public void GameCountExcludesSpecialPets()
    {
        var catalog = new PetCatalog();
        catalog.LoadBase("""
            [{"id":1001,"name":"Black Dragon","genus":"Special"},{"id":1204,"name":"Pagati","genus":"Special"},
             {"id":1002,"name":"Krall","genus":"Cogni"},{"id":1005,"name":"Klaw","genus":"Cogni"},{"id":1136,"name":"Rafflesia","genus":"Natura"}]
            """);

        var count = catalog.GameCount([State(1204, 1), State(1002, 2), State(1005, 0), State(1136, 3)]);

        Assert.Equal(new GameCollectionCount(2, 3, 1, 2), count);
        Assert.Equal("2/3, Special 1/2", count.ToString());
        Assert.False(catalog.CountsTowardCollection(1204));
        Assert.True(catalog.IsSpecial(1001));
        Assert.True(catalog.CountsTowardCollection(1002));
    }

    [Fact]
    public void UserGenusOverrideMovesAPetOutOfTheCollectionCount()
    {
        var catalog = new PetCatalog();
        catalog.LoadBase("""[{"id":1002,"name":"Krall","genus":"Cogni"}]""");
        catalog.LoadOverrides("""{"1002":{"genus":"Special"}}""");
        Assert.False(catalog.CountsTowardCollection(1002));
    }

    [Fact]
    public void SourceTagsDungeonAndShopPetsWithoutChangingTheCount()
    {
        var catalog = new PetCatalog();
        catalog.LoadBase("""
            [{"id":1002,"name":"Krall","genus":"Cogni","source":"Dungeon"},{"id":1005,"name":"Klaw","genus":"Cogni","source":" shop "},
             {"id":1136,"name":"Rafflesia","genus":"Natura","source":"Somewhere"},{"id":1001,"name":"Black Dragon","genus":"Special","source":"Shop"}]
            """);

        Assert.Equal("Dungeon", catalog.SourceOf(1002));
        Assert.True(catalog.IsDungeon(1002));
        Assert.False(catalog.IsShop(1002));
        Assert.Equal("Shop", catalog.SourceOf(1005));
        Assert.True(catalog.IsShop(1005));
        Assert.Null(catalog.SourceOf(1136));
        Assert.Null(catalog.SourceOf(9999));
        Assert.True(catalog.IsShop(1001) && catalog.IsSpecial(1001));
        Assert.Equal(new GameCollectionCount(1, 3, 0, 1), catalog.GameCount([State(1002, 1)]));
    }

    [Fact]
    public void MergeKeepsTheHandMaintainedSource()
    {
        var existing = """[{"id":1002,"name":"Krall","genus":"Cogni","source":"Dungeon"}]""";
        var (json, _) = CatalogMerge.Merge(existing, [new ImportedPet(1002, "Krall Laborer", "Cogni")]);

        var catalog = new PetCatalog();
        catalog.LoadBase(json);
        Assert.Equal("Krall Laborer", catalog.NameOf(1002));
        Assert.Equal("Dungeon", catalog.SourceOf(1002));
    }

    [Fact]
    public void ShippedCatalogHasTheKnownShape()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "pets.json");
        Assert.True(File.Exists(path));
        var catalog = PetCatalog.Load(path, null);
        var ids = catalog.KnownIds.ToList();
        Assert.Equal(209, ids.Count);
        Assert.Equal(9, ids.Count(catalog.IsSpecial));
        Assert.Equal("Klaw", catalog.NameOf(1005));
        Assert.Equal("Pagati", catalog.NameOf(1204));
        Assert.True(catalog.IsSpecial(1204));
        Assert.Equal(200, catalog.GameCount([]).Total);
    }
}

public class LinkPolicyTests
{
    [Fact]
    public void AcceptsTheExactPetPage()
    {
        Assert.True(LinkPolicy.IsAllowedUrl("https://aion2.th.gl/db/pets/pet_1136"));
        Assert.True(LinkPolicy.IsPetUrl(LinkPolicy.PetUrl(1005)));
        Assert.True(LinkPolicy.IsAllowedUrl(LinkPolicy.NpcapUrl));
        Assert.True(LinkPolicy.IsAllowedUrl(LinkPolicy.PetSoulsUrl));
        Assert.True(LinkPolicy.IsAllowedUrl("https://petsouls.dev/"));
        Assert.True(LinkPolicy.IsAllowedUrl(LinkPolicy.RepoUrl));
        Assert.True(LinkPolicy.IsAllowedUrl("https://github.com/Gaikotsu2/Aion2-PetTracker"));
        Assert.True(LinkPolicy.IsAllowedUrl(LinkPolicy.KofiUrl));
        Assert.True(LinkPolicy.IsAllowedUrl("https://ko-fi.com/gaikotsu2"));
    }

    [Theory]
    [InlineData("http://aion2.th.gl/db/pets/pet_1136")]
    [InlineData("ftp://aion2.th.gl/db/pets/pet_1136")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://th.gl/db/pets/pet_1136")]
    [InlineData("https://evil.example/db/pets/pet_1136")]
    [InlineData("https://aion2.th.gl.evil.example/db/pets/pet_1136")]
    [InlineData("https://user@aion2.th.gl/db/pets/pet_1136")]
    [InlineData("https://aion2.th.gl:444/db/pets/pet_1136")]
    [InlineData("https://aion2.th.gl/db/pets")]
    [InlineData("https://aion2.th.gl/db/pets/")]
    [InlineData("https://aion2.th.gl/db/pets/pet_")]
    [InlineData("https://aion2.th.gl/db/pets/pet_12a")]
    [InlineData("https://aion2.th.gl/db/pets/pet_1136/")]
    [InlineData("https://aion2.th.gl/db/pets/pet_1136?x=1")]
    [InlineData("https://aion2.th.gl/db/pets/pet_1136#top")]
    [InlineData("http://petsouls.dev/")]
    [InlineData("https://petsouls.dev/evil")]
    [InlineData("https://petsouls.dev/#/x")]
    [InlineData("https://petsouls.dev.evil.example/")]
    [InlineData("http://github.com/Gaikotsu2/Aion2-PetTracker")]
    [InlineData("https://github.com/Gaikotsu2/Aion2-PetTracker/../evil")]
    [InlineData("https://github.com/Gaikotsu2/Aion2-PetTracker/releases/download/x.exe")]
    [InlineData("https://github.com/someone-else/Aion2-PetTracker")]
    [InlineData("https://github.com.evil.example/Gaikotsu2/Aion2-PetTracker")]
    [InlineData("http://ko-fi.com/gaikotsu2")]
    [InlineData("https://ko-fi.com/someone-else")]
    [InlineData("https://ko-fi.com/gaikotsu2/shop")]
    [InlineData("https://ko-fi.com.evil.example/gaikotsu2")]
    [InlineData("https://aion2.th.gl/db/pets/pet_1136\n")]
    [InlineData("https://aion2.th.gl/db/items/item_1136")]
    [InlineData("https://aion2.th.gl/db/pets/../items/pet_1136")]
    [InlineData("https://aion2.th.gl/db/pets/pet_-1")]
    [InlineData("https://aion2.th.gl/db/pets/pet_\u0661\u0662")]
    [InlineData("")]
    public void RejectsEverythingElse(string url)
    {
        Assert.False(LinkPolicy.IsPetUrl(url));
        Assert.False(LinkPolicy.IsAllowedUrl(url));
    }

    [Fact]
    public void RejectsNull() => Assert.False(LinkPolicy.IsAllowedUrl(null));
}
