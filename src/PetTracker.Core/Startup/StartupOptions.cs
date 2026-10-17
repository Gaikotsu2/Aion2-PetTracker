namespace PetTracker.Core.Startup;

/// <summary>The app's command line: --data &lt;folder&gt;, --exit-after &lt;seconds&gt;, --minimized, --quit.</summary>
public sealed record StartupOptions(string? DataFolder, int ExitAfterSeconds, bool Minimized, bool Quit)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        string? data = null;
        var exitAfter = 0;
        bool minimized = false, quit = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--data" when i + 1 < args.Count:
                    data = args[++i];
                    break;
                case "--exit-after" when i + 1 < args.Count:
                    if (int.TryParse(args[++i], out var seconds) && seconds > 0)
                        exitAfter = seconds;
                    break;
                case AutoStart.MinimizedArgument:
                    minimized = true;
                    break;
                case "--quit":
                    quit = true;
                    break;
            }
        }
        return new StartupOptions(data, exitAfter, minimized, quit);
    }
}
