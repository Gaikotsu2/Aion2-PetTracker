using System.CommandLine;
using PetTracker.Core.Protocol;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class HexCommand
{
    public static Command Build(CommonOptions common)
    {
        var file = CommandSupport.CaptureArgument();
        var opcode = new Option<string>("--opcode") { Description = "Opcode to dump, 4 hex digits (e.g. 1e56).", Required = true };
        var limit = CommandSupport.LimitOption(20);
        var around = new Option<string?>("--around") { Description = "Only frames near these markers (M3, 3,4, note:drop, nonote, all)." };
        var window = CommandSupport.WindowOption();
        var after = CommandSupport.AfterOption();
        var command = new Command("hex", "Hex-dump the bodies of one opcode, marking byte offsets that vary between frames of the same length (*).")
        {
            file, opcode, limit, around, window, after,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var data = CommandSupport.LoadCapture(result, common, result.GetValue(file)!);
            var wanted = OpcodeRegistry.ParseOpcode(result.GetValue(opcode)!);
            var frames = data.Frames.Where(f => f.Frame.Opcode == wanted);

            if (result.GetValue(around) is { } spec)
            {
                var before = DurationParser.Parse(result.GetValue(window)!);
                var afterSpan = DurationParser.Parse(result.GetValue(after)!);
                var windows = MarkerSelector.Select(spec, data.Markers).Select(m => CommandSupport.Window(m, before, afterSpan)).ToList();
                frames = frames.Where(f => windows.Any(w => f.Ticks >= w.Start && f.Ticks <= w.End));
            }

            var max = result.GetValue(limit);
            var selected = (max > 0 ? frames.Take(max) : frames).ToList();
            if (selected.Count == 0)
            {
                ConsoleOut.Warn($"No frames with opcode {wanted:x4} found.");
                return 0;
            }

            var variable = VariableMasks(selected);
            ConsoleOut.Heading($"{selected.Count} frame(s) with opcode {ConsoleOut.OpcodeLabel(wanted, registry)}");
            foreach (var f in selected)
            {
                ConsoleOut.Line($"-- #{f.Index} t={f.Seconds:0.000}s flow {f.FlowId} body {f.Frame.Body.Length} bytes {data.NearestMarker(f.Ticks)}", ConsoleColor.DarkCyan);
                foreach (var line in HexFormat.Dump(f.Frame.Body, variable[f.Frame.Body.Length]))
                    Console.WriteLine("   " + line);
            }

            PrintVariation(selected, variable);
            return 0;
        }));
        return command;
    }

    /// <summary>For each body length, which byte offsets differ across the selected frames.</summary>
    private static Dictionary<int, bool[]> VariableMasks(List<TimedFrame> frames)
    {
        var masks = new Dictionary<int, bool[]>();
        foreach (var group in frames.GroupBy(f => f.Frame.Body.Length))
        {
            var mask = new bool[group.Key];
            var first = group.First().Frame.Body;
            foreach (var f in group.Skip(1))
                for (var i = 0; i < mask.Length; i++)
                    mask[i] |= f.Frame.Body[i] != first[i];
            masks[group.Key] = mask;
        }
        return masks;
    }

    private static void PrintVariation(List<TimedFrame> frames, Dictionary<int, bool[]> masks)
    {
        Console.WriteLine();
        foreach (var group in frames.GroupBy(f => f.Frame.Body.Length).OrderByDescending(g => g.Count()))
        {
            if (group.Count() < 2)
                continue;
            var mask = masks[group.Key];
            var ranges = new List<string>();
            for (var i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                var start = i;
                while (i + 1 < mask.Length && mask[i + 1]) i++;
                ranges.Add(start == i ? $"0x{start:x2}" : $"0x{start:x2}-0x{i:x2}");
            }
            Console.WriteLine($"Body length {group.Key} ({group.Count()} frames): varying offsets: {(ranges.Count == 0 ? "none (identical)" : string.Join(", ", ranges))}");
        }
        if (frames.Select(f => f.Frame.Body.Length).Distinct().Count() > 1)
            ConsoleOut.Warn("Frames have different lengths: this opcode probably carries a list or strings; compare same-length groups.");
    }
}
