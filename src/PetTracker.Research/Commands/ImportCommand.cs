using System.CommandLine;
using PetTracker.Core;
using PetTracker.Core.Capture;
using PetTracker.Core.Recording;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class ImportCommand
{
    public static Command Build(CommonOptions common)
    {
        var file = new Argument<string>("capture") { Description = "Offline capture to import (.pcap or .pcapng), e.g. recorded with Wireshark's dumpcap." };
        var output = new Option<string>("-o", "--output") { Description = "Capture file to write (.a2cap).", Required = true };
        var ports = new Option<int[]>("--server-port")
        {
            Description = "Treat flows whose server uses this TCP port as the game (repeatable). Without it, game flows are found by their heartbeat/framing.",
            AllowMultipleArgumentsPerToken = true,
        };
        var markers = new Option<string?>("--markers")
        {
            Description = "Text file with one marker per line: wall-clock time HH:mm:ss[.fff] (local time) and an optional note.",
        };
        var command = new Command("import", "Convert a pcap/pcapng recording into an .a2cap capture using the same flow classification as 'record'. No Npcap needed.") { file, output, ports, markers };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var input = result.GetValue(file)!;
            var path = result.GetValue(output)!;
            if (!File.Exists(input))
                throw new FileNotFoundException($"Capture file not found: {input}");

            var entries = result.GetValue(markers) is { } markerPath ? MarkerFile.ParseFile(markerPath) : [];
            var options = new ImportOptions
            {
                Pipeline = new PipelineOptions
                {
                    Decoder = common.DecoderOptions(result),
                    ForcedServerPorts = result.GetValue(ports) ?? [],
                },
                Markers = entries,
            };

            ImportResult summary;
            using (var writer = A2CapWriter.Create(path))
                summary = CaptureImporter.Import(() => CaptureFileReader.ReadFile(input), options, writer.Write);

            if (summary.Packets == 0)
            {
                ConsoleOut.Warn($"{input} contains no packets. Wrote an empty {path}.");
                return 1;
            }

            Console.WriteLine($"Imported {summary.Packets} packets ({summary.TcpPackets} TCP) from {Path.GetFileName(input)}: " +
                              $"{summary.Start!.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {summary.End!.Value.ToLocalTime():HH:mm:ss} local time.");
            Console.WriteLine($"Saved {path}: {summary.Records} records, {summary.GameFlowsAccepted} game flow(s) accepted, {summary.Markers} marker(s).");
            if (summary.MarkersOutsideCapture > 0)
                ConsoleOut.Warn($"  {summary.MarkersOutsideCapture} marker time(s) fall outside the capture. Check the clock/date in {result.GetValue(markers)}.");
            if (summary.GameFlowsAccepted == 0)
                ConsoleOut.Warn("  No game flow was recognised. Pass --server-port N with the game server's port, or check the capture filter.");

            var data = CaptureData.Load(path, common.DecoderOptions(result), autoDetect: false);
            Console.WriteLine($"Decoded {data.Frames.Count} frames, {data.Stats.Desyncs} desyncs. Next: pt-research flows {path}");
            return 0;
        }));
        return command;
    }
}
