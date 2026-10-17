using System.CommandLine;
using PetTracker.Core.Capture;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class AdaptersCommand
{
    public static Command BuildAdapters()
    {
        var command = new Command("adapters", "List capture adapters (needs Npcap). Use a name or IP with --adapter to override the automatic choice.");
        command.SetAction(_ => CommandSupport.Run(() =>
        {
            foreach (var adapter in NpcapSource.ListAdapters())
                Console.WriteLine(adapter);
            return 0;
        }));
        return command;
    }

    public static Command BuildConnections()
    {
        var process = new Option<string>("--process") { Description = "Game process name without .exe.", DefaultValueFactory = _ => "AION2" };
        var command = new Command("connections", "Show the game's established TCP connections (needs no Npcap). Handy to check the tool can see the game.") { process };
        command.SetAction(result => CommandSupport.Run(() =>
        {
            var locator = new ConnectionLocator(result.GetValue(process)!);
            var pids = locator.FindPids();
            if (pids.Count == 0)
            {
                ConsoleOut.Warn($"No process named '{locator.ProcessName}' is running.");
                return 0;
            }
            Console.WriteLine($"Process ids: {string.Join(", ", pids)}");
            var connections = locator.Poll();
            if (connections.Count == 0)
                ConsoleOut.Warn("No established TCP connections yet.");
            foreach (var c in connections)
                Console.WriteLine(c);
            return 0;
        }));
        return command;
    }
}
