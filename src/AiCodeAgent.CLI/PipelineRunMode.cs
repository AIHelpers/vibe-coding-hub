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
        IReadOnlyDictionary<string, string>? cast = null, int? maxParallel = null)
    {
        if (pipeline.IsFlow)
        {
            await RunFlowAsync(pipeline, task, workingDirectory, model, cast, maxParallel);
            return;
        }

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

    /// <summary>
    /// Flows run several characters at once, so their text is not streamed (it would interleave).
    /// Instead each node prints status lines and its answer when it finishes.
    /// </summary>
    private async Task RunFlowAsync(SdlcPipelineDefinition pipeline, string task, string workingDirectory, string? model,
        IReadOnlyDictionary<string, string>? cast, int? maxParallel)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sessionId = Guid.NewGuid().ToString();
        Console.WriteLine($"Running flow '{pipeline.Name}': {pipeline.Description}");
        Console.WriteLine($"Task: {task}");
        if (cast is { Count: > 0 })
            Console.WriteLine($"Cast: {string.Join(", ", cast.Select(kv => $"{kv.Key} = {kv.Value}"))}");

        FlowRunResult? result = null;
        await foreach (var evt in _runner.RunAsync(pipeline, task, sessionId, workingDirectory, model, cast: cast, maxParallel: maxParallel))
        {
            if (evt is AgentTaggedEvent { Inner: ApprovalRequestEvent approval } gate)
            {
                var question = approval.Call.Name == "pipeline_stage"
                    ? $"\nStart '{gate.AgentId}'? [y/N] "
                    : $"\n'{gate.AgentId}' wants to run {approval.Call.Name}. Allow? [y/N] ";
                approval.Approval.TrySetResult(AskYesNo(question));
                continue;
            }
            if (evt is FlowFinishedEvent finished) result = finished.Result;
            var line = FlowConsoleFormatter.Format(evt);
            if (line != null) Console.WriteLine(line);
        }

        Console.WriteLine();
        if (result != null)
        {
            Console.WriteLine(FlowConsoleFormatter.Summary(result));
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
        var sb = new StringBuilder($"Flow {result.Status.ToString().ToLowerInvariant()}: {result.Message}\n");
        foreach (var n in result.Nodes)
        {
            var mark = n.State switch { FlowNodeState.Done => "✓", FlowNodeState.Failed => "✕", FlowNodeState.Skipped => "–", _ => "·" };
            sb.Append($"  {mark} {n.NodeId,-18} {n.CharacterId ?? "",-18} runs: {n.Runs}  {n.Duration.TotalSeconds,5:F0}s");
            if (n.Outcome != null) sb.Append($"  outcome: {n.Outcome}");
            if (n.Detail != null) sb.Append($"  ({n.Detail})");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Who(FlowNodeStatusEvent n) => string.IsNullOrEmpty(n.CharacterId) ? "" : $" ({n.CharacterId})";
}
