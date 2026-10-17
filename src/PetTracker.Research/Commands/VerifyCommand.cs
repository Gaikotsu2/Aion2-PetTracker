using System.CommandLine;
using PetTracker.Core.Domain;
using PetTracker.Core.Verification;
using PetTracker.Research.Support;

namespace PetTracker.Research.Commands;

internal static class VerifyCommand
{
    public static Command Build(CommonOptions common)
    {
        var files = new Argument<string[]>("captures")
        {
            Description = "One or more .a2cap files, replayed in order into one collection (for example r2.a2cap r3.a2cap r4.a2cap).",
            Arity = ArgumentArity.OneOrMore,
        };
        var expect = new Option<string?>("--expect") { Description = "Expectation JSON (see docs/expect.example.json). Exit code 1 on any mismatch." };
        var record = new Option<string?>("--write-expect")
        {
            Description = "Write an expectation file from what the replay produces now (every pet, plus the game count after each capture). Review it against the game before trusting it.",
        };
        var command = new Command("verify", "Replay captures and compare the final state (pet levels/progress, game-style counts) with an expectation file. Regression check for patch days.")
        {
            files, expect, record,
        };

        command.SetAction(result => CommandSupport.Run(() =>
        {
            var registry = common.Registry(result);
            // Base catalog only: the user's own pet-names.json must not change what counts as Special.
            var catalog = PetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "pets.json"), null);
            var paths = result.GetValue(files)!;
            var captures = paths.Select(p => ReplayVerifier.LoadFrames(p, common.DecoderOptions(result))).ToList();
            var expectPath = result.GetValue(expect);
            var recordPath = result.GetValue(record);
            if (expectPath is null && recordPath is null)
                throw new InvalidOperationException("Give --expect <file> to verify, or --write-expect <file> to record one.");

            var exit = 0;
            if (expectPath is not null)
            {
                var expectation = ReplayExpectation.Load(expectPath);
                var problems = ReplayVerifier.Verify(expectation, captures, registry, catalog);
                var checks = (expectation.Checkpoints?.Count ?? 0) + (expectation.Final is null ? 0 : 1);
                var final = ReplayVerifier.Replay(captures, registry, catalog);
                Console.WriteLine($"Replayed {paths.Length} capture(s): {CollectionSummary.From(final.Pets)}");
                Console.WriteLine($"Game-style count: {final.GameCount}");
                if (problems.Count == 0)
                {
                    ConsoleOut.Line($"PASS: {checks} expectation set(s) match.", ConsoleColor.Green);
                }
                else
                {
                    foreach (var p in problems)
                        ConsoleOut.Line("MISMATCH " + p, ConsoleColor.Red);
                    ConsoleOut.Line($"FAIL: {problems.Count} mismatch(es).", ConsoleColor.Red);
                    exit = 1;
                }
            }

            if (recordPath is not null)
            {
                var recorded = ReplayVerifier.Record(paths.Select(Path.GetFileName).OfType<string>().ToList(), captures, registry, catalog);
                var full = Path.GetFullPath(recordPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, recorded.ToJson());
                Console.WriteLine($"Wrote expectation ({recorded.Final?.Pets?.Count ?? 0} pets, {recorded.Checkpoints?.Count ?? 0} checkpoint(s)) to {full}");
            }
            return exit;
        }));
        return command;
    }
}
