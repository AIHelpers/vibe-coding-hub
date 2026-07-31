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

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var operation = GetArg<string>(call, "operation");
        var args = GetArg<string>(call, "args", "");

        var allowedReadOps = new HashSet<string> { "status", "diff", "log", "branch", "show", "blame" };
        if (context.IsReadOnly && !allowedReadOps.Contains(operation))
            return Error($"Git operation '{operation}' is not allowed in read-only mode.");

        var gitCommand = BuildGitCommand(operation, args);

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = gitCommand,
                    WorkingDirectory = context.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                return Error($"Git error: {stderr}");

            return Success(string.IsNullOrEmpty(stdout) ? "Done" : stdout);
        }
        catch (Exception ex)
        {
            return Error($"Failed to execute git command: {ex.Message}");
        }
    }

    private static string BuildGitCommand(string operation, string args) => operation switch
    {
        "status" => $"status {args}",
        "diff" => $"diff {args}",
        "log" => $"log --oneline --graph {args}",
        "add" => $"add {(string.IsNullOrEmpty(args) ? "." : args)}",
        "commit" => $"commit -m \"{args}\"",
        "branch" => $"branch {args}",
        "checkout" => $"checkout {args}",
        "pull" => $"pull {args}",
        "push" => $"push {args}",
        "stash" => $"stash {args}",
        "show" => $"show {args}",
        "blame" => $"blame {args}",
        "init" => "init",
        _ => throw new ArgumentException($"Unknown git operation: {operation}")
    };
}