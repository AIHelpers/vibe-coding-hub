using System.CommandLine;
using System.CommandLine.Invocation;
using AiCodeAgent.Core.Audit;

namespace AiCodeAgent.CLI;

/// <summary>
/// <c>aiagent audit list|verify|export</c> (E7-S2): check and export the append-only, hash-chained audit trail of chat tasks and flow runs.
/// Exit codes: 0 ok, 1 the chain is broken, 2 the run was not found (or the name is ambiguous), 3 bad arguments.
/// </summary>
public static class AuditCommands
{
    public static void AddCommands(RootCommand root) => root.AddCommand(Build());

    private static Command Build()
    {
        var audit = new Command("audit", "Verify and export the audit trail of chat tasks and flow runs");

        // audit list
        var list = new Command("list", "List recorded runs (newest first)");
        var limit = new Option<int>("--limit", () => 20, "Maximum number of runs to show");
        list.AddOption(limit);
        list.SetHandler((int n) =>
        {
            var files = new AuditLocator().List();
            if (files.Count == 0)
            {
                Console.WriteLine("No audit trails yet. They are written for every chat task and flow run.");
                return;
            }
            Console.WriteLine($"{"Run",-52} {"Kind",-5} {"Entries",8}  Updated (UTC)");
            foreach (var f in files.Take(n))
            {
                var isFlow = string.Equals(Path.GetFileName(f), AuditChain.FileName, StringComparison.OrdinalIgnoreCase);
                var entries = 0;
                try { entries = AuditFiles.ReadLines(f).Count(l => l.Length > 0); } catch (IOException) { }
                Console.WriteLine($"{AuditLocator.Id(f),-52} {(isFlow ? "flow" : "chat"),-5} {entries,8}  {File.GetLastWriteTimeUtc(f):yyyy-MM-dd HH:mm}");
            }
        }, limit);
        audit.AddCommand(list);

        // audit verify <run>
        var verify = new Command("verify", "Check the hash chain of a run's audit.jsonl; names the first line that was changed");
        var runArg = new Argument<string>("run", "Run id, part of a chat run's file name, or a path to audit.jsonl / an exported .zip");
        var headOption = new Option<string?>("--head", "Expected head hash (from the run record or an earlier verify); also catches a cut-off or rewritten tail");
        verify.AddArgument(runArg);
        verify.AddOption(headOption);
        verify.SetHandler((InvocationContext ctx) =>
        {
            var run = ctx.ParseResult.GetValueForArgument(runArg);
            var head = ctx.ParseResult.GetValueForOption(headOption);
            if (!TryResolve(run, out var path)) { ctx.ExitCode = 2; return; }
            var result = AuditChainVerifier.Verify(path);
            if (!result.Ok)
            {
                Console.Error.WriteLine($"✗ {path}");
                Console.Error.WriteLine($"  Verification FAILED at line {result.Failure!.Line}: {result.Failure.Problem}");
                ctx.ExitCode = 1;
                return;
            }
            if (!string.IsNullOrWhiteSpace(head) && !string.Equals(head.Trim(), result.HeadHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"✗ {path}");
                Console.Error.WriteLine($"  Head hash {result.HeadHash} does not match the expected {head.Trim()}: lines were cut off the end or the tail was rewritten.");
                ctx.ExitCode = 1;
                return;
            }
            Console.WriteLine($"✓ {path}");
            Console.WriteLine($"  {result.Lines} entries, hash chain intact.");
            Console.WriteLine($"  Head hash: {result.HeadHash}");
            foreach (var w in result.Warnings) Console.WriteLine($"  Note: {w}");
        });
        audit.AddCommand(verify);

        // audit export <run>
        var export = new Command("export", "Export a run's audit trail as Markdown, JSON or a zip (audit.jsonl + report + one .diff per file change)");
        var exportRun = new Argument<string>("run", "Run id, part of a chat run's file name, or a path to audit.jsonl");
        var format = new Option<string>(new[] { "--format", "-f" }, () => "md", "md, json or zip");
        var output = new Option<string?>(new[] { "--output", "-o" }, "Where to write the export (default: next to the audit file)");
        export.AddArgument(exportRun);
        export.AddOption(format);
        export.AddOption(output);
        export.SetHandler((InvocationContext ctx) =>
        {
            var p = ctx.ParseResult;
            if (!AuditExporter.TryParseFormat(p.GetValueForOption(format), out var fmt))
            {
                Console.Error.WriteLine("Unknown --format. Use md, json or zip.");
                ctx.ExitCode = 3;
                return;
            }
            if (!TryResolve(p.GetValueForArgument(exportRun), out var path)) { ctx.ExitCode = 2; return; }
            var target = p.GetValueForOption(output)
                ?? Path.Combine(Path.GetDirectoryName(path)!, AuditLocator.Id(path) + ".audit-export" + AuditExporter.DefaultExtension(fmt));
            var result = AuditExporter.Export(path, target, fmt);
            Console.WriteLine($"Exported to {Path.GetFullPath(target)}");
            if (!result.Ok)
            {
                Console.Error.WriteLine($"Warning: the hash chain is broken at line {result.Failure!.Line}: {result.Failure.Problem}");
                ctx.ExitCode = 1;
            }
        });
        audit.AddCommand(export);
        return audit;
    }

    private static bool TryResolve(string run, out string path)
    {
        var (found, candidates) = new AuditLocator().Resolve(run);
        path = found ?? string.Empty;
        if (found != null) return true;
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine($"No audit trail found for '{run}'. Try: aiagent audit list");
        }
        else
        {
            Console.Error.WriteLine($"'{run}' matches {candidates.Count} runs; use more of the name:");
            foreach (var c in candidates.Take(10)) Console.Error.WriteLine($"  {AuditLocator.Id(c)}");
        }
        return false;
    }
}
