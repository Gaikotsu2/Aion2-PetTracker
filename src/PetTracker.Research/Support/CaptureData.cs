using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Research.Support;

public sealed record TimedFrame(int Index, long Ticks, double Seconds, int FlowId, Frame Frame);

/// <summary>A fully decoded .a2cap file: frames on a timeline, markers and connection events.</summary>
public sealed class CaptureData
{
    public required string Path { get; init; }
    public required long StartTicks { get; init; }
    public required long EndTicks { get; init; }
    public required List<TimedFrame> Frames { get; init; }
    public required List<Marker> Markers { get; init; }
    public required List<ConnectionEvent> Events { get; init; }
    public required DecoderStats Stats { get; init; }
    public required BundleFormat BundleFormat { get; init; }
    public required IReadOnlyList<FlowSummary> Flows { get; init; }
    public required LengthMode Mode { get; init; }
    public required long ChunkBytes { get; init; }
    public IReadOnlyList<LengthModeScore>? DetectScores { get; init; }

    public double Duration => (EndTicks - StartTicks) / (double)TimeSpan.TicksPerSecond;
    public double Seconds(long ticks) => (ticks - StartTicks) / (double)TimeSpan.TicksPerSecond;

    public static CaptureData Load(string path, FrameDecoderOptions options, bool autoDetect)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Capture file not found: {path}");

        using var reader = A2CapReader.Open(path);
        var records = reader.ReadAll().ToList();

        IReadOnlyList<LengthModeScore>? scores = null;
        if (autoDetect)
        {
            scores = DetectMode(records, options);
            if (scores.Count > 0 && scores[0].Frames > 0)
                options = options with { LengthMode = scores[0].Mode };
        }

        var processor = new StreamProcessor(options);
        var frames = new List<TimedFrame>();
        var markers = new List<Marker>();
        var events = new List<ConnectionEvent>();
        var start = records.Count > 0 ? records.Min(r => r.Ticks) : reader.Created.Ticks;
        var end = records.Count > 0 ? records.Max(r => r.Ticks) : start;
        long bytes = 0;

        foreach (var record in records)
        {
            switch (record)
            {
                case Marker m: markers.Add(m); break;
                case ConnectionEvent e: events.Add(e); break;
                case S2CChunk c: bytes += c.Data.Length; break;
            }

            if (processor.Process(record) is { } decoded)
            {
                foreach (var frame in decoded.Frames)
                    frames.Add(new TimedFrame(frames.Count, decoded.Ticks, (decoded.Ticks - start) / (double)TimeSpan.TicksPerSecond, decoded.FlowId, frame));
            }
        }

        return new CaptureData
        {
            Path = path,
            StartTicks = start,
            EndTicks = end,
            Frames = frames,
            Markers = markers,
            Events = events,
            Stats = processor.Stats,
            BundleFormat = processor.LastBundleFormat,
            Flows = processor.Flows,
            Mode = options.LengthMode,
            ChunkBytes = bytes,
            DetectScores = scores,
        };
    }

    /// <summary>Runs the length-mode detector over the S2C bytes of the busiest flow.</summary>
    public static IReadOnlyList<LengthModeScore> DetectMode(IEnumerable<A2CapRecord> records, FrameDecoderOptions options)
    {
        var busiest = records.OfType<S2CChunk>().GroupBy(c => c.FlowId)
            .OrderByDescending(g => g.Sum(c => (long)c.Data.Length)).FirstOrDefault();
        if (busiest is null)
            return [];

        var buffer = new List<byte>();
        foreach (var chunk in busiest)
        {
            buffer.AddRange(chunk.Data);
            if (buffer.Count >= 4 * 1024 * 1024)
                break;
        }
        return LengthModeDetector.Detect(buffer.ToArray(), heartbeat: options.HeartbeatSignature, maxFrameSize: options.MaxFrameSize);
    }

    public string NearestMarker(long ticks, double withinSeconds = 10)
    {
        Marker? best = null;
        var bestDistance = long.MaxValue;
        foreach (var m in Markers)
        {
            var d = Math.Abs(m.Ticks - ticks);
            if (d < bestDistance)
            {
                best = m;
                bestDistance = d;
            }
        }
        if (best is null || bestDistance > withinSeconds * TimeSpan.TicksPerSecond)
            return "";
        return $"M{best.Number} {(ticks - best.Ticks) / (double)TimeSpan.TicksPerSecond:+0.00;-0.00}s";
    }
}
