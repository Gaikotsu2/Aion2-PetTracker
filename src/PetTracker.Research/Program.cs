using System.CommandLine;
using PetTracker.Research;
using PetTracker.Research.Commands;

var common = new CommonOptions();
var root = new RootCommand("pt-research: passive network research tool for the Aion 2 Pet Tracker. It only listens to traffic; it never touches the game process.");
common.AddTo(root);

root.Subcommands.Add(RecordCommand.Build(common));
root.Subcommands.Add(ImportCommand.Build(common));
root.Subcommands.Add(ImportNamesCommand.Build(common));
root.Subcommands.Add(FramesCommand.Build(common));
root.Subcommands.Add(FlowsCommand.Build(common));
root.Subcommands.Add(HistCommand.Build(common));
root.Subcommands.Add(HexCommand.Build(common));
root.Subcommands.Add(SearchCommand.Build(common));
root.Subcommands.Add(DiffCommand.Build(common));
root.Subcommands.Add(LiveCommand.Build(common));
root.Subcommands.Add(ReplayCommand.Build(common));
root.Subcommands.Add(AdaptersCommand.BuildAdapters());
root.Subcommands.Add(AdaptersCommand.BuildConnections());

return root.Parse(args).Invoke();
