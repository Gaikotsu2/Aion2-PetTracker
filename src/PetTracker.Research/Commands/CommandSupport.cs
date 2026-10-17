using System.CommandLine;
using PetTracker.Core.Capture;
using PetTracker.Core.Recording;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class CommandSupport
{
    public static Argument<string> CaptureArgument(string name = "capture") =>
        new(name) { Description = "Path to a .a2cap capture file." };

    public static Option<int> LimitOption(int defaultValue) =>
        new("--limit") { Description = "Maximum rows to print (0 = unlimited).", DefaultValueFactory = _ => defaultValue };

    public static Option<string> WindowOption() =>
        new("--window") { Description = "How far before a marker to look (e.g. 2s).", DefaultValueFactory = _ => "2s" };

    public static Option<string> AfterOption() =>
        new("--after") { Description = "How far after a marker to look.", DefaultValueFactory = _ => "0.5s" };

    public static CaptureData LoadCapture(ParseResult result, CommonOptions common, string path, bool quiet = false)
    {
        var data = CaptureData.Load(path, common.DecoderOptions(result), common.WantsAutoDetect(result));
        if (data.DetectScores is { } scores)
            ConsoleOut.PrintDetectScores(scores);
        if (!quiet)
        {
            ConsoleOut.Line($"{Path.GetFileName(path)}: {data.Frames.Count} frames, {data.Markers.Count} markers, " +
                            $"{data.Duration:0.0}s, length mode {data.Mode}, desyncs {data.Stats.Desyncs}");
            if (data.Stats.Desyncs > 0 && !common.WantsAutoDetect(result))
                ConsoleOut.Warn("  Desyncs found. The default framing is fixed:-4; run `flows` to check the flow is the game stream, or try --auto-detect.");
        }
        return data;
    }

    public static (long Start, long End) Window(Marker marker, TimeSpan before, TimeSpan after) =>
        (marker.Ticks - before.Ticks, marker.Ticks + after.Ticks);

    /// <summary>Runs a command body, turning expected failures into clear messages and exit codes.</summary>
    public static int Run(Func<int> body)
    {
        try
        {
            return body();
        }
        catch (NpcapNotAvailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (!ex.Message.Contains("npcap.com", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(NpcapNotAvailableException.InstallHint);
            return 2;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or InvalidOperationException or IOException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
