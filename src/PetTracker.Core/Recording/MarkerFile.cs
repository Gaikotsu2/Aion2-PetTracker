using System.Globalization;
using System.Text.RegularExpressions;

namespace PetTracker.Core.Recording;

/// <summary>One line of a markers.txt file: a wall-clock time of day and an optional note.</summary>
public sealed record MarkerEntry(TimeOnly Time, string? Note);

/// <summary>
/// Markers for captures that have none of their own (pcapng): a text file with one marker per line,
/// <c>HH:mm:ss[.fff] [note]</c>, in local time. Blank lines and lines starting with # are ignored.
/// </summary>
public static partial class MarkerFile
{
    [GeneratedRegex(@"^(\d{1,2}):(\d{2}):(\d{2})(?:[.,](\d{1,7}))?(?:\s+(.*))?$")]
    private static partial Regex LinePattern();

    public static IReadOnlyList<MarkerEntry> ParseFile(string path) => Parse(File.ReadAllLines(path));

    public static IReadOnlyList<MarkerEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<MarkerEntry>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#')
                continue;

            var match = LinePattern().Match(line);
            if (!match.Success)
                throw new FormatException($"markers line {number}: expected 'HH:mm:ss[.fff] [note]', got '{line}'.");

            var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (hours > 23 || minutes > 59 || seconds > 59)
                throw new FormatException($"markers line {number}: '{match.Groups[1].Value}:{match.Groups[2].Value}:{match.Groups[3].Value}' is not a valid time.");

            var fraction = match.Groups[4].Success ? long.Parse(match.Groups[4].Value.PadRight(7, '0'), CultureInfo.InvariantCulture) : 0;
            var time = new TimeOnly(hours, minutes, seconds).Add(TimeSpan.FromTicks(fraction));
            var note = match.Groups[5].Success && match.Groups[5].Value.Trim().Length > 0 ? match.Groups[5].Value.Trim() : null;
            entries.Add(new MarkerEntry(time, note));
        }
        return entries;
    }

    /// <summary>
    /// Converts local clock times to markers on the capture's UTC timeline. Each time is placed on the capture's start
    /// date (local); a time more than a minute before the capture started is taken to be after midnight, i.e. the next day.
    /// Markers are returned in time order and numbered from 1.
    /// </summary>
    public static IReadOnlyList<Marker> Resolve(IReadOnlyList<MarkerEntry> entries, long captureStartUtcTicks, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(captureStartUtcTicks, DateTimeKind.Utc), zone);

        var resolved = new List<(long Ticks, string? Note)>();
        foreach (var entry in entries)
        {
            var local = DateTime.SpecifyKind(startLocal.Date + entry.Time.ToTimeSpan(), DateTimeKind.Unspecified);
            if (local < startLocal.AddMinutes(-1))
                local = local.AddDays(1);
            if (zone.IsInvalidTime(local))
                local = local.AddHours(1);
            resolved.Add((TimeZoneInfo.ConvertTimeToUtc(local, zone).Ticks, entry.Note));
        }

        return resolved.OrderBy(r => r.Ticks)
            .Select((r, i) => new Marker(r.Ticks, i + 1, r.Note))
            .ToList();
    }
}
