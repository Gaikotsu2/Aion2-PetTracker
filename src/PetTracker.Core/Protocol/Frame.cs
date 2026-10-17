namespace PetTracker.Core.Protocol;

/// <summary>A decoded frame. Opcode is the two wire bytes read big-endian, so wire "33 36" is 0x3336.</summary>
public sealed record Frame(ushort Opcode, byte[] Body, bool Bundled = false)
{
    public string OpcodeHex => Opcode.ToString("x4");
}

public enum LengthKind
{
    /// <summary>Length counts opcode + body only.</summary>
    ExcludesPrefix,
    /// <summary>Length also counts the varint's own bytes.</summary>
    IncludesPrefix,
    /// <summary>Opcode + body size = length + Adjustment.</summary>
    Fixed,
}

public readonly record struct LengthMode(LengthKind Kind, int Adjustment = 0)
{
    public static LengthMode ExcludesPrefix => new(LengthKind.ExcludesPrefix);
    public static LengthMode IncludesPrefix => new(LengthKind.IncludesPrefix);
    public static LengthMode Fixed(int adjustment) => new(LengthKind.Fixed, adjustment);

    public long PayloadSize(ulong declared, int prefixLength)
    {
        var value = (long)Math.Min(declared, long.MaxValue / 2);
        return Kind switch
        {
            LengthKind.ExcludesPrefix => value,
            LengthKind.IncludesPrefix => value - prefixLength,
            _ => value + Adjustment,
        };
    }

    public static LengthMode Parse(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        switch (t)
        {
            case "excludes" or "excludes-prefix": return ExcludesPrefix;
            case "includes" or "includes-prefix": return IncludesPrefix;
        }
        if (t.StartsWith("fixed", StringComparison.Ordinal)
            && int.TryParse(t[5..].TrimStart(new[] { ':', '=' }), out var adj))
            return Fixed(adj);
        throw new FormatException($"Unknown length mode '{text}'. Use excludes-prefix, includes-prefix or fixed:N (e.g. fixed:2, fixed:-1).");
    }

    public override string ToString() => Kind switch
    {
        LengthKind.ExcludesPrefix => "excludes-prefix",
        LengthKind.IncludesPrefix => "includes-prefix",
        _ => $"fixed:{Adjustment}",
    };
}

public sealed record FrameDecoderOptions
{
    /// <summary>Confirmed on a real capture: payload (opcode + body) = varint - 4.</summary>
    public LengthMode LengthMode { get; init; } = LengthMode.Fixed(-4);
    public int MaxFrameSize { get; init; } = 4 * 1024 * 1024;
    public byte[] HeartbeatSignature { get; init; } = [0x0E, 0x00, 0x36];
}

public sealed class DecoderStats
{
    public long Frames { get; internal set; }
    public long Desyncs { get; internal set; }
    public long Heartbeats { get; internal set; }
    public long BytesDiscarded { get; internal set; }
    public long BundlesBlock { get; internal set; }
    public long BundlesFrame { get; internal set; }
    public long BundleFailures { get; internal set; }
    /// <summary>Frames (including heartbeats) that arrived inside bundles; also counted in Frames.</summary>
    public long InnerFrames { get; internal set; }
    /// <summary>Flows classified as TLS or otherwise not the game stream. Not desyncs.</summary>
    public long FlowsRejected { get; internal set; }

    public long Bundles => BundlesBlock + BundlesFrame;

    public void Add(DecoderStats other)
    {
        Frames += other.Frames;
        Desyncs += other.Desyncs;
        Heartbeats += other.Heartbeats;
        BytesDiscarded += other.BytesDiscarded;
        BundlesBlock += other.BundlesBlock;
        BundlesFrame += other.BundlesFrame;
        BundleFailures += other.BundleFailures;
        InnerFrames += other.InnerFrames;
        FlowsRejected += other.FlowsRejected;
    }
}
