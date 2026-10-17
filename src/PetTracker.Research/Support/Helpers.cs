using System.Globalization;
using System.Text;
using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Research.Support;

public static class DurationParser
{
    public static TimeSpan Parse(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        var (number, unit) = t.EndsWith("ms", StringComparison.Ordinal) ? (t[..^2], 0.001)
            : t.EndsWith('m') ? (t[..^1], 60)
            : t.EndsWith('s') ? (t[..^1], 1)
            : (t, 1);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"'{text}' is not a duration. Examples: 2s, 500ms, 1.5s.");
        return TimeSpan.FromSeconds(value * unit);
    }
}

/// <summary>Selects markers by "M3", "3", "3,4,7", "note:drop" (note contains text), "nonote" or "all".</summary>
public static class MarkerSelector
{
    public static List<Marker> Select(string spec, IReadOnlyList<Marker> markers)
    {
        var chosen = new List<Marker>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token == "all")
                chosen.AddRange(markers);
            else if (token == "nonote")
                chosen.AddRange(markers.Where(m => string.IsNullOrEmpty(m.Note)));
            else if (token.StartsWith("note:", StringComparison.Ordinal))
                chosen.AddRange(markers.Where(m => m.Note?.Contains(raw[5..], StringComparison.OrdinalIgnoreCase) == true));
            else if (int.TryParse(token.TrimStart('m'), out var number))
                chosen.AddRange(markers.Where(m => m.Number == number));
            else
                throw new FormatException($"Cannot understand marker '{raw}'. Use M3, 3,4,5, note:text, nonote or all.");
        }

        var result = chosen.DistinctBy(m => m.Number).OrderBy(m => m.Number).ToList();
        if (result.Count == 0)
            throw new InvalidOperationException($"No markers match '{spec}'. This capture has {markers.Count} marker(s).");
        return result;
    }
}

public static class HexFormat
{
    public static string Bytes(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (var b in data)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
        return sb.ToString().TrimEnd();
    }

    public static IEnumerable<string> Dump(ReadOnlySpan<byte> data, ReadOnlySpan<bool> variable = default)
    {
        var lines = new List<string>();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var row = data.Slice(offset, Math.Min(16, data.Length - offset));
            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            for (var i = 0; i < 16; i++)
            {
                if (i < row.Length)
                {
                    hex.Append(row[i].ToString("x2", CultureInfo.InvariantCulture));
                    var marked = !variable.IsEmpty && variable[offset + i];
                    hex.Append(marked ? '*' : ' ');
                    ascii.Append(row[i] is >= 0x20 and < 0x7F ? (char)row[i] : '.');
                }
                else
                {
                    hex.Append("   ");
                }
                if (i == 7) hex.Append(' ');
            }
            lines.Add($"{offset:x4}  {hex} |{ascii}|");
        }
        return lines;
    }
}

public static class ConsoleOut
{
    public static void Line(string text, ConsoleColor? color = null)
    {
        if (color is null || Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
            return;
        }
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void Heading(string text) => Line(text, ConsoleColor.Cyan);
    public static void Warn(string text) => Line(text, ConsoleColor.Yellow);

    public static string OpcodeLabel(ushort opcode, OpcodeRegistry registry)
    {
        var name = registry.Describe(opcode);
        return name.Length == 0 ? opcode.ToString("x4") : $"{opcode:x4} {name}";
    }

    public static void PrintDetectScores(IReadOnlyList<LengthModeScore> scores)
    {
        Heading("Length mode detection (consecutive frames that chain cleanly):");
        foreach (var s in scores)
            Console.WriteLine($"  {s.Mode,-16} {s.Frames,8} frames  (from byte {s.StartOffset})");
        if (scores.Count == 0 || scores[0].Frames == 0)
            Warn("  No mode chained any frames. The stream may not be a plaintext S2C stream, or the framing hypothesis is wrong.");
        else
            Console.WriteLine($"  Best: {scores[0].Mode}");
    }
}
