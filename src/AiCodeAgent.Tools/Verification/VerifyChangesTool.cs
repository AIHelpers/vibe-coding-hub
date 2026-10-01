using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Verification;

/// <summary>
/// One-call "is my work correct?" check: detects the project's build/test/lint commands, runs them in order, stops
/// at the first failure and returns a condensed error report. The model is expected to fix and call it again; after
/// <see cref="MaxConsecutiveFailures"/> failed attempts in a row the tool tells it to stop and report instead.
/// </summary>
public class VerifyChangesTool : BaseTool
{
    public const int MaxConsecutiveFailures = 5;
    private const int StageTimeoutSeconds = 600;

    private static readonly ConcurrentDictionary<string, int> Failures = new();

    private readonly ICommandSandbox? _sandbox;

    public VerifyChangesTool(ILogger<VerifyChangesTool> logger, ICommandSandbox? sandbox = null) : base(logger)
        => _sandbox = sandbox;

    public override string Name => "verify_changes";
    public override string Description =>
        "Run the project's build, tests and lint (auto-detected, or from .aiagent/verify.json or AGENTS.md 'Build/Test/Lint command:' lines) " +
        "and report pass/fail with condensed errors. Call it after finishing a set of edits, before telling the user you are done. " +
        "If it fails, fix the cause and call it again; stop after repeated failures and explain what is still broken.";
    public override RiskLevel Risk => RiskLevel.Execute;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["stages"] = new()
                {
                    Type = "array",
                    Description = "Optional subset of stages to run, e.g. [\"build\"] or [\"test\"]. Default: all detected.",
                    Items = new PropertySchema { Type = "string", Description = "Stage name (build, test, lint, ...)" }
                },
                ["path"] = new() { Type = "string", Description = "Project directory (default: working directory)" }
            },
            Required = new()
        }
    };

    public static void ResetAttempts(string key) => Failures.TryRemove(key, out _);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path", ".");
        var wanted = GetArg<string[]>(call, "stages", Array.Empty<string>()) ?? Array.Empty<string>();

        string dir;
        try { dir = ResolvePath(path, context); }
        catch (UnauthorizedAccessException ex) { return Error(ex.Message); }
        if (!Directory.Exists(dir)) return Error($"Directory not found: {path}");

        var stages = VerifyCommandDetector.Detect(dir);
        if (wanted.Length > 0)
            stages = stages.Where(s => wanted.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (stages.Count == 0)
            return Error("No build/test/lint commands found. Add .aiagent/verify.json " +
                         "({\"commands\":[{\"name\":\"test\",\"command\":\"...\"}]}) or 'Test command:' lines to AGENTS.md.");

        var key = $"{context.SessionId}::{context.AgentId}::{Path.GetFullPath(dir)}";
        var report = new StringBuilder();
        var sw = Stopwatch.StartNew();

        foreach (var stage in stages)
        {
            var (exit, output, timedOut) = await RunStageAsync(stage.Command, dir, context);
            if (exit == 0 && !timedOut)
            {
                report.AppendLine($"PASS  {stage.Name}: {stage.Command}");
                continue;
            }

            var attempts = Failures.AddOrUpdate(key, 1, (_, n) => n + 1);
            report.AppendLine($"FAIL  {stage.Name}: {stage.Command} ({(timedOut ? $"timed out after {StageTimeoutSeconds}s" : $"exit {exit}")})");
            report.AppendLine();
            report.AppendLine(FailureSummarizer.Summarize(output));
            report.AppendLine();
            report.AppendLine(attempts >= MaxConsecutiveFailures
                ? $"Attempt {attempts}/{MaxConsecutiveFailures}: limit reached. Do NOT keep retrying. Stop, and tell the user what is still failing and what you tried."
                : $"Attempt {attempts}/{MaxConsecutiveFailures}. Fix the cause above, then call verify_changes again.");
            return new ToolResult { ToolName = Name, Content = report.ToString().TrimEnd(), IsError = true };
        }

        ResetAttempts(key);
        report.AppendLine($"All {stages.Count} stage(s) passed in {sw.Elapsed.TotalSeconds:0.#}s.");
        return Success(report.ToString().TrimEnd());
    }

    private async Task<(int Exit, string Output, bool TimedOut)> RunStageAsync(string command, string dir, AgentExecutionContext context)
    {
        string exe;
        IReadOnlyList<string> args;
        if (_sandbox is { Enabled: true })
        {
            var unavailable = await _sandbox.CheckAvailabilityAsync(context.CancellationToken);
            if (unavailable != null)
                return (-1, $"Sandbox is enabled ({_sandbox.Description}) but unavailable: {unavailable} Nothing was run.", false);
            var launch = _sandbox.Wrap(command, string.IsNullOrEmpty(context.WorkingDirectory) ? dir : context.WorkingDirectory, dir);
            exe = launch.Executable;
            args = launch.Arguments;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { exe = "cmd.exe"; args = ["/c", command]; }
        else { exe = "/bin/bash"; args = ["-c", command]; }

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in context.Environment) psi.Environment[k] = v;

        var output = new StringBuilder();
        var gate = new object();
        void Append(string? line)
        {
            if (line == null) return;
            lock (gate) { if (output.Length < 400_000) output.AppendLine(line); }
        }

        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(StageTimeoutSeconds));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, output.ToString(), !context.CancellationToken.IsCancellationRequested);
            }
            // Parameterless WaitForExit flushes the async output handlers.
            p.WaitForExit();
            return (p.ExitCode, output.ToString(), false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "verify stage failed to start: {Command}", command);
            return (-1, $"Could not run '{command}': {ex.Message}", false);
        }
    }
}
