using System.CommandLine;
using PetTracker.Core.Domain;
using PetTracker.Core.Messages;
using PetTracker.Core.Persistence;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class ReplayCommand
{
    public static Command Build(CommonOptions common)
    {
        var files = new Argument<string[]>("captures")
        {
            Description = "One or more .a2cap files, replayed in order into one collection (e.g. r2.a2cap r3.a2cap).",
            Arity = ArgumentArity.OneOrMore,
        };
        var print = new Option<bool>("--print") { Description = "Print every frame as it is replayed." };
        var stats = new Option<bool>("--stats") { Description = "Print opcode statistics even when several captures are chained." };
        var state = new Option<string?>("--state") { Description = "Start from a saved collection.json instead of an empty collection." };
        var appEvents = new Option<bool>("--app-events") { Description = "Print only the domain events and the collection summary (no frame statistics)." };
        var limit = CommandSupport.LimitOption(200);
        var command = new Command("replay", "Run captures through the decoding pipeline headless and print domain events plus the final collection summary.")
        {
            files, print, stats, state, appEvents, limit,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var paths = result.GetValue(files)!;
            var catalog = PetCatalog.LoadDefault();
            var collection = new PetCollection();
            var showStats = !result.GetValue(appEvents) && (paths.Length == 1 || result.GetValue(stats));
            var touched = new SortedSet<int>();

            if (result.GetValue(state) is { } statePath)
            {
                collection.Restore(new CollectionStore(statePath).Load());
                Console.WriteLine($"Starting from {statePath}: {collection.Count} pets");
            }

            void Show(DomainEvent e, string when)
            {
                if (DomainEventFormatter.PetIdOf(e) is { } id)
                    touched.Add(id);
                switch (e)
                {
                    case ProgressAdjusted p:
                        ConsoleOut.Line($"{when}    progress {catalog.DisplayName(p.PetId)} -> {p.NewProgress}", ConsoleColor.DarkGray);
                        break;
                    case SnapshotLoaded:
                        ConsoleOut.Line($"{when}  {DomainEventFormatter.Describe(e, catalog)}", ConsoleColor.Green);
                        Console.WriteLine($"snapshot: {CollectionSummary.From(collection.Pets)}");
                        Console.WriteLine($"snapshot (game-style): {catalog.GameCount(collection.Pets)}");
                        break;
                    case Inconsistency:
                        ConsoleOut.Line($"{when}  {DomainEventFormatter.Describe(e, catalog)}", ConsoleColor.Yellow);
                        break;
                    default:
                        Console.WriteLine($"{when}  {DomainEventFormatter.Describe(e, catalog)}");
                        break;
                }
            }

            foreach (var path in paths)
            {
                var data = CommandSupport.LoadCapture(result, common, path);

                if (result.GetValue(print))
                {
                    var max = result.GetValue(limit);
                    foreach (var f in max > 0 ? data.Frames.Take(max) : data.Frames)
                        Console.WriteLine($"{f.Seconds,8:0.000}s  {ConsoleOut.OpcodeLabel(f.Frame.Opcode, registry),-22} {f.Frame.Body.Length,5} B  {(f.Frame.Bundled ? "bundled" : "")}");
                }

                foreach (var f in data.Frames)
                {
                    var when = $"{f.Seconds,8:0.000}s";
                    var now = new DateTime(f.Ticks, DateTimeKind.Utc);
                    foreach (var message in PetMessageParser.Extract(registry, [f.Frame], err => ConsoleOut.Warn($"{when}  unparsable: {err}")))
                    {
                        if (message is PetLevelChangedMessage or PetSoulsConsumedMessage or PetSoulsGainedMessage)
                            ConsoleOut.Line($"{when}  < {message.GetType().Name.Replace("Message", "")} {Describe(message)}", ConsoleColor.DarkGray);
                        foreach (var e in collection.Apply(message, now))
                            Show(e, when);
                    }
                    foreach (var e in collection.Tick(now))
                        Show(e, when);
                }

                if (showStats)
                    PrintStats(data, registry);
            }

            foreach (var e in collection.Flush())
                Show(e, "     end ");

            Console.WriteLine();
            Console.WriteLine($"final: {CollectionSummary.From(collection.Pets)}");
            Console.WriteLine($"collection (game-style, Special excluded): {catalog.GameCount(collection.Pets)}");
            foreach (var id in touched)
            {
                if (collection.Get(id) is not { } p)
                    continue;
                var threshold = collection.Rules.ThresholdFor(p.Level);
                Console.WriteLine($"  pet {id} {catalog.DisplayName(id)}: {(p.Registered ? "L" + p.Level : "unregistered")} " +
                                  $"{(threshold is { } t ? $"{p.Progress}/{t}" : "MAX")}{(p.NeedsResync ? "  NEEDS RESYNC" : "")}");
            }
            return 0;
        }));
        return command;
    }

    private static string Describe(PetMessage message) => message switch
    {
        PetSoulsGainedMessage g => string.Join(", ", g.Entries.Select(e => $"#{e.PetId} +{e.Amount}")),
        PetSoulsConsumedMessage c => string.Join(", ", c.Entries.Select(e => $"#{e.PetId} -{e.Amount}")),
        PetLevelChangedMessage l => $"#{l.PetId} -> L{l.NewLevel}",
        _ => "",
    };

    private static void PrintStats(CaptureData data, PetTracker.Core.Protocol.OpcodeRegistry registry)
    {
        Console.WriteLine();
        ConsoleOut.Heading("Opcode statistics");
        Console.WriteLine($"{"opcode",-22} {"count",8} {"per min",9} {"avg body",9} {"max body",9}");
        foreach (var g in data.Frames.GroupBy(f => f.Frame.Opcode).OrderByDescending(g => g.Count()).Take(40))
        {
            var known = registry.IsKnown(g.Key);
            ConsoleOut.Line(
                $"{ConsoleOut.OpcodeLabel(g.Key, registry),-22} {g.Count(),8} {g.Count() * 60 / Math.Max(data.Duration, 1),9:0.0} {g.Average(f => f.Frame.Body.Length),9:0.0} {g.Max(f => f.Frame.Body.Length),9}",
                known ? ConsoleColor.Green : null);
        }

        var s = data.Stats;
        Console.WriteLine();
        Console.WriteLine($"Duration {data.Duration:0.0}s | S2C bytes {data.ChunkBytes} | frames {s.Frames} | heartbeats {s.Heartbeats} | desyncs {s.Desyncs} | rejected flows {s.FlowsRejected}");
        Console.WriteLine($"Bundles {s.Bundles} (inner frames {s.InnerFrames}): {s.BundlesBlock} LZ4 block, {s.BundlesFrame} LZ4 frame, {s.BundleFailures} failed | last working format: {data.BundleFormat}");
        foreach (var m in data.Markers)
            Console.WriteLine($"  M{m.Number} at {data.Seconds(m.Ticks):0.00}s {m.Note}");
    }
}
