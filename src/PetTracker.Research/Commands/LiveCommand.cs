using System.CommandLine;
using PetTracker.Core;
using PetTracker.Core.Protocol;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class LiveCommand
{
    private const int RareThreshold = 3;

    public static Command Build(CommonOptions common)
    {
        var capture = new CaptureOptions();
        var onlyNew = new Option<bool>("--only-new") { Description = "Print only opcodes that are new or rare (seen fewer than 3 times this session)." };
        var heartbeats = new Option<bool>("--heartbeats") { Description = "Also print heartbeat frames." };
        var command = new Command("live", "Real-time feed of decoded frames. Known opcodes are green, unknown or rare ones yellow.") { onlyNew, heartbeats };
        capture.AddTo(command);

        command.SetAction((result, token) => Task.FromResult(CommandSupport.Run(() =>
            Live(capture.Build(result, common), common.Registry(result), common.WantsAutoDetect(result),
                result.GetValue(onlyNew), result.GetValue(heartbeats), token))));
        return command;
    }

    private static int Live(PipelineOptions options, OpcodeRegistry registry, bool autoDetect, bool onlyNew, bool showHeartbeats, CancellationToken token)
    {
        using var pipeline = new Pipeline(options);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var seen = new Dictionary<ushort, int>();
        var detectBuffer = new List<byte>();
        var detected = !autoDetect;
        var started = DateTime.UtcNow;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        pipeline.RecordProduced += record =>
        {
            switch (record)
            {
                case Core.Recording.ConnectionEvent e:
                    ConsoleOut.Line($"-- {e.Kind}: {e.Detail}", ConsoleColor.DarkGray);
                    break;
                case Core.Recording.S2CChunk c when !detected:
                    detectBuffer.AddRange(c.Data);
                    if (detectBuffer.Count >= 128 * 1024)
                    {
                        detected = true;
                        ConsoleOut.PrintDetectScores(LengthModeDetector.Detect(detectBuffer.ToArray(), heartbeat: options.Decoder.HeartbeatSignature));
                    }
                    break;
            }
        };

        pipeline.FramesDecoded += decoded =>
        {
            foreach (var frame in decoded.Frames)
            {
                var op = frame.Opcode;
                seen[op] = seen.GetValueOrDefault(op) + 1;
                var known = registry.IsKnown(op);
                var rare = !known && seen[op] <= RareThreshold;

                if (!showHeartbeats && registry.Is(MessageNames.Heartbeat, op)) continue;
                if (onlyNew && !rare) continue;

                var text = $"{DateTime.Now:HH:mm:ss.fff} flow {decoded.FlowId} {ConsoleOut.OpcodeLabel(op, registry),-22} {frame.Body.Length,5} B{(frame.Bundled ? " bundled" : "")}" +
                           (rare ? $"  NEW/RARE (#{seen[op]})" : "") +
                           (rare ? "  " + HexFormat.Bytes(frame.Body.AsSpan(0, Math.Min(24, frame.Body.Length))) : "");
                ConsoleOut.Line(text, rare ? ConsoleColor.Yellow : known ? ConsoleColor.Green : ConsoleColor.Gray);
            }
        };

        pipeline.Start();
        Console.WriteLine($"Live feed (length mode {options.Decoder.LengthMode}). Ctrl+C to stop.");

        var lastReport = DateTime.UtcNow;
        while (!cts.IsCancellationRequested)
        {
            cts.Token.WaitHandle.WaitOne(250);
            if (DateTime.UtcNow - lastReport < TimeSpan.FromSeconds(10))
                continue;
            lastReport = DateTime.UtcNow;
            var s = pipeline.Status;
            ConsoleOut.Line($"-- [{s.Phase}] frames {s.Frames} | desyncs {s.Desyncs} | packets {s.Packets} | up {(DateTime.UtcNow - started):mm\\:ss}", ConsoleColor.DarkGray);
        }
        return 0;
    }
}
