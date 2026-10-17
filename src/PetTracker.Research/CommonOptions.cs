using System.CommandLine;
using PetTracker.Core.Protocol;

namespace PetTracker.Research;

/// <summary>Options shared by every command: framing hypothesis, auto-detection and the opcode map.</summary>
public sealed class CommonOptions
{
    public Option<string?> LengthMode { get; } = new("--length-mode")
    {
        Description = "How the frame length varint is interpreted: fixed:-4 (default, confirmed: varint - 4 = opcode+body size), excludes-prefix, includes-prefix, or fixed:N (varint + N = opcode+body size).",
        Recursive = true,
    };

    public Option<bool> AutoDetect { get; } = new("--auto-detect")
    {
        Description = "Diagnostic only: try every length mode on the captured stream, report which chains the most frames, and use the best one. Not needed with the default fixed:-4.",
        Recursive = true,
    };

    public Option<string?> Opcodes { get; } = new("--opcodes")
    {
        Description = "Path to an opcode map JSON (default: data/opcodes.default.json next to the tool).",
        Recursive = true,
    };

    public void AddTo(RootCommand root)
    {
        root.Options.Add(LengthMode);
        root.Options.Add(AutoDetect);
        root.Options.Add(Opcodes);
    }

    public FrameDecoderOptions DecoderOptions(ParseResult result)
    {
        var text = result.GetValue(LengthMode);
        return new FrameDecoderOptions
        {
            LengthMode = text is null ? new FrameDecoderOptions().LengthMode : Core.Protocol.LengthMode.Parse(text),
        };
    }

    public bool WantsAutoDetect(ParseResult result) => result.GetValue(AutoDetect);

    public OpcodeRegistry Registry(ParseResult result) =>
        result.GetValue(Opcodes) is { } path ? OpcodeRegistry.Load(path) : OpcodeRegistry.LoadDefault();
}
