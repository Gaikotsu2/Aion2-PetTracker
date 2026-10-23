using PetTracker.Core.Diagnostics;

namespace PetTracker.Tests.Diagnostics;

public class LogSanitizerTests
{
    private static LogSanitizer Make() => new("Alice", "ALICE-PC", [(@"C:\Users\Alice", "%USERPROFILE%"), (@"C:\Users\Alice\AppData\Roaming", "%APPDATA%")]);

    [Fact]
    public void IpAddressesBecomeStableTokens()
    {
        var s = Make();
        var line = s.Sanitize("192.168.1.20:51234 -> 203.0.113.9:7777 and again 192.168.1.20");
        Assert.DoesNotContain("192.168", line);
        Assert.DoesNotContain("203.0.113", line);
        Assert.Contains("ip#1(private):51234", line);
        Assert.Contains("ip#2(public):7777", line);
        Assert.EndsWith("ip#1(private)", line);
    }

    [Fact]
    public void Ipv6AndMacAreRemoved()
    {
        var s = Make();
        var line = s.Sanitize("addr 2001:db8::1234 and fe80::1%12 mac 00-1A-2B-3C-4D-5E / aa:bb:cc:dd:ee:ff");
        Assert.DoesNotContain("2001:db8", line);
        Assert.DoesNotContain("fe80", line);
        Assert.DoesNotContain("1A-2B", line);
        Assert.DoesNotContain("aa:bb", line);
    }

    [Fact]
    public void VersionsAndTimesAreLeftAlone()
    {
        var s = Make();
        Assert.Equal("os 10.0.26300.1 app 0.1.0 at 14:03:11 ok", s.Sanitize("os 10.0.26300.1 app 0.1.0 at 14:03:11 ok"));
    }

    [Fact]
    public void ProfilePathsUserAndMachineNamesAreRemoved()
    {
        var s = Make();
        var line = s.Sanitize(@"data C:\Users\Alice\AppData\Roaming\Aion2PetTracker; other C:\Users\Bob\x; host alice-pc user ALICE");
        Assert.DoesNotContain("Alice", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bob", line);
        Assert.Contains(@"%APPDATA%\Aion2PetTracker", line);
        Assert.Contains("<host>", line);
        Assert.Contains("<user>", line);
    }

    [Fact]
    public void StackTracePathsKeepOnlyTheFileName()
    {
        var s = Make();
        var line = s.Sanitize(@"   at X.Y() in D:\Build\src\PetTracker.Core\Pipeline.cs:line 42");
        Assert.Equal("   at X.Y() in Pipeline.cs:line 42", line);
    }
}

public class DiagSinkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pt-diag-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void WritesSanitizedTimestampedLinesToTheDaysFile()
    {
        var now = new DateTime(2026, 10, 22, 14, 3, 11, DateTimeKind.Utc);
        using var sink = new DiagSink(_folder, new LogSanitizer("zed", "zedbox", []), () => now);
        sink.Write(DiagLevel.Info, "Locator", "connected to 203.0.113.9:7777");
        sink.Write(DiagLevel.Error, "Capture", "boom", new InvalidOperationException("bad"));
        sink.Flush();

        var text = File.ReadAllText(Path.Combine(_folder, "pettracker-20261022.log"));
        Assert.Contains("2026-10-22T14:03:11.000Z [INF] Locator: connected to ip#1(public):7777", text);
        Assert.Contains("[ERR] Capture: boom", text);
        Assert.Contains("System.InvalidOperationException: bad", text);
        Assert.DoesNotContain("203.0.113.9", text);
    }

    [Fact]
    public void FilesOlderThanThreeDaysAreDeleted()
    {
        Directory.CreateDirectory(_folder);
        foreach (var day in new[] { "20261017", "20261019", "20261020", "20261021", "20261022" })
            File.WriteAllText(Path.Combine(_folder, $"pettracker-{day}.log"), "x");
        File.WriteAllText(Path.Combine(_folder, "other.log"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "other.log"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        using var sink = new DiagSink(_folder, new LogSanitizer("zed", "zedbox", []), () => new DateTime(2026, 10, 22, 23, 59, 0, DateTimeKind.Utc));

        var left = Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["pettracker-20261020.log", "pettracker-20261021.log", "pettracker-20261022.log"], left);
    }

    [Fact]
    public void DailySizeCapDropsFurtherInfoLines()
    {
        var now = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
        using var sink = new DiagSink(_folder, new LogSanitizer("zed", "zedbox", []), () => now, dayCapBytes: 2000);
        for (var i = 0; i < 200; i++)
            sink.Write(DiagLevel.Info, "Spam", new string('a', 50));
        sink.Write(DiagLevel.Error, "Spam", "still recorded");
        sink.Flush();

        var path = Path.Combine(_folder, "pettracker-20261022.log");
        var text = File.ReadAllText(path);
        Assert.Contains("size cap reached", text);
        Assert.Contains("still recorded", text);
        Assert.True(new FileInfo(path).Length < 4000);
    }

    [Fact]
    public void ReportContainsHeaderStatusAndLogs()
    {
        var now = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
        var sanitizer = new LogSanitizer("zed", "zedbox", []);
        using var sink = new DiagSink(_folder, sanitizer, () => now);
        sink.Write(DiagLevel.Info, "Test", "hello from 10.1.2.3");
        sink.Flush();

        var report = DiagReport.Build(_folder, "tracker status  : Waiting for game", sanitizer, now);
        Assert.Contains("diagnostic report", report);
        Assert.Contains("app version", report);
        Assert.Contains("Waiting for game", report);
        Assert.Contains("==== pettracker-20261022.log ====", report);
        Assert.Contains("hello from ip#1(private)", report);
        Assert.DoesNotContain("10.1.2.3", report);
    }
}

public class OpcodeHistogramTests
{
    [Fact]
    public void ReportsOnlyWhenSomethingChanged()
    {
        var h = new OpcodeHistogram();
        Assert.Null(h.TakeReport(_ => null));
        h.Add(0x1234);
        h.Add(0x1234);
        h.Add(0x0001);
        var first = h.TakeReport(op => op == 1 ? "Heartbeat" : null);
        Assert.Contains("1234=2 (+2)", first);
        Assert.Contains("0001[Heartbeat]=1", first);
        Assert.Null(h.TakeReport(_ => null));
    }
}
