using System.CommandLine;
using System.CommandLine.Invocation;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Import;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.CLI;

/// <summary>
/// <c>import [source]</c> (E6-S2): brings skills and rules from Claude Code, Cursor, Kiro and Copilot into this project.
/// Sources: all (default), claude, claude-md, cursor, kiro, copilot. Exit codes: 0 done or nothing to do, 1 failed or doctor errors, 3 not confirmed.
/// </summary>
public static class ImportCommands
{
    public static void AddCommands(RootCommand root) => root.AddCommand(Build());

    private static Command Build()
    {
        var cmd = new Command("import", "Import skills and rules from other tools: aiagent import [all|claude|claude-md|cursor|kiro|copilot]");
        var sourceArg = new Argument<string?>("source", () => "all", "What to import: all, claude (skills), claude-md, cursor, kiro or copilot");
        var dirOption = new Option<string?>("--dir", "Project directory (default: current directory)");
        var yesOption = new Option<bool>(new[] { "--yes", "-y" }, "Import without asking (the preview is still printed)");
        var dryRun = new Option<bool>("--dry-run", "Only show what would be imported");
        var noUser = new Option<bool>("--no-user", "Do not look at ~/.claude/skills");
        cmd.AddArgument(sourceArg);
        cmd.AddOption(dirOption);
        cmd.AddOption(yesOption);
        cmd.AddOption(dryRun);
        cmd.AddOption(noUser);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var p = ctx.ParseResult;
            var text = p.GetValueForArgument(sourceArg);
            if (!ProjectImporter.TryParseSources(text, out var sources))
            {
                Console.Error.WriteLine($"Unknown source '{text}'. Use: all, claude, claude-md, cursor, kiro, copilot.");
                ctx.ExitCode = 3;
                return;
            }
            var workdir = Path.GetFullPath(p.GetValueForOption(dirOption) ?? Directory.GetCurrentDirectory());
            var r = await SkillCharacterCommands.OpenAsync(workdir);
            var importer = new ProjectImporter(workdir, SkillRegistry.GetDefaultProjectSkillsDir(workdir), r.Skills.GlobalSkillsDirectory,
                p.GetValueForOption(noUser) ? null : ProjectImporter.DefaultUserClaudeSkills());
            var plan = importer.Plan(sources);
            Console.WriteLine(plan.Render());
            var pending = plan.Pending.ToList();
            if (plan.IsEmpty) return;
            if (pending.Count == 0)
            {
                Console.WriteLine("\nEverything found is already imported; nothing to do.");
                return;
            }
            if (p.GetValueForOption(dryRun)) return;
            if (!p.GetValueForOption(yesOption) && !Confirm($"\nImport {pending.Count} item(s)? The source files stay as they are."))
            {
                Console.Error.WriteLine("Nothing imported.");
                ctx.ExitCode = 3;
                return;
            }

            var outcomes = await importer.ApplyAsync(plan, ctx.GetCancellationToken());
            foreach (var o in outcomes.Where(o => o.Kind != ImportResultKind.AlreadyImported))
                Console.WriteLine($"{(o.Kind == ImportResultKind.Imported ? "✓" : o.Kind == ImportResultKind.Failed ? "✕" : "·")} {o.Item.Label} → {o.Message}");
            var failed = outcomes.Any(o => o.Kind == ImportResultKind.Failed);

            if (outcomes.Any(o => o.Kind == ImportResultKind.Imported && o.Item.Target == ImportTarget.TeamConventionsSection))
                Console.WriteLine("\nTip: pin the always-on rules on a character: aiagent characters add-skill <id> team-conventions --pinned");

            Console.WriteLine("\nChecking the result (skills doctor):");
            var reopened = await SkillCharacterCommands.OpenAsync(workdir);
            var checks = await SkillDoctor.DiagnoseAsync(reopened.Skills, reopened.Characters, reopened.Presets);
            foreach (var c in checks)
            {
                var tag = c.Status switch { DoctorStatus.Ok => "OK  ", DoctorStatus.Warning => "WARN", _ => "FAIL" };
                Console.WriteLine($"  {tag}  {c.Name,-28} {c.Detail}");
            }
            if (failed || checks.Any(c => c.Status == DoctorStatus.Error)) ctx.ExitCode = 1;
        });
        return cmd;
    }

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("No terminal to ask on; run again with --yes after reading the preview.");
            return false;
        }
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine();
        return answer != null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }
}
