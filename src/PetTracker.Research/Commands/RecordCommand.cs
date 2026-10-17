using System.CommandLine;
using System.Collections.Concurrent;
using PetTracker.Core;
using PetTracker.Core.Recording;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class RecordCommand
{
    private readonly record struct Press(Hotkey Key, long Ticks);

    public static Command Build(CommonOptions common)
    {
        var output = new Option<string>("-o", "--output") { Description = "Capture file to write (.a2cap).", Required = true };
        var capture = new CaptureOptions();
        var command = new Command("record", "Record the server stream. F9 drops a numbered marker; F10 drops a marker and asks for a note.") { output };
        capture.AddTo(command);

        command.SetAction((result, token) => Task.FromResult(CommandSupport.Run(() =>
            Record(result.GetValue(output)!, capture.Build(result, common), common.WantsAutoDetect(result), token))));
        return command;
    }

    private static int Record(string path, PipelineOptions options, bool autoDetect, CancellationToken token)
    {
        var (markers, frames, desyncs) = RunSession(path, options, token);
        Console.WriteLine($"Saved {path} with {markers} marker(s). Frames decoded live: {frames}, desyncs: {desyncs}.");
        if (autoDetect)
            ReportBestMode(path);
        return 0;
    }

    private static (int Markers, long Frames, long Desyncs) RunSession(string path, PipelineOptions options, CancellationToken token)
    {
        using var writer = A2CapWriter.Create(path);
        using var pipeline = new Pipeline(options);
        using var hotkeys = new HotkeyListener();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var presses = new ConcurrentQueue<Press>();
        var markers = 0;

        pipeline.RecordProduced += writer.Write;
        hotkeys.Pressed += (key, ticks) => presses.Enqueue(new Press(key, ticks));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        pipeline.Start();
        Console.WriteLine($"Recording to {path}");
        Console.WriteLine("F9 = marker, F10 = marker with a note typed here, Ctrl+C = stop and save.");
        Console.WriteLine("Looking for AION2 ... start the game if it is not running.");
        Console.WriteLine();

        var previousFrames = 0L;
        var nextStatus = DateTime.UtcNow;
        while (!cts.IsCancellationRequested)
        {
            while (presses.TryDequeue(out var press))
                markers = HandlePress(press, writer, markers);

            if (DateTime.UtcNow >= nextStatus)
            {
                var status = pipeline.Status;
                var rate = status.Frames - previousFrames;
                previousFrames = status.Frames;
                nextStatus = DateTime.UtcNow.AddSeconds(1);
                PrintStatus(status, rate, markers, pipeline);
            }
            cts.Token.WaitHandle.WaitOne(100);
        }

        ClearStatusLine();
        var final = pipeline.Status;
        return (markers, final.Frames, final.Desyncs);
    }

    private static int HandlePress(Press press, A2CapWriter writer, int markers)
    {
        markers++;
        ClearStatusLine();
        if (press.Key == Hotkey.F10)
        {
            Console.Write($"Marker {markers} note (Enter to confirm): ");
            var note = Console.ReadLine()?.Trim();
            writer.WriteMarker(press.Ticks, markers, string.IsNullOrEmpty(note) ? null : note);
            ConsoleOut.Line($"Marker M{markers} saved{(string.IsNullOrEmpty(note) ? "" : $": {note}")}", ConsoleColor.Green);
        }
        else
        {
            writer.WriteMarker(press.Ticks, markers);
            ConsoleOut.Line($"Marker M{markers} saved at {DateTime.Now:HH:mm:ss}", ConsoleColor.Green);
        }
        return markers;
    }

    private static void PrintStatus(PipelineStatus status, long framesPerSecond, int markers, Pipeline pipeline)
    {
        var locked = status.ActiveFlowId is { } id && pipeline.FlowName(id) is { } flow ? flow
            : status.Connections.Count > 0 ? string.Join(", ", status.Connections.Select(c => c.Remote.ToString()))
            : "none";
        var line = $"[{status.Phase}] conn: {locked} | {framesPerSecond} frames/s | frames {status.Frames} | desyncs {status.Desyncs} | gaps {status.Gaps} | markers {markers}";
        if (status.Error is not null)
            line += $" | ERROR: {status.Error}";

        if (Console.IsOutputRedirected)
            Console.WriteLine(line);
        else
            Console.Write("\r" + line.PadRight(Math.Max(Console.WindowWidth - 1, line.Length))[..Math.Max(Console.WindowWidth - 1, 20)]);
    }

    private static void ClearStatusLine()
    {
        if (!Console.IsOutputRedirected)
            Console.Write("\r" + new string(' ', Math.Max(Console.WindowWidth - 1, 1)) + "\r");
    }

    private static void ReportBestMode(string path)
    {
        using var reader = A2CapReader.Open(path);
        var scores = CaptureData.DetectMode(reader.ReadAll(), new Core.Protocol.FrameDecoderOptions());
        ConsoleOut.PrintDetectScores(scores);
    }
}
