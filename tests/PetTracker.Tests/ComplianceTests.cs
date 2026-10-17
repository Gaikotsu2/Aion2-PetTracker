using System.Text.RegularExpressions;

namespace PetTracker.Tests;

/// <summary>Guards the zero-interaction rule for APIs the BannedApiAnalyzers cannot see (raw P/Invokes).</summary>
public class ComplianceTests
{
    private static string[] ForbiddenNames => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ForbiddenApis.txt"))
        .Where(l => l.Length > 0 && l[0] != (char)35).ToArray();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BannedSymbols.txt")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void SourceNeverReferencesProcessOrInputApis()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (var name in ForbiddenNames.Where(n => Regex.IsMatch(text, $@"\b{n}\w*\b")))
                offenders.Add($"{Path.GetRelativePath(root, file)}: {name}");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void BannedSymbolsFileCoversTheEssentials()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "BannedSymbols.txt"));
        Assert.Contains("P:System.Diagnostics.Process.Handle", text);
        Assert.Contains("P:System.Diagnostics.Process.MainModule", text);
        Assert.Contains("M:SharpPcap.IInjectionDevice.SendPacket", text);
    }
}
