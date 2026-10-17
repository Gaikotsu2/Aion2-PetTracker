using System.Buffers.Binary;
using System.CommandLine;
using System.Text;
using PetTracker.Core.Protocol;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class SearchCommand
{
    private sealed record Pattern(string Label, byte[] Bytes);
    private sealed record Hit(TimedFrame Frame, int Offset, string Label);

    public static Command Build(CommonOptions common)
    {
        var file = CommandSupport.CaptureArgument();
        var ints = new Option<long[]>("--int") { Description = "Integer value(s) to find (e.g. your soul count).", AllowMultipleArgumentsPerToken = true };
        var intAs = new Option<string>("--as") { Description = "Integer encodings: u8,u16le,u32le,varint.", DefaultValueFactory = _ => "u8,u16le,u32le,varint" };
        var strings = new Option<string[]>("--str") { Description = "Text to find (e.g. a pet name).", AllowMultipleArgumentsPerToken = true };
        var enc = new Option<string>("--enc") { Description = "Text encodings: utf16le,utf8.", DefaultValueFactory = _ => "utf16le,utf8" };
        var limit = CommandSupport.LimitOption(100);
        var command = new Command("search", "Find known values (integers or text) inside decompressed frame bodies; reports opcode and body offset.")
        {
            file, ints, intAs, strings, enc, limit,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var patterns = BuildPatterns(result.GetValue(ints) ?? [], result.GetValue(intAs)!, result.GetValue(strings) ?? [], result.GetValue(enc)!);
            if (patterns.Count == 0)
                throw new InvalidOperationException("Give at least one --int or --str to search for.");

            var data = CommandSupport.LoadCapture(result, common, result.GetValue(file)!);
            var hits = data.Frames.SelectMany(f => Find(f, patterns)).ToList();
            var max = result.GetValue(limit);

            foreach (var hit in max > 0 ? hits.Take(max) : hits)
                Console.WriteLine($"#{hit.Frame.Index,-6} {hit.Frame.Seconds,8:0.000}s {ConsoleOut.OpcodeLabel(hit.Frame.Frame.Opcode, registry),-20} body offset 0x{hit.Offset:x2} ({hit.Offset})  {hit.Label}  {data.NearestMarker(hit.Frame.Ticks)}");
            if (max > 0 && hits.Count > max)
                ConsoleOut.Warn($"... {hits.Count - max} more hits hidden by --limit {max}.");

            Console.WriteLine();
            ConsoleOut.Heading($"{hits.Count} hit(s). Most frequent locations (a real field repeats at the same opcode and offset):");
            foreach (var g in hits.GroupBy(h => (h.Frame.Frame.Opcode, h.Offset, h.Label)).OrderByDescending(g => g.Count()).Take(20))
                Console.WriteLine($"  {g.Count(),5}x  opcode {g.Key.Opcode:x4}  offset 0x{g.Key.Offset:x2}  {g.Key.Label}");
            return 0;
        }));
        return command;
    }

    private static List<Pattern> BuildPatterns(long[] ints, string intFormats, string[] strings, string textFormats)
    {
        var patterns = new List<Pattern>();
        var formats = Split(intFormats);
        foreach (var n in ints)
        {
            foreach (var format in formats)
            {
                switch (format)
                {
                    case "u8" when n is >= 0 and <= byte.MaxValue:
                        patterns.Add(new Pattern($"{n} as u8", [(byte)n]));
                        break;
                    case "u16le" when n is >= 0 and <= ushort.MaxValue:
                        patterns.Add(new Pattern($"{n} as u16le", [(byte)n, (byte)(n >> 8)]));
                        break;
                    case "u32le" when n is >= 0 and <= uint.MaxValue:
                        var four = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)n);
                        patterns.Add(new Pattern($"{n} as u32le", four));
                        break;
                    case "varint" when n >= 0:
                        patterns.Add(new Pattern($"{n} as varint", Varint.Encode((ulong)n)));
                        break;
                    case "u8" or "u16le" or "u32le" or "varint":
                        break;
                    default:
                        throw new FormatException($"Unknown integer encoding '{format}'. Use u8,u16le,u32le,varint.");
                }
            }
        }

        foreach (var s in strings)
        {
            foreach (var format in Split(textFormats))
            {
                patterns.Add(format switch
                {
                    "utf16le" => new Pattern($"\"{s}\" as utf16le", Encoding.Unicode.GetBytes(s)),
                    "utf8" => new Pattern($"\"{s}\" as utf8", Encoding.UTF8.GetBytes(s)),
                    _ => throw new FormatException($"Unknown text encoding '{format}'. Use utf16le,utf8."),
                });
            }
        }
        return patterns;
    }

    private static string[] Split(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).ToArray();

    private static IEnumerable<Hit> Find(TimedFrame frame, List<Pattern> patterns) =>
        patterns.SelectMany(p => Offsets(frame.Frame.Body, p.Bytes).Select(offset => new Hit(frame, offset, p.Label)));

    private static List<int> Offsets(byte[] body, byte[] needle)
    {
        var offsets = new List<int>();
        var from = 0;
        while (from <= body.Length - needle.Length)
        {
            var i = body.AsSpan(from).IndexOf(needle);
            if (i < 0) break;
            offsets.Add(from + i);
            from += i + 1;
        }
        return offsets;
    }
}
