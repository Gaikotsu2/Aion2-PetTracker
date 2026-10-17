using System.Text.Json;

namespace PetTracker.Tests;

/// <summary>Signing is opt-in: with no configuration the build must be unchanged. (Process.Start is banned, so these check the wiring.)</summary>
public class SigningTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BannedSymbols.txt")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    [Fact]
    public void TargetsAreImportedAndOnlyActWhenOptedIn()
    {
        Assert.Contains("build/Signing.targets", Read("Directory.Build.props"));

        var targets = Read("build", "Signing.targets");
        var guard = "'$(SignBuild)' == 'true' Or ('$(SignBuild)' != 'false' And Exists('$(SigningMetadataFile)'))";
        foreach (var name in new[] { "SignIntermediateOutputs", "SignBuildOutputs", "SignPublishedOutputs" })
        {
            var start = targets.IndexOf($"<Target Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            var header = targets[start..targets.IndexOf('>', start)];
            Assert.Contains(guard, header); // every signing target is skipped without config
        }
        Assert.Contains("AfterTargets=\"Build\"", targets);
        Assert.Contains("AfterTargets=\"Publish\"", targets);
        Assert.Contains("signing" + Path.DirectorySeparatorChar + "metadata.json", targets);
    }

    [Fact]
    public void OnlyOurOwnAssembliesAreSigned()
    {
        var targets = Read("build", "Signing.targets");
        Assert.Contains("PetTracker.*.dll", targets);
        Assert.Contains("pt-research.dll", targets);
        // No wildcard over everything: third-party DLLs must never match.
        Assert.DoesNotContain("Include=\"$(OutDir)*.dll", targets);
        Assert.DoesNotContain("Include=\"$(PublishDir)*.dll", targets);
    }

    [Fact]
    public void SampleMetadataIsValidAndKeepsAzureCliCredential()
    {
        using var doc = JsonDocument.Parse(Read("signing", "metadata.sample.json"));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("Endpoint", out _));
        Assert.True(root.TryGetProperty("CodeSigningAccountName", out _));
        Assert.True(root.TryGetProperty("CertificateProfileName", out _));
        var excluded = root.GetProperty("ExcludeCredentials").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("AzureCliCredential", excluded);
        Assert.Contains("signing/metadata.json", Read(".gitignore"));
    }
}
