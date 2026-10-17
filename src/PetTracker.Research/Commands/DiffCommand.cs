using System.CommandLine;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class DiffCommand
{
    public static Command Build(CommonOptions common)
    {
        var a = CommandSupport.CaptureArgument("capture-a");
        var b = CommandSupport.CaptureArgument("capture-b");
        var top = new Option<int>("--top") { Description = "Rows per section.", DefaultValueFactory = _ => 30 };
        var command = new Command("diff", "Compare two captures: opcodes only in one, and the biggest changes in frequency (per minute).")
        {
            a, b, top,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var first = CommandSupport.LoadCapture(result, common, result.GetValue(a)!);
            var second = CommandSupport.LoadCapture(result, common, result.GetValue(b)!);
            var countsA = Counts(first);
            var countsB = Counts(second);
            var rateA = 60 / Math.Max(first.Duration, 1);
            var rateB = 60 / Math.Max(second.Duration, 1);
            var rows = result.GetValue(top);
            string Label(ushort op) => ConsoleOut.OpcodeLabel(op, registry);

            Console.WriteLine();
            ConsoleOut.Heading("Only in A:");
            foreach (var (op, n) in countsA.Where(kv => !countsB.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(rows))
                Console.WriteLine($"  {Label(op),-22} {n,7}x ({n * rateA:0.0}/min)");

            ConsoleOut.Heading("Only in B:");
            foreach (var (op, n) in countsB.Where(kv => !countsA.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(rows))
                Console.WriteLine($"  {Label(op),-22} {n,7}x ({n * rateB:0.0}/min)");

            ConsoleOut.Heading("Biggest frequency changes (per minute, A -> B):");
            var common2 = countsA.Keys.Intersect(countsB.Keys)
                .Select(op => (Op: op, A: countsA[op] * rateA, B: countsB[op] * rateB))
                .OrderByDescending(x => Math.Abs(x.B - x.A))
                .Take(rows);
            foreach (var (op, ra, rb) in common2)
                Console.WriteLine($"  {Label(op),-22} {ra,8:0.0} -> {rb,8:0.0}  ({(ra == 0 ? 0 : rb / ra):0.00}x)");
            return 0;
        }));
        return command;
    }

    private static Dictionary<ushort, int> Counts(CaptureData data) =>
        data.Frames.GroupBy(f => f.Frame.Opcode).ToDictionary(g => g.Key, g => g.Count());
}
