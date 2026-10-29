using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.CLI;

/// <summary>
/// Runs an <see cref="SdlcPipelineDefinition"/> end-to-end from the CLI, printing a header whenever
/// the active stage changes and streaming each stage's text/tool activity, mirroring SingleRunMode
/// but for a multi-stage, multi-role session.
/// </summary>
public class PipelineRunMode
{
    private readonly SdlcPipelineRunner _runner;

    public PipelineRunMode(SdlcPipelineRunner runner) => _runner = runner;

    public async Task RunAsync(SdlcPipelineDefinition pipeline, string task, string workingDirectory, string? model,
        IReadOnlyDictionary<string, string>? cast = null, int? maxParallel = null, decimal? maxBudgetUsd = null, bool json = false)
    {
        if (pipeline.IsFlow)
        {
            await RunFlowAsync(pipeline, task, workingDirectory, model, cast, maxParallel, maxBudgetUsd, json);
            return;
        }
        if (json || maxBudgetUsd != null)
            Console.Error.WriteLine("Note: --json and --max-budget apply to flows (pipelines with edges); this linear pipeline ignores them.");

        Console.OutputEncoding = Encoding.UTF8;
        var sessionId = Guid.NewGuid().ToString();

        Console.WriteLine($"Running pipeline '{pipeline.Name}': {pipeline.Description}");
        Console.WriteLine($"Task: {task}");
        if (cast is { Count: > 0 })
            Console.WriteLine($"Cast: {string.Join(", ", cast.Select(kv => $"{kv.Key} = {kv.Value}"))}");
        Console.WriteLine();

        string? currentRole = null;

        await foreach (var evt in _runner.RunAsync(pipeline, task, sessionId, workingDirectory, model, cast: cast))
        {
            if (evt is not AgentTaggedEvent tagged)
                continue;

            if (tagged.AgentId + "/" + tagged.Role != currentRole)
            {
                currentRole = tagged.AgentId + "/" + tagged.Role;
                Console.WriteLine();
                Console.WriteLine($"===== Stage: {tagged.Role} (agent: {tagged.AgentId}) =====");
            }

            switch (tagged.Inner)
            {
                case TextDeltaEvent delta:
                    Console.Write(delta.Delta);
                    break;

                case ToolCallStartEvent toolStart:
                    Console.Error.WriteLine($"\n[TOOL] {toolStart.Call.Name}");
                    break;

                case ToolCallEndEvent toolEnd:
                    var icon = toolEnd.Result.IsError ? "ERROR" : "OK";
                    Console.Error.WriteLine($"[{icon}] {toolEnd.Call.Name} ({toolEnd.Duration.TotalMilliseconds:F0}ms)");
                    break;

                case ApprovalRequestEvent approval:
                    approval.Approval.TrySetResult(AskYesNo($"\nApprove {approval.Call.Name} for stage '{tagged.Role}'? [y/N] "));
                    break;

                case AgentFinishedEvent finished:
                    Console.WriteLine();
                    if (finished.Response.WasCancelled)
                        Console.WriteLine($"[{tagged.Role}] cancelled");
                    break;

                case AgentErrorEvent error:
                    Console.Error.WriteLine($"[{tagged.Role}] Error: {error.Error.Message}");
                    break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Pipeline complete.");
    }

    /// <summary>Ask a yes/no question; works with a terminal and with redirected input (an empty answer is no).</summary>
    private static bool AskYesNo(string question)
    {
        Console.Error.Write(question);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine();
            return line != null && line.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }
        var key = Console.ReadKey(intercept: false);
        Console.Error.WriteLine();
        return key.Key == ConsoleKey.Y;
    }

    /// <summary>Reads one line of text (an empty line or no input is null).</summary>
    private static string? AskLine(string question)
    {
        Console.Error.Write(question);
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }

    /// <summary>
    /// The gate in front of a step: approve, approve with a note (it is added to the step's prompt), skip the step, or stop the flow.
    /// Without a terminal (redirected input) the first word of a line decides: y/a = approve, n = note, s = skip; anything else stops.
    /// </summary>
    internal static void AnswerGate(ApprovalRequestEvent approval, string stage)
    {
        Console.Error.Write($"\nStart '{stage}'? [a]pprove / approve with [n]ote / [s]kip step / s[t]op flow: ");
        var choice = Console.IsInputRedirected
            ? (Console.ReadLine()?.Trim().FirstOrDefault() ?? 't')
            : Console.ReadKey(intercept: false).KeyChar;
        if (!Console.IsInputRedirected) Console.Error.WriteLine();
        switch (char.ToLowerInvariant(choice))
        {
            case 'y':
            case 'a':
                approval.Approval.TrySetResult(true);
                break;
            case 'n':
                approval.Feedback.Text = AskLine("Note for the step: ");
                approval.Approval.TrySetResult(true);
                break;
            case 's':
                approval.Feedback.Skip = true;
                approval.Approval.TrySetResult(false);
                break;
            default:
                approval.Approval.TrySetResult(false);
                break;
        }
    }

    /// <summary>A human step: show what it needs a decision on, then take one of its outcomes (or stop the flow).</summary>
    internal static void AnswerHuman(FlowHumanRequestEvent request)
    {
        Console.Error.WriteLine($"\n── '{request.NodeId}' needs your decision ──\n{request.Prompt}\n");
        if (request.Outcomes.Count == 0)
        {
            var go = AskLine("Press Enter to continue (or type a note), q to stop: ");
            request.Choice.TrySetResult(string.Equals(go, "q", StringComparison.OrdinalIgnoreCase)
                ? new FlowHumanChoice(null, null, Stop: true)
                : new FlowHumanChoice(null, go));
            return;
        }
        for (var i = 0; i < request.Outcomes.Count; i++)
            Console.Error.WriteLine($"  {i + 1}) {request.Outcomes[i]}");
        var answer = AskLine("Choose a number or name (q = stop the flow): ");
        if (answer == null || string.Equals(answer, "q", StringComparison.OrdinalIgnoreCase))
        {
            request.Choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
            return;
        }
        var outcome = int.TryParse(answer, out var n) && n >= 1 && n <= request.Outcomes.Count
            ? request.Outcomes[n - 1]
            : request.Outcomes.FirstOrDefault(o => string.Equals(o, answer, StringComparison.OrdinalIgnoreCase));
        if (outcome == null)
        {
            request.Choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
            return;
        }
        request.Choice.TrySetResult(new FlowHumanChoice(outcome, AskLine("Note (optional): ")));
    }

    /// <summary>
    /// Flows run several characters at once, so their text is not streamed (it would interleave).
    /// Instead each node prints status lines and its answer when it finishes.
    /// </summary>
    private async Task RunFlowAsync(SdlcPipelineDefinition pipeline, string task, string workingDirectory, string? model,
        IReadOnlyDictionary<string, string>? cast, int? maxParallel, decimal? maxBudgetUsd, bool json)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sessionId = Guid.NewGuid().ToString();
        // With --json, stdout carries only the JSON result; everything else goes to stderr.
        var log = json ? Console.Error : Console.Out;
        log.WriteLine($"Running flow '{pipeline.Name}': {pipeline.Description}");
        log.WriteLine($"Task: {task}");
        if (cast is { Count: > 0 })
            log.WriteLine($"Cast: {string.Join(", ", cast.Select(kv => $"{kv.Key} = {kv.Value}"))}");

        FlowRunResult? result = null;
        await foreach (var evt in _runner.RunAsync(pipeline, task, sessionId, workingDirectory, model, cast: cast, maxParallel: maxParallel, maxBudgetUsd: maxBudgetUsd))
        {
            if (evt is AgentTaggedEvent { Inner: ApprovalRequestEvent approval } gate)
            {
                if (approval.Call.Name == "pipeline_stage")
                    AnswerGate(approval, gate.AgentId ?? "?");
                else
                    approval.Approval.TrySetResult(AskYesNo($"\n'{gate.AgentId}' wants to run {approval.Call.Name}. Allow? [y/N] "));
                continue;
            }
            if (evt is AgentTaggedEvent { Inner: FlowHumanRequestEvent human })
            {
                AnswerHuman(human);
                continue;
            }
            if (evt is FlowFinishedEvent finished) result = finished.Result;
            var line = FlowConsoleFormatter.Format(evt);
            if (line != null) log.WriteLine(line);
        }

        log.WriteLine();
        if (result != null)
        {
            log.WriteLine(FlowConsoleFormatter.Summary(result));
            if (result.RunId != null) log.WriteLine($"Run saved: {result.RunId}");
            if (json) Console.WriteLine(FlowUsageFormat.ToJson(pipeline.Name, result));
            if (!result.Succeeded) Environment.ExitCode = 1;
        }
    }
}

/// <summary>Plain-text lines for flow events (CLI and logs).</summary>
public static class FlowConsoleFormatter
{
    public static string? Format(AgentEvent evt) => evt switch
    {
        FlowStartedEvent s => $"Waves: {string.Join(" · ", s.Waves.Select((w, i) => $"{i + 1}: {string.Join(" ∥ ", w)}"))}\n",
        FlowNodeStatusEvent { State: FlowNodeState.Running } n =>
            $"▶ {n.NodeId}{Who(n)} running{(n.Iteration > 1 ? $" (round {n.Iteration})" : "")}",
        FlowNodeStatusEvent { State: FlowNodeState.Done } n =>
            $"✓ {n.NodeId}{Who(n)} done{(n.Outcome != null ? $" → {n.Outcome}" : "")}{(n.Detail != null ? $" ({n.Detail})" : "")}",
        FlowNodeStatusEvent { State: FlowNodeState.Failed } n => $"✕ {n.NodeId}{Who(n)} failed: {n.Detail}",
        FlowNodeStatusEvent { State: FlowNodeState.Skipped } n => $"– {n.NodeId} skipped ({n.Detail})",
        FlowNodeStatusEvent { State: FlowNodeState.Pending, Detail: { } d } n when d.StartsWith("sent back", StringComparison.Ordinal) => $"↺ {n.NodeId} {d}",
        FlowUsageEvent u => $"  {u.NodeId}: {FlowUsageFormat.Badge(u.Node)} · {FlowUsageFormat.Header(u.Run, u.BudgetUsd)}",
        FlowEdgeTakenEvent { IsLoop: true } e => $"↺ {e.From} → {e.To} ({e.When ?? "always"}, loop {e.LoopCount})",
        AgentTaggedEvent { Inner: AgentFinishedEvent f } t when !string.IsNullOrWhiteSpace(f.Response.Content) =>
            $"\n───── {t.AgentId} ─────\n{f.Response.Content.Trim()}\n",
        AgentTaggedEvent { Inner: AgentErrorEvent err } t => $"[{t.AgentId}] error: {err.Error.Message}",
        AgentTaggedEvent { Inner: StatusUpdateEvent st } t when st.Status.StartsWith("Worktree", StringComparison.Ordinal) => $"  [{t.AgentId}] {st.Status}: {st.Detail}",
        StatusUpdateEvent { Status: "Flow invalid" } st => $"Flow is invalid: {st.Detail}",
        StatusUpdateEvent { Status: "Loop limit" } st => $"  {st.Detail}",
        _ => null
    };

    public static string Summary(FlowRunResult result)
    {
        var sb = new StringBuilder($"Flow {FlowUsageFormat.StatusName(result.Status)}: {result.Message}\n");
        foreach (var n in result.Nodes)
        {
            var mark = n.State switch { FlowNodeState.Done => "✓", FlowNodeState.Failed => "✕", FlowNodeState.Skipped => "–", _ => "·" };
            sb.Append($"  {mark} {n.NodeId,-18} {n.CharacterId ?? "",-18} runs: {n.Runs}  {n.Duration.TotalSeconds,5:F0}s");
            if (n.Outcome != null) sb.Append($"  outcome: {n.Outcome}");
            if (n.Detail != null) sb.Append($"  ({n.Detail})");
            sb.Append('\n');
        }
        if (result.StepRuns.Count > 0)
            sb.Append('\n').Append(FlowUsageFormat.Table(result)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string Who(FlowNodeStatusEvent n) => string.IsNullOrEmpty(n.CharacterId) ? "" : $" ({n.CharacterId})";
}
