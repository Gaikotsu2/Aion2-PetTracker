using System.CommandLine;
using PetTracker.Core.Protocol;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class FlowsCommand
{
    public static Command Build(CommonOptions common)
    {
        var file = CommandSupport.CaptureArgument();
        var all = new Option<bool>("--all") { Description = "Also list flows that carried no data." };
        var command = new Command("flows", "List every TCP flow in a capture: endpoints, bytes, classification (game / TLS / unknown), frames and desyncs.") { file, all };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var data = CommandSupport.LoadCapture(result, common, result.GetValue(file)!, quiet: true);
            var showEmpty = result.GetValue(all);

            Console.WriteLine($"{"flow",4} {"class",-8} {"bytes",10} {"frames",8} {"hb",6} {"desyncs",7}  endpoints / note");
            foreach (var f in data.Flows)
            {
                if (f.Bytes == 0 && !showEmpty) continue;
                var cls = f.Class switch { FlowClass.Game => "game", FlowClass.Tls => "TLS", _ => "unknown" };
                var endpoints = f.Endpoints is { Length: > 2 } e ? e : "(endpoints not recorded)";
                var note = f.SawSyn ? "" : " [joined mid-stream]";
                if (f.Reason is not null) note += $" [rejected: {f.Reason}]";
                ConsoleOut.Line($"{f.FlowId,4} {cls,-8} {f.Bytes,10} {f.Frames,8} {f.Heartbeats,6} {f.Desyncs,7}  {endpoints}{note}",
                    f.Class == FlowClass.Game ? ConsoleColor.Green : null);
            }

            var s = data.Stats;
            Console.WriteLine();
            Console.WriteLine($"{data.Flows.Count(f => f.Class == FlowClass.Game)} game flow(s) | {s.FlowsRejected} rejected | " +
                              $"frames {s.Frames} | bundles {s.Bundles} (inner frames {s.InnerFrames}) | desyncs {s.Desyncs}");
            return 0;
        }));
        return command;
    }
}
