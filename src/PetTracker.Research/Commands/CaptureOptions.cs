using System.CommandLine;
using PetTracker.Core;

namespace PetTracker.Research.Commands;

/// <summary>Options for commands that capture live traffic (record, live).</summary>
internal sealed class CaptureOptions
{
    public Option<string?> Adapter { get; } = new("--adapter")
    {
        Description = "Force a network adapter (name, description text or IP). Use this with a VPN. See 'adapters'.",
    };

    public Option<string> Process { get; } = new("--process")
    {
        Description = "Game process name without .exe.",
        DefaultValueFactory = _ => "AION2",
    };

    public Option<bool> NoFallback { get; } = new("--no-fallback")
    {
        Description = "Do not fall back to heartbeat-only detection when the TCP table finds no game connection.",
    };

    public Option<int[]> ExcludePort { get; } = new("--exclude-port")
    {
        Description = "Also leave this TCP port out of the capture (repeatable). 443 and 80 are always excluded.",
        AllowMultipleArgumentsPerToken = true,
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Adapter);
        command.Options.Add(Process);
        command.Options.Add(NoFallback);
        command.Options.Add(ExcludePort);
    }

    public PipelineOptions Build(ParseResult result, CommonOptions common) => new()
    {
        ProcessName = result.GetValue(Process)!,
        AdapterOverride = result.GetValue(Adapter),
        HeartbeatFallback = !result.GetValue(NoFallback),
        ExcludedPorts = result.GetValue(ExcludePort) ?? [],
        Decoder = common.DecoderOptions(result),
    };
}
