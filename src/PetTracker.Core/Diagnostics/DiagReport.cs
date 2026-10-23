using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PetTracker.Core.Diagnostics;

/// <summary>Facts about the machine and install that explain most "it does not work" reports. Nothing here identifies the person.</summary>
public static class DiagEnvironment
{
    public static IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"app version     : {AppInfo.Version}",
            $"os              : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); process {RuntimeInformation.ProcessArchitecture}",
            $"runtime         : {RuntimeInformation.FrameworkDescription}",
            $"cpu cores       : {Environment.ProcessorCount}",
            $"culture         : {CultureInfo.CurrentCulture.Name}; UTC offset {TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)}",
            $"elevated        : {IsElevated()}",
            $"install kind    : {InstallKind()}",
        };
        lines.AddRange(Npcap());
        return lines;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string InstallKind()
    {
        var baseDir = AppContext.BaseDirectory;
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return baseDir.StartsWith(programs, StringComparison.OrdinalIgnoreCase) ? "installer (per-user)" : "portable / self-built";
    }

    private static IEnumerable<string> Npcap()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var (label, path) in new[] { ("npcap wpcap.dll ", Path.Combine(system, "Npcap", "wpcap.dll")), ("system wpcap.dll ", Path.Combine(system, "wpcap.dll")) })
        {
            string state;
            try { state = File.Exists(path) ? "present, version " + FileVersionInfo.GetVersionInfo(path).FileVersion : "not found"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state = "unreadable"; }
            yield return $"{label,-16}: {state}";
        }
    }
}

/// <summary>Assembles the single text file a user sends to support: a header, the live status, then every retained log file.</summary>
public static class DiagReport
{
    public static string Build(string logFolder, string statusSnapshot, LogSanitizer sanitizer, DateTime? nowUtc = null)
    {
        DiagLog.Flush();
        var sb = new StringBuilder();
        sb.AppendLine("Aion 2 Pet Tracker - diagnostic report");
        sb.AppendLine($"created (UTC)   : {nowUtc ?? DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        foreach (var line in DiagEnvironment.Describe())
            sb.AppendLine(sanitizer.Sanitize(line));
        sb.AppendLine();
        sb.AppendLine("Privacy: IP addresses appear as ip#N tokens, user/machine names and profile folders are replaced, and no packet");
        sb.AppendLine("contents, pet names or account data are recorded. Log files older than 3 days are deleted automatically.");
        sb.AppendLine();
        sb.AppendLine("==== current status ====");
        sb.AppendLine(sanitizer.Sanitize(statusSnapshot).TrimEnd());
        sb.AppendLine();

        foreach (var file in LogFiles(logFolder))
        {
            sb.AppendLine($"==== {Path.GetFileName(file)} ====");
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                sb.AppendLine(sanitizer.Sanitize(reader.ReadToEnd()).TrimEnd());
            }
            catch (IOException) { sb.AppendLine("(could not be read)"); }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The last <paramref name="count"/> lines across all retained logs (the preview on the Diagnostics tab).</summary>
    public static string Tail(string logFolder, int count)
    {
        DiagLog.Flush();
        var lines = new Queue<string>();
        foreach (var file in LogFiles(logFolder))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > count)
                        lines.Dequeue();
                }
            }
            catch (IOException) { }
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static IReadOnlyList<string> LogFiles(string logFolder) =>
        Directory.Exists(logFolder)
            ? Directory.EnumerateFiles(logFolder, DiagSink.FilePrefix + "*.log").Order(StringComparer.Ordinal).ToList()
            : [];
}
