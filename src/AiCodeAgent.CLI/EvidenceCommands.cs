using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Evidence;

namespace AiCodeAgent.CLI;

/// <summary>
/// <c>aiagent evidence &lt;run&gt;</c> (E10-S1): the evidence pack of a finished task or flow, as Markdown for a pull request body or as JSON.
/// Exit codes: 0 ok, 1 the audit trail failed verification (the pack is still printed, with a warning in it), 2 run not found, 3 bad arguments.
/// </summary>
public static class EvidenceCommands
{
    public static void AddCommands(RootCommand root) => root.AddCommand(Build());

    private static Command Build()
    {
        var command = new Command("evidence", "Show the evidence pack of a run: tests before and after, build and lint, changed files by risk, commands, cost");
        var run = new Argument<string>("run", "Run id, part of a run's file name, a path to audit.jsonl, or 'latest'");
        var format = new Option<string>(new[] { "--format", "-f" }, () => "md", "md (for a pull request body) or json");
        var output = new Option<string?>(new[] { "--output", "-o" }, "Write to this file instead of the console");
        command.AddArgument(run);
        command.AddOption(format);
        command.AddOption(output);
        command.SetHandler((InvocationContext ctx) =>
        {
            var p = ctx.ParseResult;
            var fmt = (p.GetValueForOption(format) ?? "md").Trim().ToLowerInvariant();
            if (fmt is not ("md" or "markdown" or "json"))
            {
                Console.Error.WriteLine("Unknown --format. Use md or json.");
                ctx.ExitCode = 3;
                return;
            }
            if (!TryResolve(p.GetValueForArgument(run), out var path)) { ctx.ExitCode = 2; return; }

            var pack = EvidencePackStore.Build(path);
            // Keep the stored copy in step with the trail (it is derived, so this is always safe).
            EvidencePackStore.TryWrite(path);
            var text = fmt == "json" ? EvidencePackStore.ToJson(pack) : EvidencePackRenderer.ToMarkdown(pack);
            if (p.GetValueForOption(output) is { Length: > 0 } target)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(target, text, new UTF8Encoding(false));
                Console.WriteLine($"Wrote {Path.GetFullPath(target)}");
            }
            else
            {
                Console.Out.Write(text);
            }
            if (pack.AuditIntact == false) ctx.ExitCode = 1;
        });
        return command;
    }

    private static bool TryResolve(string run, out string path)
    {
        var locator = new AuditLocator();
        if (string.Equals(run.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            path = locator.List().FirstOrDefault() ?? string.Empty;
            if (path.Length > 0) return true;
            Console.Error.WriteLine("No audit trails yet. They are written for every task and flow run.");
            return false;
        }
        var (found, candidates) = locator.Resolve(run);
        path = found ?? string.Empty;
        if (found != null) return true;
        if (candidates.Count == 0) Console.Error.WriteLine($"No run found for '{run}'. Try: aiagent audit list");
        else
        {
            Console.Error.WriteLine($"'{run}' matches {candidates.Count} runs; use more of the name:");
            foreach (var c in candidates.Take(10)) Console.Error.WriteLine($"  {AuditLocator.Id(c)}");
        }
        return false;
    }
}
