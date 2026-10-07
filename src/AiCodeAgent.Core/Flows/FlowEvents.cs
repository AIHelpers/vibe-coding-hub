using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Flows;

/// <summary>Where a flow node is in its life cycle.</summary>
public enum FlowNodeState
{
    /// <summary>Waiting for the nodes before it.</summary>
    Pending,
    /// <summary>The character is working.</summary>
    Running,
    /// <summary>Finished; its output was handed on.</summary>
    Done,
    /// <summary>Failed (error, step limit, missing outcome); nodes after it are blocked.</summary>
    Failed,
    /// <summary>Not run: its path was not chosen, it was blocked by a failure, disabled, or the run stopped.</summary>
    Skipped
}

public enum FlowRunStatus
{
    Succeeded,
    /// <summary>At least one node failed.</summary>
    Failed,
    /// <summary>Ended early on purpose: loop limit, run limit, or a declined confirmation.</summary>
    Stopped,
    Cancelled
}

/// <summary>Per-node summary of a finished flow run.</summary>
public sealed record FlowNodeRunSummary(
    string NodeId,
    string? CharacterId,
    FlowNodeState State,
    int Runs,
    string? Outcome,
    TimeSpan Duration,
    string? Output,
    string? Detail);

public sealed record FlowRunResult(FlowRunStatus Status, string Message, IReadOnlyList<FlowNodeRunSummary> Nodes)
{
    public bool Succeeded => Status == FlowRunStatus.Succeeded;
}

/// <summary>A flow started: its nodes and the waves of work that can run in parallel.</summary>
public sealed record FlowStartedEvent(string FlowName, IReadOnlyList<string> NodeIds, IReadOnlyList<IReadOnlyList<string>> Waves) : AgentEvent;

/// <summary>A node changed state. <paramref name="Iteration"/> counts runs of this node (1 = first run, 2+ = loops).</summary>
public sealed record FlowNodeStatusEvent(
    string NodeId,
    FlowNodeState State,
    int Iteration,
    string? Detail = null,
    string? Outcome = null,
    string? CharacterId = null) : AgentEvent;

/// <summary>An edge was taken: <paramref name="To"/> gets <paramref name="From"/>'s output (or, for a loop, runs again).</summary>
public sealed record FlowEdgeTakenEvent(string From, string To, string? When, bool IsLoop, int LoopCount = 0) : AgentEvent;

/// <summary>The flow finished (or stopped, failed, was cancelled).</summary>
public sealed record FlowFinishedEvent(FlowRunResult Result) : AgentEvent;
