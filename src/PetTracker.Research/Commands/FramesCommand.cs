using System.CommandLine;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class FramesCommand
{
    public static Command Build(CommonOptions common)
    {
        var file = CommandSupport.CaptureArgument();
        var opcode = new Option<string?>("--opcode") { Description = "Only frames with this opcode (e.g. 3336)." };
        var limit = CommandSupport.LimitOption(200);
        var skipHeartbeat = new Option<bool>("--skip-heartbeat") { Description = "Hide heartbeat frames." };
        var command = new Command("frames", "List every decoded frame: time, opcode, length, bundled or not, nearest marker.")
        {
            file, opcode, limit, skipHeartbeat,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            var data = CommandSupport.LoadCapture(result, common, result.GetValue(file)!);
            var wanted = result.GetValue(opcode) is { } text ? (ushort?)Core.Protocol.OpcodeRegistry.ParseOpcode(text) : null;
            var max = result.GetValue(limit);
            var hideHeartbeat = result.GetValue(skipHeartbeat);

            Console.WriteLine($"{"#",6} {"time",9} {"flow",4} {"opcode",-22} {"body",6} {"bundled",-7} marker");
            var printed = 0;
            foreach (var f in data.Frames)
            {
                if (wanted is { } w && f.Frame.Opcode != w) continue;
                if (hideHeartbeat && registry.Is(Core.Protocol.MessageNames.Heartbeat, f.Frame.Opcode)) continue;
                if (max > 0 && printed >= max)
                {
                    ConsoleOut.Warn($"... stopped at --limit {max}. Use --limit 0 for everything.");
                    break;
                }

                var known = registry.IsKnown(f.Frame.Opcode);
                ConsoleOut.Line(
                    $"{f.Index,6} {f.Seconds,8:0.000}s {f.FlowId,4} {ConsoleOut.OpcodeLabel(f.Frame.Opcode, registry),-22} {f.Frame.Body.Length,6} {(f.Frame.Bundled ? "yes" : ""),-7} {data.NearestMarker(f.Ticks)}",
                    known ? ConsoleColor.Green : null);
                printed++;
            }

            PrintSummary(data);
            return 0;
        }));
        return command;
    }

    private static void PrintSummary(CaptureData data)
    {
        var s = data.Stats;
        Console.WriteLine();
        Console.WriteLine($"Frames {s.Frames} | heartbeats {s.Heartbeats} | desyncs {s.Desyncs} | rejected flows {s.FlowsRejected} | discarded bytes {s.BytesDiscarded}");
        Console.WriteLine($"Bundles {s.Bundles} (inner frames {s.InnerFrames}): {s.BundlesBlock} LZ4 block, {s.BundlesFrame} LZ4 frame, {s.BundleFailures} failed | last working format: {data.BundleFormat}");
        var gaps = data.Events.Count(e => e.Kind == Core.Recording.ConnectionEventKind.Gap);
        Console.WriteLine($"Flows: {data.Events.Count(e => e.Kind is Core.Recording.ConnectionEventKind.FlowOpened or Core.Recording.ConnectionEventKind.FlowJoined)} | TCP gaps {gaps}");
    }
}
