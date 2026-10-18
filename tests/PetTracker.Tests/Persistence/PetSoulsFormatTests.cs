using System.Text;
using PetTracker.Core.Domain;
using PetTracker.Core.Persistence;

namespace PetTracker.Tests.Persistence;

public class PetSoulsFormatTests
{
    // A real code exported from petsouls.dev (docs/petsouls-integration/jwt.txt): 133 levels, 43 watch entries.
    private const string SiteCode = "eyJsZXZlbHMiOnsiMTAwMiI6MSwiMTAwNCI6MSwiMTAwNSI6MiwiMTAwNiI6MiwiMTAwNyI6MywiMTAwOCI6MSwiMTAxMCI6MywiMTAxMSI6MiwiMTAxMyI6MywiMTAxNCI6MSwiMTAxNSI6MywiMTAxNiI6MywiMTAxOCI6MywiMTAyMiI6MywiMTAyNyI6MiwiMTAyOSI6MSwiMTAzMCI6MSwiMTAzMSI6MSwiMTAzMyI6MywiMTAzNCI6MSwiMTAzNSI6MSwiMTAzNyI6MiwiMTAzOCI6MiwiMTAzOSI6MywiMTA0MiI6MiwiMTA0NSI6MiwiMTA0NiI6MywiMTA0NyI6MSwiMTA0OCI6MSwiMTA0OSI6MiwiMTA1MCI6MywiMTA1MSI6MywiMTA1MiI6MiwiMTA1MyI6MSwiMTA1NCI6MywiMTA1NiI6MSwiMTA1OCI6MSwiMTA2MCI6MywiMTA2MSI6MSwiMTA2MiI6MiwiMTA2NCI6MywiMTA2NiI6MywiMTA2OSI6MSwiMTA3MSI6MiwiMTA3MiI6MSwiMTA3MyI6MiwiMTA3NCI6MywiMTA3NSI6MywiMTA3NyI6MSwiMTA4MyI6MiwiMTA4NCI6MSwiMTA4NiI6MSwiMTA4NyI6MSwiMTA5MiI6MSwiMTA5MyI6MSwiMTA5NCI6MywiMTA5NSI6MywiMTA5NiI6MywiMTA5NyI6MywiMTA5OSI6MSwiMTEwNCI6MywiMTEwNiI6MywiMTExMCI6MSwiMTExNCI6MiwiMTExNSI6MywiMTExNiI6MywiMTExNyI6MywiMTExOCI6MywiMTExOSI6MSwiMTEyMCI6MywiMTEyMSI6MSwiMTEyMyI6MiwiMTEyNSI6MSwiMTEyNiI6MywiMTEyOCI6MywiMTEzMCI6MSwiMTEzNCI6MiwiMTEzNSI6MiwiMTEzNiI6MywiMTEzNyI6MSwiMTE0MCI6MSwiMTE0MSI6MSwiMTE0MiI6MSwiMTE0MyI6MiwiMTE0NCI6MSwiMTE0NyI6MiwiMTE0OCI6MywiMTE1MyI6MywiMTE1NCI6MSwiMTE1NSI6MywiMTE1NiI6MSwiMTE1NyI6MSwiMTE1OSI6MSwiMTE2MSI6MSwiMTE2MiI6MywiMTE2MyI6MiwiMTE2NCI6MSwiMTE2NSI6MywiMTE2NiI6MywiMTE2OSI6MywiMTE3MCI6MywiMTE3MSI6MywiMTE3MiI6MywiMTE3MyI6MywiMTE3NCI6MiwiMTE3NSI6MSwiMTE3NiI6MywiMTE3OCI6MiwiMTE4MCI6MSwiMTE4MSI6MywiMTE4MyI6MiwiMTE4NCI6MSwiMTE4NiI6MiwiMTE4NyI6MywiMTE4OCI6MiwiMTE5MiI6MSwiMTE5MyI6MSwiMTE5NCI6MSwiMTE5NiI6MSwiMTIwMCI6MSwiMTIwMSI6MywiMTIwMiI6MywiMTIwMyI6MiwiMTIwNiI6MSwiMTIwNyI6MywiMTIwOSI6MSwiMTIxMyI6MywiMTIxNCI6MywiMTIxNSI6MSwiMTIxNiI6MSwiMTIxNyI6MiwiMTIxOCI6MSwiMTIxOSI6M30sInRhcmdldHMiOltdLCJvdGhlciI6e30sIndhdGNoIjpbIjEwNTgiLCIxMTk2IiwiMTA5OSIsIjEwNzciLCIxMDkzIiwiMTIxNSIsIjExMDMiLCIxMDkxIiwiQWJlcnJhbnQgT2R5bGUgU3BpZGVyIiwiQW5jaWVudCBTcGlyaXQiLCJBc2hlbiBCYXNpbGlzayIsIkFzaGVuIEx1cHlsbGluaSIsIkF1bHRyb3NzJ3MgRG9wcGVsZ2FuZ2VyIiwiQ29yYWRvbiIsIkRpb25hZSIsIkRyYW5hIENvbnRhbWluYW50IiwiRHJhdG9uYSBUaW5rZXJlciIsIkRyaWVkIFVwIEZvcmVzdCBHdWFyZGlhbiIsIkVsaXRlIEtlbmVpcmUiLCJFbGl0ZSBQb2lzb25vdXMgU3BvcmUgRnVuZ2VuIiwiRWxpdGUgU291bGxlc3MgQ29ycHNlIEduYXQiLCJFbGl0ZSBVcnN1cyIsIkZsYW1lIEJhcmdoZXN0IiwiRmxhbWUgU3RvbmUgR3VhcmRpYW4gU3RhdHVlIiwiRm9yZXN0IERpb25hZSIsIkZ1cmlvdXMgRmVydWsiLCJHYXRla2VlcGVyIFBpbm9waSIsIkdyYXZlIFJhdCIsIktlcm5vbiIsIktyYW8gTXV0YW50IiwiS3JhbyBTZWFsIEd1YXJkaWFuIiwiS3JhbyBUZXN0IFN1YmplY3QiLCJNdW11IE92ZXJzZWVyIiwiTXV0YXRlZCBJbnNlY3RvaWQgVmFsa2EiLCJTZW50aW5lbCBLJ25hc2giLCJTd2FtcCBNb25zdGVyIiwiVWx0aW1hdGUgRWFydGggU3Bpcml0IiwiVWx0aW1hdGUgRmlyZSBTcGlyaXQiLCJVbHRpbWF0ZSBXYXRlciBTcGlyaXQiLCJVbHRpbWF0ZSBXaW5kIFNwaXJpdCIsIlVydWd1Z3UgQnJhd2xlciIsIlZlbm9tb3VzIEluc2VjdCBTcmFtIiwiWW91bmcgVG9nIl19";

    private static PetSoulsProfile Parse(string text)
    {
        Assert.True(PetSoulsFormat.TryParse(text, out var profile, out var error), error);
        return profile!;
    }

    private static PetState Pet(int id, int level, int progress = 0) => new(id, level, progress, level > 0, DateTime.UnixEpoch, false);

    [Fact]
    public void ReadsTheSitesOwnCode()
    {
        var p = Parse(SiteCode);
        Assert.Equal(133, p.Levels.Count);
        Assert.Equal(3, p.Levels[1007]);
        Assert.Equal(1, p.Levels[1002]);
        Assert.Empty(p.Targets);
        Assert.Empty(p.Other);
        Assert.Equal(43, p.Watch.Count);
        Assert.Contains("Aultross's Doppelganger", p.Watch);
        Assert.Equal(0, p.Skipped);
    }

    [Fact]
    public void ReEncodingGivesTheSitesCodeBackByteForByte()
    {
        Assert.Equal(SiteCode, PetSoulsFormat.ToCode(Parse(SiteCode)));
    }

    [Fact]
    public void ExportLeavesOutUnregisteredPetsAndCapsTheLevel()
    {
        var profile = PetSoulsFormat.FromCollection([Pet(1010, 2, 30), Pet(1003, 0, 4), Pet(1001, 5)]);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(PetSoulsFormat.ToCode(profile)));
        Assert.Equal("""{"levels":{"1001":3,"1010":2},"targets":[],"other":{},"watch":[]}""", json);
    }

    [Fact]
    public void ExportCarriesTheExtrasFromTheLastImport()
    {
        var extras = Parse(SiteCode).ToExtras();
        var again = Parse(PetSoulsFormat.ToCode(PetSoulsFormat.FromCollection([Pet(1002, 1)], extras)));
        Assert.Equal(extras.Watch, again.Watch);
        Assert.Single(again.Levels);
    }

    [Fact]
    public void NonAsciiNamesRoundTrip()
    {
        var extras = new PetSoulsExtras { Targets = [1002], Other = { ["Drakan Ünit"] = 2 }, Watch = ["크라오", "Grave Rat"] };
        var p = Parse(PetSoulsFormat.ToCode(PetSoulsFormat.FromCollection([], extras)));
        Assert.Equal([1002], p.Targets);
        Assert.Equal(2, p.Other["Drakan Ünit"]);
        Assert.Equal(["크라오", "Grave Rat"], p.Watch);
    }

    [Fact]
    public void FileJsonIsAcceptedBack()
    {
        var file = PetSoulsFormat.ToFileJson(PetSoulsFormat.FromCollection([Pet(1005, 2)]));
        Assert.Equal(2, Parse(file).Levels[1005]);
    }

    [Theory]
    [InlineData("""{"profile":{"levels":{"1005":2}}}""")]
    [InlineData("""{"app":"aion2-pet-tracker","version":2,"active":"b","profiles":{"a":{"levels":{"1005":1}},"b":{"levels":{"1005":2}}}}""")]
    [InlineData("""  {"levels":{"1005":2}}  """)]
    public void AcceptsTheSitesJsonShapes(string json) => Assert.Equal(2, Parse(json).Levels[1005]);

    [Fact]
    public void ToleratesWhitespaceAndMissingPadding()
    {
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"levels":{"1005":2}}""")).TrimEnd('=');
        Assert.Equal(2, Parse(code[..10] + "\r\n " + code[10..]).Levels[1005]);
    }

    [Fact]
    public void DropsWhatTheSiteDrops()
    {
        var p = Parse("""{"levels":{"1005":7,"1006":0,"abc":2,"1007":2.4,"1008":"2"},"targets":[1005,"x"],"watch":["a",3]}""");
        Assert.Equal(new Dictionary<int, int> { [1007] = 2 }, p.Levels);
        Assert.Equal([1005], p.Targets);
        Assert.Equal(["a"], p.Watch);
        Assert.Equal(6, p.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!")]
    [InlineData("aGVsbG8=")] // "hello"
    [InlineData("""{"name":"x"}""")]
    [InlineData("""[1,2,3]""")]
    public void RejectsWhatIsNotAProfile(string text)
    {
        Assert.False(PetSoulsFormat.TryParse(text, out var profile, out var error));
        Assert.Null(profile);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
