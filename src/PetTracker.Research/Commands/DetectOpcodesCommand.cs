using System.CommandLine;
using PetTracker.Core.Domain;
using PetTracker.Core.Protocol;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class DetectOpcodesCommand
{
    public static Command Build(CommonOptions common)
    {
        var files = new Argument<string[]>("captures")
        {
            Description = "One or more .a2cap files (a login, ideally with a soul drop and a level-up). Examined in order as one stream.",
            Arity = ArgumentArity.OneOrMore,
        };
        var write = new Option<string?>("--write")
        {
            Description = "Write an opcodes.json override with the detected opcodes that differ from the current map (copy it to %AppData%\\Aion2PetTracker\\opcodes.json).",
        };
        var minConfidence = new Option<double>("--min-confidence")
        {
            Description = "Lowest confidence (0..1) that is still proposed.",
            DefaultValueFactory = _ => OpcodeDetection.DefaultMinConfidence,
        };
        var command = new Command("detect-opcodes",
            "Find the pet messages by their shape (not their opcode) and propose the opcode map, with a diff against the current one. For patch days.")
        {
            files, write, minConfidence,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var catalog = PetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "pets.json"), null);
            if (!catalog.KnownIds.Any())
                throw new InvalidOperationException("data/pets.json (the pet catalog) was not found next to the tool.");

            var frames = new List<ObservedFrame>();
            foreach (var path in result.GetValue(files)!)
            {
                var data = CommandSupport.LoadCapture(result, common, path);
                frames.AddRange(data.Frames.Select(f => new ObservedFrame(f.Ticks, f.Frame)));
            }

            var detection = new PetOpcodeDetector(catalog).Detect(frames);
            var min = result.GetValue(minConfidence);

            Console.WriteLine();
            ConsoleOut.Heading($"Candidates ({detection.FramesExamined} frames examined)");
            if (detection.Candidates.Count == 0)
                ConsoleOut.Warn("  Nothing here has the shape of a pet message.");
            foreach (var c in detection.Candidates)
            {
                ConsoleOut.Line($"  {c.LogicalName,-22} {c.OpcodeHex}  confidence {c.Confidence:0.00}  {c.Matches}/{c.Frames} frames", c.Confidence >= min ? ConsoleColor.Green : ConsoleColor.DarkGray);
                Console.WriteLine($"      {c.Evidence}");
                Console.WriteLine($"      sample: {c.SampleHex}");
            }

            var proposal = detection.Mapping(min);
            var current = registry.ToDictionary();
            Console.WriteLine();
            ConsoleOut.Heading("Proposal vs current registry");
            Console.WriteLine($"  {"message",-22} {"current",-14} {"detected",-14} result");
            var changed = new Dictionary<string, IReadOnlyList<ushort>>();
            foreach (var name in current.Keys.Union(proposal.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                current.TryGetValue(name, out var now);
                proposal.TryGetValue(name, out var found);
                var nowText = now is null ? "-" : OpcodeOverrides.FormatList(now);
                var foundText = found is null ? "-" : OpcodeOverrides.FormatList(found);
                string verdict;
                ConsoleColor? color;
                if (found is null)
                {
                    verdict = "not detected (unchanged)";
                    color = ConsoleColor.DarkGray;
                }
                else if (now is not null && now.Order().SequenceEqual(found.Order()))
                {
                    verdict = "same";
                    color = ConsoleColor.Green;
                }
                else
                {
                    verdict = "DIFFERENT";
                    color = ConsoleColor.Yellow;
                    changed[name] = found;
                }
                ConsoleOut.Line($"  {name,-22} {nowText,-14} {foundText,-14} {verdict}", color);
            }

            if (result.GetValue(write) is { } target)
            {
                Console.WriteLine();
                if (changed.Count == 0)
                {
                    Console.WriteLine("Nothing differs from the current map; no override written.");
                }
                else
                {
                    var full = Path.GetFullPath(target);
                    File.WriteAllText(full, OpcodeOverrides.ToJson(changed));
                    Console.WriteLine($"Wrote {changed.Count} override(s) to {full}");
                }
            }
            return 0;
        }));
        return command;
    }
}
