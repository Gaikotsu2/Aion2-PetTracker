namespace PetTracker.Core.Protocol;

public readonly record struct LengthModeScore(LengthMode Mode, int Frames, int StartOffset);

public static class LengthModeDetector
{
    /// <summary>fixed:-4 first (the confirmed mode, so it wins ties), then every fixed:-8..+8 (0 is excludes-prefix), then excludes/includes.</summary>
    public static IReadOnlyList<LengthMode> DefaultCandidates { get; } =
    [
        LengthMode.Fixed(-4),
        .. Enumerable.Range(-8, 17).Where(a => a is not (-4 or 0)).Select(LengthMode.Fixed),
        LengthMode.ExcludesPrefix,
        LengthMode.IncludesPrefix,
    ];

    /// <summary>
    /// Scores each mode by the longest run of consecutive frames chaining cleanly, starting either at offset 0
    /// or at an occurrence of the heartbeat signature. Results are best first; ties keep candidate order.
    /// </summary>
    public static IReadOnlyList<LengthModeScore> Detect(
        ReadOnlySpan<byte> buffer,
        IEnumerable<LengthMode>? candidates = null,
        byte[]? heartbeat = null,
        int maxFrameSize = 4 * 1024 * 1024)
    {
        heartbeat ??= new FrameDecoderOptions().HeartbeatSignature;
        var starts = new List<int> { 0 };
        var from = 0;
        while (starts.Count < 16)
        {
            var i = buffer[from..].IndexOf(heartbeat);
            if (i < 0) break;
            starts.Add(from + i);
            from += i + 1;
        }

        var scores = new List<LengthModeScore>();
        foreach (var mode in candidates ?? DefaultCandidates)
        {
            var best = new LengthModeScore(mode, 0, 0);
            foreach (var start in starts)
            {
                var n = FrameParser.CountChain(buffer[start..], mode, maxFrameSize);
                if (n > best.Frames)
                    best = new LengthModeScore(mode, n, start);
            }
            scores.Add(best);
        }
        return scores.OrderByDescending(s => s.Frames).ToList();
    }
}
