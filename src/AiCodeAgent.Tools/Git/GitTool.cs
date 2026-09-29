using System.Diagnostics;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Git;

public class GitTool : BaseTool
{
    public GitTool(ILogger<GitTool> logger) : base(logger) { }

    public override string Name => "git";
    public override string Description =>
        "Execute git operations: status, diff, log, add, commit, branch, etc.";
    public override RiskLevel Risk => RiskLevel.Execute;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["operation"] = new()
                {
                    Type = "string",
                    Description = "Git operation",
                    Enum = ["status", "diff", "log", "add", "commit", "branch", "checkout", "pull", "push", "stash", "show", "blame", "init"]
                },
                ["args"] = new() { Type = "string", Description = "Additional arguments for the operation" }
            },
            Required = ["operation"]
        }
    };

    /// <summary>Wall-clock limit for one git invocation (a hung credential prompt / pager must not stall the agent forever).</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Option names that make git run arbitrary programs or redirect its configuration.</summary>
    private static readonly string[] ForbiddenOptionPrefixes =
    {
        "-c", "--config", "--upload-pack", "--receive-pack", "--exec", "--output", "--git-dir", "--work-tree",
        "--ext-diff", "--textconv", "--no-index", "--open-files-in-pager"
    };

    private static readonly string[] ReadOnlyBranchFlags = { "-a", "-r", "-v", "-vv", "--list", "-l", "--all", "--remotes", "--show-current" };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var operation = GetArg<string>(call, "operation");
        var args = GetArg<string>(call, "args", "");

        var allowedReadOps = new HashSet<string> { "status", "diff", "log", "branch", "show", "blame" };
        if (context.IsReadOnly && !allowedReadOps.Contains(operation))
            return Error($"Git operation '{operation}' is not allowed in read-only mode.");

        // Throws ArgumentException for an unknown operation.
        var gitArgs = BuildGitCommand(operation, args);

        // Anything after the operation that could execute programs or rewrite config is refused;
        // for "commit" the whole args string is one message argument, so it is inert.
        if (operation != "commit")
        {
            foreach (var token in gitArgs.Skip(1))
            {
                if (IsForbiddenOption(token))
                    return Error($"Git option '{token}' is not allowed.");
            }
        }

        if (context.IsReadOnly && operation == "branch" &&
            gitArgs.Skip(1).Any(t => !ReadOnlyBranchFlags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            return Error("Only listing branches is allowed in read-only mode.");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = context.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            // ArgumentList (no shell, no manual quoting) — user text can't break out of its argument.
            foreach (var a in gitArgs)
                process.StartInfo.ArgumentList.Add(a);
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GIT_PAGER"] = "cat";
            process.StartInfo.Environment["GIT_EDITOR"] = "true";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(GitTimeout);

            process.Start();
            process.StandardInput.Close();

            // Read both streams concurrently: sequential ReadToEnd can deadlock when stderr fills its pipe.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Error(context.CancellationToken.IsCancellationRequested
                    ? "Git command cancelled."
                    : $"Git command timed out after {(int)GitTimeout.TotalSeconds}s.");
            }

            const int MaxGitOutputChars = 200_000;
            var stdout = stdoutTask.Result;
            if (stdout.Length > MaxGitOutputChars)
                stdout = stdout[..MaxGitOutputChars] + "\n...[output truncated]";
            var stderr = stderrTask.Result;

            if (process.ExitCode != 0)
                return Error($"Git error (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");

            return Success(string.IsNullOrEmpty(stdout) ? "Done" : stdout);
        }
        catch (Exception ex)
        {
            return Error($"Failed to execute git command: {ex.Message}");
        }
    }

    private static bool IsForbiddenOption(string token)
    {
        if (!token.StartsWith('-')) return false;
        foreach (var prefix in ForbiddenOptionPrefixes)
        {
            if (token.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                (prefix.StartsWith("--") && token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        // "-cfoo=bar" glued form of -c
        return token.Length > 2 && token[0] == '-' && token[1] == 'c' && token[2] != '-' && token.Contains('=');
    }

    /// <summary>Builds the argument vector for <c>git</c> (no shell involved).</summary>
    private static string[] BuildGitCommand(string operation, string args)
    {
        var extra = SplitArgs(args);
        return operation switch
        {
            "status" or "diff" or "branch" or "checkout" or "pull" or "push" or "stash" or "show" or "blame"
                => new[] { operation }.Concat(extra).ToArray(),
            "log" => new[] { "log", "--oneline", "--graph" }.Concat(extra).ToArray(),
            "add" => new[] { "add" }.Concat(extra.Length == 0 ? new[] { "." } : extra).ToArray(),
            // The whole args string is ONE message argument — quotes/newlines in it can't add options.
            "commit" => new[] { "commit", "-m", args },
            "init" => new[] { "init" },
            _ => throw new ArgumentException($"Unknown git operation: {operation}")
        };
    }

    /// <summary>Whitespace split that keeps single/double-quoted segments together.</summary>
    private static string[] SplitArgs(string? args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return result.ToArray();
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in args)
        {
            if (quote != null)
            {
                if (c == quote) quote = null; else sb.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result.ToArray();
    }
}