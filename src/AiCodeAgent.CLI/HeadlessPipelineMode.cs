using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeAgent.CLI;

/// <summary>What <c>aiagent pipeline run --non-interactive</c> was asked to do.</summary>
internal sealed record HeadlessArgs(
    string Pipeline,
    string? Task,
    string? TaskFile,
    string? Cast,
    int? MaxParallel,
    decimal? MaxBudgetUsd,
    string? Permission,
    string? Output,
    string? OutputFile,
    int? TimeoutSeconds,
    string? Model,
    string? WorkingDirectory);

/// <summary>
/// A flow run for CI: nothing is ever asked, the report goes to a file or stdout, progress goes to stderr, and the
/// process exit code says how it went (see <see cref="FlowExitCodes"/>).
/// </summary>
internal static class HeadlessPipelineMode
{
    public static async Task<int> RunAsync(
        HeadlessArgs args, Func<Task<IServiceProvider>> buildServices, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        var definition = new SdlcPipelineDefinition { Name = args.Pipeline };
        string task = args.Task ?? string.Empty;

        async Task<int> Finish(FlowHeadlessOutcome outcome)
        {
            var format = ParseFormat(args.Output, args.OutputFile);
            if (format != null)
            {
                var text = format == "json" ? FlowReport.Json(definition, outcome) : FlowReport.Markdown(definition, task, outcome);
                if (!string.IsNullOrWhiteSpace(args.OutputFile))
                {
                    var path = Path.GetFullPath(args.OutputFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, text + Environment.NewLine);
                    stderr.WriteLine($"Report written to {path}");
                }
                else
                    stdout.WriteLine(text);
            }
            stderr.WriteLine(outcome.Succeeded
                ? $"Flow '{args.Pipeline}' finished: {outcome.Result?.Message}"
                : $"Flow '{args.Pipeline}' did not succeed (exit code {outcome.ExitCode}): {outcome.Reason}");
            return outcome.ExitCode;
        }

        FlowHeadlessOutcome Fail(int code, string reason) => new(code, null, reason, Array.Empty<string>());

        // 1. Arguments (exit code 3 when they do not make sense).
        if (args.Output != null && ParseFormat(args.Output, null) == null)
            return await Finish(Fail(FlowExitCodes.Validation, $"Unknown --output '{args.Output}'. Use json or markdown."));
        PermissionMode? permission = null;
        if (!string.IsNullOrWhiteSpace(args.Permission))
        {
            permission = ParsePermission(args.Permission);
            if (permission == null)
                return await Finish(Fail(FlowExitCodes.Validation, $"Unknown --permission '{args.Permission}'. Use Plan, Ask, AutoEdit or FullAuto."));
        }
        Dictionary<string, string> cast;
        try { cast = SdlcPipelineRunner.ParseCast(args.Cast); }
        catch (FormatException ex) { return await Finish(Fail(FlowExitCodes.Validation, ex.Message)); }

        if (!TryResolveTask(args.Task, args.TaskFile, out task, out var taskError))
            return await Finish(Fail(FlowExitCodes.Validation, taskError!));

        // 2. Providers and configuration (exit code 4).
        IServiceProvider services;
        SdlcPipelineRunner runner;
        try
        {
            services = await buildServices();
            runner = services.GetRequiredService<SdlcPipelineRunner>();
        }
        catch (Exception ex)
        {
            return await Finish(Fail(FlowExitCodes.Configuration, "Configuration error: " + ex.GetBaseException().Message));
        }

        // 3. The flow.
        var entry = services.GetRequiredService<SdlcPipelineLoader>().GetEntry(args.Pipeline);
        if (entry?.Pipeline == null)
            return await Finish(Fail(FlowExitCodes.Validation, $"Unknown pipeline '{args.Pipeline}'. Run 'pipeline list' to see available pipelines."));
        definition = entry.Pipeline;
        stderr.WriteLine($"Running {entry.ScopeLabel} flow '{args.Pipeline}' non-interactively" +
                         (permission != null ? $" (permission: {permission})" : "") + ".");

        var characters = services.GetService<ICharacterRegistry>();
        var headless = new FlowHeadlessRunner(runner);
        var outcome = await headless.RunAsync(definition, task, args.WorkingDirectory ?? Directory.GetCurrentDirectory(), new FlowHeadlessOptions
        {
            Model = args.Model,
            Cast = cast,
            MaxParallel = args.MaxParallel,
            MaxBudgetUsd = args.MaxBudgetUsd,
            Permission = permission,
            Timeout = args.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(args.TimeoutSeconds.Value) : null,
            Log = line => stderr.WriteLine(line),
            CharacterLookup = characters == null ? null : id => characters.GetAsync(id).GetAwaiter().GetResult()
        });
        return await Finish(outcome);
    }

    /// <summary>"json", "markdown", or null (none). A bare <c>--output-file</c> means markdown.</summary>
    internal static string? ParseFormat(string? output, string? outputFile)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.IsNullOrWhiteSpace(outputFile) ? null : "markdown";
        return output.Trim().ToLowerInvariant() switch
        {
            "json" => "json",
            "markdown" or "md" => "markdown",
            _ => null
        };
    }

    internal static PermissionMode? ParsePermission(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsDigit)) return null;
        return Enum.TryParse<PermissionMode>(text.Replace("-", string.Empty).Replace("_", string.Empty), ignoreCase: true, out var mode) ? mode : null;
    }

    /// <summary>The task comes from the argument or from <c>--task-file</c>, not both; it must not be empty.</summary>
    internal static bool TryResolveTask(string? task, string? taskFile, out string resolved, out string? error)
    {
        resolved = task ?? string.Empty;
        error = null;
        var hasText = !string.IsNullOrWhiteSpace(task);
        if (!string.IsNullOrWhiteSpace(taskFile))
        {
            if (hasText) { error = "Give the task as an argument or with --task-file, not both."; return false; }
            try { resolved = File.ReadAllText(taskFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Cannot read the task file '{taskFile}': {ex.Message}";
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(resolved))
        {
            error = string.IsNullOrWhiteSpace(taskFile) ? "No task. Give it as an argument or with --task-file." : $"The task file '{taskFile}' is empty.";
            return false;
        }
        return true;
    }
}
