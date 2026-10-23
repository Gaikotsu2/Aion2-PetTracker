using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace PetTracker.Core.Diagnostics;

public enum DiagLevel { Info, Warn, Error }

/// <summary>
/// Writes sanitized, timestamped text lines to one file per UTC day (pettracker-yyyyMMdd.log) from a background thread, so the
/// capture pipeline never waits on disk. Files from before the retention window are deleted; each day's file has a size cap.
/// </summary>
public sealed class DiagSink : IDisposable
{
    public const string FilePrefix = "pettracker-";
    public const int RetentionDays = 3;
    public const long DefaultDayCapBytes = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<(DateTime Utc, string Line, DiagLevel Level)> _queue = new();
    private readonly Func<DateTime> _clock;
    private readonly long _dayCap;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private DateTime _lastPrune = DateTime.MinValue;
    private DateOnly _capDay;
    private long _capWritten;
    private bool _capNoticed;

    public DiagSink(string folder, LogSanitizer? sanitizer = null, Func<DateTime>? clock = null, long dayCapBytes = DefaultDayCapBytes)
    {
        Folder = folder;
        Sanitizer = sanitizer ?? new LogSanitizer();
        _clock = clock ?? (() => DateTime.UtcNow);
        _dayCap = dayCapBytes;
        Prune();
        _loop = Task.Run(RunAsync);
    }

    public string Folder { get; }
    public LogSanitizer Sanitizer { get; }

    public static string FileNameFor(DateTime utc) => $"{FilePrefix}{utc:yyyyMMdd}.log";

    public void Write(DiagLevel level, string area, string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message}{Environment.NewLine}{Describe(ex)}";
        text = Sanitizer.Sanitize(text).Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    ");
        var tag = level switch { DiagLevel.Warn => "WRN", DiagLevel.Error => "ERR", _ => "INF" };
        var now = _clock();
        _queue.Enqueue((now, $"{now:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{tag}] {Sanitizer.Sanitize(area)}: {text}", level));
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e != ex)
                sb.AppendLine("--- inner exception ---");
            sb.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
            if (e.StackTrace is { } trace)
                sb.AppendLine(trace);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Writes everything queued so far (called by the background loop, a report, and on exit).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                var now = _clock();
                if (now - _lastPrune > TimeSpan.FromHours(1))
                    Prune();
                Directory.CreateDirectory(Folder);
                foreach (var group in DrainByDay())
                {
                    var path = Path.Combine(Folder, FileNameFor(group.Key));
                    var day = DateOnly.FromDateTime(group.Key);
                    if (day != _capDay)
                    {
                        _capDay = day;
                        _capWritten = File.Exists(path) ? new FileInfo(path).Length : 0;
                        _capNoticed = false;
                    }

                    var text = new StringBuilder();
                    foreach (var (line, level) in group)
                    {
                        // Past the cap only errors get through, up to half again; a runaway can never fill the disk.
                        var over = _capWritten + text.Length >= _dayCap;
                        if (over && (level != DiagLevel.Error || _capWritten + text.Length >= _dayCap + _dayCap / 2))
                        {
                            if (!_capNoticed)
                            {
                                _capNoticed = true;
                                text.AppendLine($"{group.Key:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [WRN] Diag: daily log size cap reached; further non-error lines are dropped until tomorrow");
                            }
                            continue;
                        }
                        text.AppendLine(line);
                    }
                    if (text.Length == 0)
                        continue;
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                    stream.Write(bytes);
                    _capWritten += bytes.Length;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private IEnumerable<IGrouping<DateTime, (string Line, DiagLevel Level)>> DrainByDay()
    {
        var items = new List<(DateTime Utc, string Line, DiagLevel Level)>();
        while (_queue.TryDequeue(out var item))
            items.Add(item);
        return items.GroupBy(i => i.Utc.Date, i => (i.Line, i.Level));
    }

    /// <summary>Deletes log files older than the retention window: today and the two days before it are kept, so nothing is more than 3 days old.</summary>
    public void Prune()
    {
        _lastPrune = _clock();
        try
        {
            if (!Directory.Exists(Folder))
                return;
            var oldest = _clock().Date.AddDays(-(RetentionDays - 1));
            foreach (var file in Directory.EnumerateFiles(Folder, "*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var stale = name.StartsWith(FilePrefix, StringComparison.Ordinal)
                    && DateTime.TryParseExact(name[FilePrefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                    ? day < oldest
                    : File.GetLastWriteTimeUtc(file) < _clock().AddDays(-RetentionDays);
                if (stale)
                    TryDelete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes every log file (the Clear logs button).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out _)) { }
            try
            {
                if (Directory.Exists(Folder))
                    foreach (var file in Directory.EnumerateFiles(Folder, "*.log"))
                        TryDelete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _capDay = default;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
                Flush();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        Flush();
        _cts.Dispose();
    }
}

/// <summary>Process-wide diagnostics log. Does nothing until <see cref="Start"/> is called, so tests and the research CLI stay silent.</summary>
public static class DiagLog
{
    private static DiagSink? _sink;

    public static bool IsStarted => _sink is not null;
    public static string? Folder => _sink?.Folder;
    public static LogSanitizer? Sanitizer => _sink?.Sanitizer;

    public static void Start(string folder)
    {
        Stop();
        _sink = new DiagSink(folder);
    }

    public static void Info(string area, string message) => _sink?.Write(DiagLevel.Info, area, message);
    public static void Warn(string area, string message, Exception? ex = null) => _sink?.Write(DiagLevel.Warn, area, message, ex);
    public static void Error(string area, string message, Exception? ex = null) => _sink?.Write(DiagLevel.Error, area, message, ex);

    public static void Flush() => _sink?.Flush();
    public static void Clear() => _sink?.Clear();

    public static void Stop()
    {
        var sink = Interlocked.Exchange(ref _sink, null);
        sink?.Dispose();
    }
}
