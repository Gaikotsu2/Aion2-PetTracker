using System.CommandLine;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class HistCommand
{
    private sealed record Row(ushort Opcode, double Average, double Presence, double ControlAverage, double ControlPresence);

    public static Command Build(CommonOptions common)
    {
        var file = CommandSupport.CaptureArgument();
        var around = new Option<string>("--around") { Description = "Markers to examine: M3, 3,4,5, note:drop, nonote or all.", Required = true };
        var control = new Option<string?>("--control") { Description = "Control markers to compare against (same syntax)." };
        var window = CommandSupport.WindowOption();
        var after = CommandSupport.AfterOption();
        var top = new Option<int>("--top") { Description = "Rows to show.", DefaultValueFactory = _ => 40 };
        var command = new Command("hist", "Opcode counts near markers, compared against control markers. Ranks opcodes that appear near one set but not the other.")
        {
            file, around, control, window, after, top,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var data = CommandSupport.LoadCapture(result, common, result.GetValue(file)!);
            var before = DurationParser.Parse(result.GetValue(window)!);
            var afterSpan = DurationParser.Parse(result.GetValue(after)!);

            var targets = MarkerSelector.Select(result.GetValue(around)!, data.Markers);
            var controls = result.GetValue(control) is { } c ? MarkerSelector.Select(c, data.Markers) : [];
            var targetCounts = Count(data, targets, before, afterSpan);
            var controlCounts = Count(data, controls, before, afterSpan);

            var rows = targetCounts.Keys.Union(controlCounts.Keys).Select(op => new Row(
                    op,
                    Average(targetCounts, op, targets.Count), Presence(targetCounts, op, targets.Count),
                    Average(controlCounts, op, controls.Count), Presence(controlCounts, op, controls.Count)))
                .OrderByDescending(r => r.Presence - r.ControlPresence)
                .ThenByDescending(r => r.Average - r.ControlAverage)
                .Take(result.GetValue(top))
                .ToList();

            Console.WriteLine($"Window: {before.TotalSeconds:0.##}s before to {afterSpan.TotalSeconds:0.##}s after each marker.");
            Console.WriteLine($"Target markers ({targets.Count}): {string.Join(" ", targets.Select(m => "M" + m.Number))}");
            if (controls.Count > 0)
                Console.WriteLine($"Control markers ({controls.Count}): {string.Join(" ", controls.Select(m => "M" + m.Number))}");
            Console.WriteLine();
            Console.WriteLine($"{"opcode",-22} {"avg/marker",10} {"in markers",11} {"ctl avg",9} {"ctl in",8}  verdict");
            foreach (var r in rows)
            {
                var verdict = controls.Count == 0 ? "" : r.Presence - r.ControlPresence >= 0.7 ? "<-- candidate" : "";
                ConsoleOut.Line(
                    $"{ConsoleOut.OpcodeLabel(r.Opcode, registry),-22} {r.Average,10:0.0} {r.Presence,10:P0} {r.ControlAverage,9:0.0} {r.ControlPresence,8:P0}  {verdict}",
                    verdict.Length > 0 ? ConsoleColor.Yellow : null);
            }
            return 0;
        }));
        return command;
    }

    /// <summary>For each opcode: how many frames fell in each marker's window, as a list per marker.</summary>
    private static Dictionary<ushort, int[]> Count(CaptureData data, List<Core.Recording.Marker> markers, TimeSpan before, TimeSpan after)
    {
        var counts = new Dictionary<ushort, int[]>();
        for (var i = 0; i < markers.Count; i++)
        {
            var (from, to) = CommandSupport.Window(markers[i], before, after);
            foreach (var f in data.Frames.Where(f => f.Ticks >= from && f.Ticks <= to))
            {
                if (!counts.TryGetValue(f.Frame.Opcode, out var perMarker))
                    counts[f.Frame.Opcode] = perMarker = new int[markers.Count];
                perMarker[i]++;
            }
        }
        return counts;
    }

    private static double Average(Dictionary<ushort, int[]> counts, ushort op, int markers) =>
        markers == 0 || !counts.TryGetValue(op, out var c) ? 0 : c.Sum() / (double)markers;

    private static double Presence(Dictionary<ushort, int[]> counts, ushort op, int markers) =>
        markers == 0 || !counts.TryGetValue(op, out var c) ? 0 : c.Count(n => n > 0) / (double)markers;
}
