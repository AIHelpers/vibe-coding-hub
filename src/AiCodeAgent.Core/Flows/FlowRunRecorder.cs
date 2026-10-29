using System.Globalization;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Flows;

/// <summary>Builds the <see cref="FlowRunRecord"/> of one flow run while it happens and saves it when it ends.</summary>
internal sealed class FlowRunRecorder
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

    public FlowRunRecorder(FlowRunStore store, SdlcPipelineDefinition definition, string task, string? sessionModel, FlowReplayPlan? replay, ILogger logger)
    {
        _store = store;
        _definition = definition;
        _task = task;
        _sessionModel = sessionModel;
        _replay = replay;
        _logger = logger;
        RunId = FlowRunStore.NewRunId(_started);
    }

    public string RunId { get; }

    /// <summary>Adds a line to the event log for the events that tell the story of the run (not the streamed text).</summary>
    public void Observe(AgentEvent evt)
    {
        var now = DateTimeOffset.UtcNow;
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
    }

    /// <summary>Saves the run; a failure to write is logged and never fails the flow. Returns the run id, or null when nothing was saved.</summary>
    public string? Save(FlowRunResult final)
    {
        try
        {
            var usage = final.Usage;
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
                Events = _events
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
