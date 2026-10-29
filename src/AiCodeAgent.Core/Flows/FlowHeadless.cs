using System.Text;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Flows;

/// <summary>Process exit codes of <c>aiagent pipeline run --non-interactive</c>.</summary>
public static class FlowExitCodes
{
    public const int Success = 0;
    /// <summary>A step failed, the flow was stopped (a gate or a human step nobody can answer, a loop or run limit) or it timed out.</summary>
    public const int Failed = 1;
    public const int BudgetExceeded = 2;
    /// <summary>Bad arguments, an unknown or invalid flow, a missing task.</summary>
    public const int Validation = 3;
    /// <summary>The provider or the configuration could not be set up (unknown provider, missing key).</summary>
    public const int Configuration = 4;

    public static int FromStatus(FlowRunStatus status) => status switch
    {
        FlowRunStatus.Succeeded => Success,
        FlowRunStatus.BudgetExceeded => BudgetExceeded,
        _ => Failed
    };
}

/// <summary>How a headless run is set up.</summary>
public sealed record FlowHeadlessOptions
{
    public string? Model { get; init; }
    public IReadOnlyDictionary<string, string>? Cast { get; init; }
    public int? MaxParallel { get; init; }
    public decimal? MaxBudgetUsd { get; init; }
    /// <summary>Permission mode for every step (replaces the role defaults); null = each step keeps its own. Anything that still needs approval is denied.</summary>
    public PermissionMode? Permission { get; init; }
    /// <summary>Wall-clock limit for the whole run; null = none. A run that exceeds it is cancelled and exits with code 1.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Called with progress lines (the CLI writes them to stderr).</summary>
    public Action<string>? Log { get; init; }
    /// <summary>Resolves a character id for validation; null skips the character checks.</summary>
    public Func<string, CharacterInfo?>? CharacterLookup { get; init; }
}

/// <summary>The outcome of a headless run: the exit code to return and why.</summary>
/// <param name="Reason">One sentence for logs and the PR comment; empty on success.</param>
/// <param name="Denied">Tool requests that were denied because nobody could approve them ("agent: tool").</param>
public sealed record FlowHeadlessOutcome(
    int ExitCode, FlowRunResult? Result, string Reason, IReadOnlyList<string> Denied, string? RunId = null)
{
    public bool Succeeded => ExitCode == FlowExitCodes.Success;
}

/// <summary>
/// Runs a flow with nobody at the keyboard (CI). Every question the flow would ask is answered at once, so the run
/// can never wait for input: a tool call that needs approval is denied, a step that asks for confirmation (or a human
/// step) ends the flow with a clear reason.
/// </summary>
public sealed class FlowHeadlessRunner
{
    public const string DeniedNote =
        "Denied: this is a non-interactive run and the permission mode does not allow it without approval. Carry on without it.";

    private readonly SdlcPipelineRunner _runner;

    public FlowHeadlessRunner(SdlcPipelineRunner runner) => _runner = runner;

    public async Task<FlowHeadlessOutcome> RunAsync(
        SdlcPipelineDefinition pipeline, string task, string workingDirectory, FlowHeadlessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FlowHeadlessOptions();
        if (!pipeline.IsFlow)
            return new FlowHeadlessOutcome(FlowExitCodes.Validation, null,
                $"'{pipeline.Name}' is a linear pipeline. Non-interactive runs need a flow (a pipeline with edges).", Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(task))
            return new FlowHeadlessOutcome(FlowExitCodes.Validation, null, "The task is empty.", Array.Empty<string>());

        var graph = FlowGraph.Build(pipeline, options.CharacterLookup, options.Cast);
        if (!graph.IsValid)
        {
            var errors = string.Join(" ", graph.Problems.Where(p => p.IsError).Select(p => p.Message));
            return new FlowHeadlessOutcome(FlowExitCodes.Validation, null, "The flow is invalid: " + errors, Array.Empty<string>());
        }

        using var timeout = options.Timeout is { } limit && limit > TimeSpan.Zero ? new CancellationTokenSource(limit) : null;
        using var linked = timeout == null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked?.Token ?? cancellationToken;

        var denied = new List<string>();
        string? needsPerson = null;
        FlowRunResult? result = null;
        try
        {
            await foreach (var evt in _runner.RunAsync(pipeline, task, Guid.NewGuid().ToString(), workingDirectory, options.Model,
                               cancellationToken: token, cast: options.Cast, maxParallel: options.MaxParallel,
                               maxBudgetUsd: options.MaxBudgetUsd, permissionMode: options.Permission).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AgentTaggedEvent { Inner: ApprovalRequestEvent approval } tagged:
                        if (approval.Call.Name == "pipeline_stage")
                        {
                            needsPerson ??= $"Step '{tagged.AgentId}' asks for confirmation (requireConfirmation), which a non-interactive run cannot give.";
                            options.Log?.Invoke($"✕ {tagged.AgentId}: needs confirmation; stopping the flow");
                        }
                        else
                        {
                            approval.Feedback.Text = DeniedNote;
                            denied.Add($"{tagged.AgentId}: {approval.Call.Name}");
                            options.Log?.Invoke($"– {tagged.AgentId}: {approval.Call.Name} denied (non-interactive)");
                        }
                        approval.Approval.TrySetResult(false);
                        break;

                    case AgentTaggedEvent { Inner: FlowHumanRequestEvent human }:
                        needsPerson ??= $"Step '{human.NodeId}' is a human step and needs a person to choose ({string.Join(" | ", human.Outcomes)}); a non-interactive run cannot.";
                        options.Log?.Invoke($"✕ {human.NodeId}: needs a human decision; stopping the flow");
                        human.Choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
                        break;

                    case StatusUpdateEvent { Status: "Flow invalid" } invalid:
                        options.Log?.Invoke("Flow is invalid: " + invalid.Detail);
                        break;

                    case FlowFinishedEvent finished:
                        result = finished.Result;
                        break;

                    default:
                        if (options.Log != null && Describe(evt) is { } line) options.Log(line);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The time limit fired while the flow was winding down.
        }

        var timedOut = timeout != null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        if (result == null)
            return new FlowHeadlessOutcome(FlowExitCodes.Failed, null,
                timedOut ? $"The run did not finish within {Format(options.Timeout!.Value)}." : "The flow ended without a result.", denied);

        if (result.Message.StartsWith("The flow is invalid", StringComparison.Ordinal))
            return new FlowHeadlessOutcome(FlowExitCodes.Validation, result, result.Message, denied, result.RunId);

        var code = FlowExitCodes.FromStatus(result.Status);
        string reason;
        if (needsPerson != null && result.Status != FlowRunStatus.Succeeded)
        {
            code = FlowExitCodes.Failed;
            reason = needsPerson;
        }
        else if (timedOut)
        {
            code = FlowExitCodes.Failed;
            reason = $"The run did not finish within {Format(options.Timeout!.Value)} and was stopped.";
        }
        else
            reason = result.Succeeded ? string.Empty : result.Message;
        return new FlowHeadlessOutcome(code, result, reason, denied, result.RunId);
    }

    private static string? Describe(AgentEvent evt) => evt switch
    {
        FlowNodeStatusEvent { State: FlowNodeState.Running } n => $"▶ {n.NodeId}",
        FlowNodeStatusEvent { State: FlowNodeState.Done } n => $"✓ {n.NodeId}{(n.Outcome != null ? $" → {n.Outcome}" : "")}",
        FlowNodeStatusEvent { State: FlowNodeState.Failed } n => $"✕ {n.NodeId}: {n.Detail}",
        FlowNodeStatusEvent { State: FlowNodeState.Skipped } n => $"– {n.NodeId} skipped ({n.Detail})",
        _ => null
    };

    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalSeconds:0.#} s";
}

/// <summary>The result of a flow run as a PR-comment-sized Markdown report or as JSON.</summary>
public static class FlowReport
{
    /// <summary>Hidden first line of the Markdown report; the GitHub action uses it to find and update its comment.</summary>
    public static string Marker(string flowName) => $"<!-- vibe-coding-hub:flow:{flowName} -->";

    /// <summary>GitHub rejects comments over 65 536 characters.</summary>
    public const int MaxLength = 60_000;
    private const int MaxStepOutput = 6_000;

    public static string Markdown(SdlcPipelineDefinition pipeline, string task, FlowHeadlessOutcome outcome)
    {
        var sb = new StringBuilder();
        var result = outcome.Result;
        sb.AppendLine(Marker(pipeline.Name));
        sb.AppendLine($"## {Icon(outcome)} Flow `{pipeline.Name}` — {StatusText(outcome)}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(outcome.Reason)) sb.AppendLine($"**{outcome.Reason.Trim()}**").AppendLine();
        else if (result != null) sb.AppendLine(result.Message).AppendLine();

        if (result != null)
        {
            var usage = result.Usage;
            var done = result.Nodes.Count(n => n.State == FlowNodeState.Done);
            var line = $"{done}/{result.Nodes.Count} steps · {FlowUsageFormat.Tokens(usage.Tokens)} tokens · {FlowUsageFormat.Cost(usage.CostUsd)}" +
                       (usage.HasUnpriced ? " (some models unpriced)" : "") +
                       $" · {result.StepRuns.Sum(r => r.Duration.TotalSeconds):0}s of model time";
            if (result.BudgetUsd is { } cap) line += $" · budget {FlowUsageFormat.Cost(cap)}";
            sb.AppendLine(line).AppendLine();

            // The steps nothing else consumes carry the answer (e.g. the summary of a review).
            var graph = FlowGraph.Build(pipeline);
            var sinks = result.Nodes.Where(n => n.State == FlowNodeState.Done && !string.IsNullOrWhiteSpace(n.Output) &&
                                                !graph.Outgoing(n.NodeId).Any(e => !e.IsLoop)).ToList();
            foreach (var sink in sinks)
            {
                sb.AppendLine(sinks.Count > 1 ? $"### {sink.NodeId}" + (sink.Outcome != null ? $" — {sink.Outcome}" : "") : "### Result" + (sink.Outcome != null ? $" — {sink.Outcome}" : ""));
                sb.AppendLine().AppendLine(Cap(sink.Output!.Trim(), MaxStepOutput)).AppendLine();
            }

            sb.AppendLine("<details><summary>Steps</summary>").AppendLine();
            sb.AppendLine("| Step | State | Outcome | Tokens | Cost | Time |").AppendLine("|---|---|---|---|---|---|");
            foreach (var n in result.Nodes)
            {
                var runs = result.StepRuns.Where(r => r.NodeId == n.NodeId).ToList();
                var totals = FlowUsageTotals.Sum(runs);
                sb.AppendLine($"| `{n.NodeId}` | {StateIcon(n.State)} {n.State.ToString().ToLowerInvariant()}{(n.Detail != null && n.State != FlowNodeState.Done ? $" ({Cell(n.Detail)})" : "")} | {n.Outcome ?? ""} | " +
                              $"{(runs.Count == 0 ? "" : FlowUsageFormat.Tokens(totals.Tokens))} | {(runs.Count == 0 ? "" : FlowUsageFormat.Cost(totals.CostUsd))} | {n.Duration.TotalSeconds:0}s |");
            }
            sb.AppendLine().AppendLine("</details>").AppendLine();

            foreach (var n in result.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Output) && !sinks.Contains(n)))
            {
                sb.AppendLine($"<details><summary>{StateIcon(n.State)} {n.NodeId}{(n.Outcome != null ? $" — {n.Outcome}" : "")}</summary>").AppendLine();
                sb.AppendLine(Cap(n.Output!.Trim(), MaxStepOutput)).AppendLine().AppendLine("</details>").AppendLine();
            }
        }

        if (outcome.Denied.Count > 0)
            sb.AppendLine($"_{outcome.Denied.Count} tool request(s) needed approval and were denied (non-interactive run): {string.Join(", ", outcome.Denied.Distinct().Take(10))}._").AppendLine();
        if (outcome.RunId != null) sb.AppendLine($"<sub>Run `{outcome.RunId}`</sub>");

        var text = sb.ToString().TrimEnd();
        return text.Length <= MaxLength ? text : text[..(MaxLength - 60)] + "\n\n_…report shortened to fit a comment._";
    }

    /// <summary>The run as JSON: <see cref="FlowUsageFormat.ToJson"/> plus the exit code, the reason and every step's output.</summary>
    public static string Json(SdlcPipelineDefinition pipeline, FlowHeadlessOutcome outcome)
    {
        var result = outcome.Result;
        JsonElement? usage = result == null ? null : JsonDocument.Parse(FlowUsageFormat.ToJson(pipeline.Name, result)).RootElement;
        var payload = new Dictionary<string, object?>
        {
            ["flow"] = pipeline.Name,
            ["exitCode"] = outcome.ExitCode,
            ["status"] = result == null ? "not_run" : FlowUsageFormat.StatusName(result.Status),
            ["reason"] = outcome.Reason,
            ["runId"] = outcome.RunId,
            ["deniedToolRequests"] = outcome.Denied,
            ["message"] = result?.Message,
            ["totals"] = usage?.GetProperty("totals"),
            ["steps"] = usage?.GetProperty("steps"),
            ["nodes"] = result?.Nodes.Select(n => new
            {
                step = n.NodeId,
                state = n.State.ToString().ToLowerInvariant(),
                runs = n.Runs,
                outcome = n.Outcome,
                detail = n.Detail,
                output = n.Output
            }).ToList()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Icon(FlowHeadlessOutcome o) => o.ExitCode switch
    {
        FlowExitCodes.Success => "✅",
        FlowExitCodes.BudgetExceeded => "💸",
        _ => "❌"
    };

    private static string StatusText(FlowHeadlessOutcome o) =>
        o.Result == null ? (o.ExitCode == FlowExitCodes.Validation ? "invalid" : o.ExitCode == FlowExitCodes.Configuration ? "not configured" : "failed")
            : FlowUsageFormat.StatusName(o.Result.Status);

    private static string StateIcon(FlowNodeState s) => s switch
    {
        FlowNodeState.Done => "✅",
        FlowNodeState.Failed => "❌",
        FlowNodeState.Skipped => "⏭",
        FlowNodeState.Running => "▶",
        _ => "⏳"
    };

    private static string Cell(string s) => s.ReplaceLineEndings(" ").Replace("|", "/");

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + "\n\n_…output shortened._";
}
