using System.Globalization;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Flows;

/// <summary>Builds the <see cref="FlowRunRecord"/> of one flow run while it happens and saves it when it ends.</summary>
internal sealed class FlowRunRecorder : IDisposable
{
    private readonly FlowRunStore _store;
    private readonly SdlcPipelineDefinition _definition;
    private readonly string _task;
    private readonly string? _sessionModel;
    private readonly FlowReplayPlan? _replay;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly List<FlowStepRecord> _steps = new();
    private readonly List<FlowLogEntry> _events = new();
    private readonly Dictionary<string, string> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly AuditTrail? _audit;

    public FlowRunRecorder(FlowRunStore store, SdlcPipelineDefinition definition, string task, string? sessionModel, FlowReplayPlan? replay, ILogger logger,
        string? permissionMode = null, string? workingDirectory = null)
    {
        _store = store;
        _definition = definition;
        _task = task;
        _sessionModel = sessionModel;
        _replay = replay;
        _logger = logger;
        RunId = FlowRunStore.NewRunId(_started);
        // The audit file lives in the run's folder and grows while the flow runs (E7-S2).
        _audit = AuditTrail.TryOpen(
            Path.Combine(store.RunDirectory(definition.Name, RunId), AuditChain.FileName),
            new AuditRunInfo("flow", RunId, definition.Name, task, sessionModel, null, permissionMode, workingDirectory),
            logger: logger);
        if (_audit != null && replay != null)
            _audit.Record("flow.replay", null, new System.Text.Json.Nodes.JsonObject { ["sourceRun"] = replay.SourceRunId, ["fromStep"] = replay.FromNodeId });
    }

    /// <summary>Hash of the last audit line so far (null when the audit file could not be created).</summary>
    public string? AuditHead => _audit?.HeadHash;

    /// <summary>A step run starts: from now on its events in the audit file carry the character and model.</summary>
    public void NodeStarted(string nodeId, int round, string? characterId, string? model, string? provider) =>
        _audit?.StepStarted(nodeId, round, characterId, model, provider);

    public void Dispose() => _audit?.Dispose();

    public string RunId { get; }

    /// <summary>Adds a line to the event log for the events that tell the story of the run (not the streamed text).</summary>
    public void Observe(AgentEvent evt)
    {
        var now = DateTimeOffset.UtcNow;
        _audit?.Observe(evt);
        if (evt is FlowEdgeTakenEvent edge)
            _audit?.Record("flow.edge", null, new System.Text.Json.Nodes.JsonObject
            {
                ["from"] = edge.From, ["to"] = edge.To, ["when"] = edge.When, ["loop"] = edge.IsLoop
            });
        switch (evt)
        {
            case FlowStartedEvent s:
                _events.Add(new FlowLogEntry(now, "started", null, $"{s.FlowName}: {string.Join(" · ", s.Waves.Select((w, i) => $"{i + 1}: {string.Join(" ∥ ", w)}"))}"));
                break;
            case FlowNodeStatusEvent n:
                _events.Add(new FlowLogEntry(now, n.State.ToString().ToLowerInvariant(), n.NodeId,
                    $"round {n.Iteration}" + (n.Outcome != null ? $" → {n.Outcome}" : "") + (n.Detail != null ? $" ({n.Detail})" : "")));
                break;
            case FlowEdgeTakenEvent e:
                _events.Add(new FlowLogEntry(now, e.IsLoop ? "loop" : "edge", e.From, $"{e.From} → {e.To}" + (e.When != null ? $" when {e.When}" : "") + (e.IsLoop ? $" (loop {e.LoopCount})" : "")));
                break;
            case FlowUsageEvent u:
                _events.Add(new FlowLogEntry(now, "usage", u.NodeId, $"{FlowUsageFormat.Badge(u.Node)} · {FlowUsageFormat.Header(u.Run, u.BudgetUsd)}"));
                break;
            case StatusUpdateEvent st:
                _events.Add(new FlowLogEntry(now, "status", null, st.Status + (st.Detail != null ? ": " + st.Detail : "")));
                break;
            case AgentTaggedEvent { Inner: AgentErrorEvent err } t:
                _events.Add(new FlowLogEntry(now, "error", t.AgentId, err.Error.Message));
                break;
        }
    }

    /// <summary>Adds a step run; its answer is saved as <c>&lt;node&gt;.r&lt;round&gt;.md</c>.</summary>
    public void AddStep(FlowStepRecord step, string output)
    {
        var file = $"{step.NodeId}.r{step.Round.ToString(CultureInfo.InvariantCulture)}.md";
        // A node that ran again in a re-run keeps both files apart.
        for (var i = 2; _outputs.ContainsKey(file); i++) file = $"{step.NodeId}.r{step.Round}-{i}.md";
        _outputs[file] = output;
        _steps.Add(step with { OutputFile = file });
        _audit?.StepFinished(step.NodeId, step.Round, step.State.ToString().ToLowerInvariant(), step.Outcome, step.Detail,
            step.Usage?.Tokens ?? 0, step.Usage?.CostUsd ?? 0m, step.Duration, step.ToolCalls.Count, step.Diffs.Count);
    }

    /// <summary>Saves the run; a failure to write is logged and never fails the flow. Returns the run id, or null when nothing was saved.</summary>
    public string? Save(FlowRunResult final)
    {
        var usage = final.Usage;
        // Close the audit file first, so the record can carry its head hash.
        _audit?.Complete(final.Status.ToString().ToLowerInvariant(), final.Message, DateTimeOffset.UtcNow - _started, usage.Tokens, usage.CostUsd);
        try
        {
            var record = new FlowRunRecord
            {
                Id = RunId,
                FlowName = _definition.Name,
                Task = _task,
                SessionModel = _sessionModel,
                StartedAt = _started,
                FinishedAt = DateTimeOffset.UtcNow,
                Status = final.Status,
                Message = final.Message,
                BudgetUsd = final.BudgetUsd,
                CostUsd = usage.CostUsd,
                Tokens = usage.Tokens,
                ReplayOfRunId = _replay?.SourceRunId,
                ReplayFromNodeId = _replay?.FromNodeId,
                Definition = _definition,
                Steps = _steps,
                Events = _events,
                AuditHead = _audit?.HeadHash,
                AuditEntries = _audit == null ? null : (int)_audit.EntryCount
            };
            _store.Save(record, _outputs);
            return RunId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save flow run {Run} of {Flow}", RunId, _definition.Name);
            return null;
        }
    }
}
