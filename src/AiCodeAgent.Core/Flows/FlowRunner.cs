using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Flows;

/// <summary>Run-time settings for a flow.</summary>
public sealed record FlowRunOptions
{
    public string? Model { get; init; }
    public int MaxIterationsPerNode { get; init; } = 30;
    /// <summary>Character overrides by stage name or role (<c>--cast</c>).</summary>
    public IReadOnlyDictionary<string, string>? Cast { get; init; }
    /// <summary>Most nodes running at once (default <see cref="FlowRunner.DefaultMaxParallel"/>).</summary>
    public int? MaxParallel { get; init; }
    /// <summary>Safety cap on node runs across the whole flow, loops included.</summary>
    public int MaxTotalRuns { get; init; } = FlowRunner.DefaultMaxTotalRuns;
    /// <summary>
    /// Permission mode for every step, replacing the one its role or character asks for (headless runs: <c>--permission</c>).
    /// Null = each step keeps its own.
    /// </summary>
    public PermissionMode? PermissionMode { get; init; }
    /// <summary>How long a confirmation gate waits before it counts as declined.</summary>
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Spend cap in USD for the run (null or 0 = none). Checked whenever a step is about to start; each step also
    /// gets the remaining budget as its own cap, so a running step stops after the model call that crosses it.
    /// Parallel steps start with the same remaining amount, so a run can overshoot by the cost of one call per running step.
    /// </summary>
    public decimal? MaxBudgetUsd { get; init; }
    /// <summary>Where to save the run (<c>run.json</c> and one Markdown file per step output). Null = the run is not recorded.</summary>
    public FlowRunStore? Store { get; init; }
    /// <summary>Re-run from a step: the steps before it keep their recorded output and do not run again.</summary>
    public FlowReplayPlan? Replay { get; init; }
}

/// <summary>
/// Runs a flow: a pipeline whose stages are connected by edges. A node starts as soon as every
/// edge into it is resolved and at least one was taken; nodes that don't depend on each other run
/// concurrently (each in its own git worktree when isolation is available). Each node runs in its
/// own session and receives the outputs of the nodes connected into it. Conditional edges route on
/// the node's <c>OUTCOME:</c>; loop edges send work back to an earlier node up to <c>maxLoops</c> times.
/// </summary>
public sealed class FlowRunner
{
    public const int DefaultMaxParallel = 3;
    public const int DefaultMaxTotalRuns = 25;

    /// <summary>An enabled-tools list that matches no tool: the agent can only answer.</summary>
    internal const string NoTools = "__no_tools__";

    private readonly AgentSessionCoordinator _coordinator;
    private readonly SdlcPipelineRunner _stageBuilder;
    private readonly ILogger _logger;
    private readonly ICharacterRegistry? _characters;
    private readonly IExternalAgentRunner? _externalAgents;
    private readonly ICheckpointManager? _checkpoints;

    public FlowRunner(AgentSessionCoordinator coordinator, SdlcPipelineRunner stageBuilder, ILogger logger, ICharacterRegistry? characters = null, IExternalAgentRunner? externalAgents = null, ICheckpointManager? checkpoints = null)
    {
        _checkpoints = checkpoints;
        _externalAgents = externalAgents;
        _coordinator = coordinator;
        _stageBuilder = stageBuilder;
        _logger = logger;
        _characters = characters;
    }

    /// <summary>Validate a flow against the character registry (and an optional cast).</summary>
    public FlowGraph Validate(SdlcPipelineDefinition pipeline, IReadOnlyDictionary<string, string>? cast = null) =>
        FlowGraph.Build(pipeline, _characters == null ? null : id => _characters.GetAsync(id).GetAwaiter().GetResult(), cast);

    private sealed class NodeRun
    {
        public required FlowNode Node { get; init; }
        public FlowNodeState State { get; set; } = FlowNodeState.Pending;
        public int Runs { get; set; }
        public FlowNodeOutput? Output { get; set; }
        public string? Outcome { get; set; }
        public string? Detail { get; set; }
        public bool LoopExhausted { get; set; }
        /// <summary>A conditional loop (e.g. "rejected") ran out of rounds on the last run.</summary>
        public bool ConditionalLoopExhausted { get; set; }
        public bool BlockedByFailure { get; set; }
        public List<FlowNodeOutput> Feedback { get; } = new();
        public TimeSpan Duration { get; set; }
        public string? CharacterId { get; set; }
        /// <summary>Usage of every finished run of this node, loop rounds included.</summary>
        public List<FlowStepRunUsage> Usage { get; } = new();
        /// <summary>Usage collector of the run in progress.</summary>
        public StepUsage? Current { get; set; }
        /// <summary>The last run was stopped by its budget cap.</summary>
        public bool BudgetStopped { get; set; }
    }


    /// <summary>Collects the usage of one step run; filled on the node's task, read after the task completed.</summary>
    private sealed class StepUsage
    {
        private readonly object _gate = new();
        private TokenUsage _total = new();
        public string? Model { get; init; }
        public string? Provider { get; init; }
        public bool BudgetStopped { get; private set; }
        /// <summary>The prompt the step received, and the steps whose output is in it.</summary>
        public string Prompt { get; set; } = string.Empty;
        public List<string> InputFrom { get; init; } = new();
        public List<FlowToolCallRecord> ToolCalls { get; } = new();
        public List<FlowDiffRecord> Diffs { get; } = new();

        public void Add(AgentResponse response)
        {
            lock (_gate)
            {
                var u = response.TotalUsage;
                _total = new TokenUsage
                {
                    PromptTokens = _total.PromptTokens + u.PromptTokens,
                    CompletionTokens = _total.CompletionTokens + u.CompletionTokens,
                    CacheReadTokens = _total.CacheReadTokens + u.CacheReadTokens,
                    CacheCreationTokens = _total.CacheCreationTokens + u.CacheCreationTokens
                };
                if (response.StopReason == "budget_exceeded") BudgetStopped = true;
            }
        }

        public TokenUsage Total { get { lock (_gate) return _total; } }
    }

    private static decimal SpentOf(Dictionary<string, NodeRun> runs) =>
        runs.Values.Sum(r => r.Usage.Sum(u => u.CostUsd));

    private static FlowUsageTotals TotalsOf(Dictionary<string, NodeRun> runs) =>
        FlowUsageTotals.Sum(runs.Values.SelectMany(r => r.Usage));

    /// <param name="Skipped">The reviewer skipped the step at its gate: it did not run and does not block what follows.</param>
    private sealed record NodeResult(string Text, string? Failure, bool Cancelled, bool Declined, string? Outcome, TimeSpan Duration, bool Skipped = false);

    private enum GateChoice { Approve, Skip, Stop }
    private sealed record GateAnswer(GateChoice Choice, string? Note = null);

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        FlowRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new FlowRunOptions();
        var recorder = options.Store == null ? null : new FlowRunRecorder(options.Store, pipeline, task, options.Model, options.Replay, _logger);
        await foreach (var evt in RunCoreAsync(pipeline, task, sessionId, workingDirectory, options, recorder, cancellationToken).ConfigureAwait(false))
        {
            recorder?.Observe(evt);
            yield return evt;
        }
    }

    private async IAsyncEnumerable<AgentEvent> RunCoreAsync(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        FlowRunOptions options,
        FlowRunRecorder? recorder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var graph = Validate(pipeline, options.Cast);
        if (!graph.IsValid)
        {
            var errors = string.Join(" ", graph.Problems.Where(p => p.IsError).Select(p => p.Message));
            yield return new StatusUpdateEvent("Flow invalid", errors);
            yield return new FlowFinishedEvent(new FlowRunResult(FlowRunStatus.Failed, "The flow is invalid: " + errors, Array.Empty<FlowNodeRunSummary>()));
            yield break;
        }

        // Nodes run on a linked token: when the caller stops enumerating (or cancels), the finally block
        // below cancels the nodes still running and waits for them, so no work is left behind.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var maxParallel = Math.Max(1, options.MaxParallel ?? DefaultMaxParallel);
        var isolated = graph.NodesWithParallelPeers();
        var runs = graph.Nodes.ToDictionary(n => n.Id, n => new NodeRun
        {
            Node = n,
            CharacterId = FlowGraph.EffectiveCharacter(n.Stage, options.Cast)
        }, StringComparer.OrdinalIgnoreCase);
        var loopCounts = new Dictionary<int, int>();
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var running = new Dictionary<string, Task<NodeResult>>(StringComparer.OrdinalIgnoreCase);
        var totalRuns = 0;
        string? stopReason = null;
        var budgetHit = false;
        var budget = options.MaxBudgetUsd is > 0 ? options.MaxBudgetUsd : null;
        var finishOrder = new List<FlowStepRunUsage>();

        _logger.LogInformation("Starting flow {Flow} ({Nodes} nodes, max {Parallel} in parallel): {Waves}",
            pipeline.Name, graph.Nodes.Count, maxParallel, graph.DescribeWaves());
        yield return new FlowStartedEvent(pipeline.Name, graph.Nodes.Select(n => n.Id).ToList(), graph.Waves);
        var reusedCount = 0;
        if (options.Replay is { } replay)
        {
            foreach (var r in runs.Values)
            {
                if (replay.Reused.TryGetValue(r.Node.Id, out var reused))
                {
                    r.State = FlowNodeState.Done;
                    r.Runs = 1;
                    r.Outcome = reused.Outcome;
                    r.Detail = "reused";
                    r.Output = new FlowNodeOutput(r.Node.Id, reused.CharacterId ?? r.CharacterId, reused.Text, reused.Outcome, 1);
                    reusedCount++;
                    recorder?.AddStep(new FlowStepRecord
                    {
                        NodeId = r.Node.Id, Round = 1, CharacterId = r.Output.CharacterId, State = FlowNodeState.Done,
                        Outcome = reused.Outcome, Detail = "reused", Reused = true
                    }, reused.Text);
                }
                else if (replay.NotRun.Contains(r.Node.Id, StringComparer.OrdinalIgnoreCase))
                {
                    r.State = FlowNodeState.Skipped;
                    r.Detail = "not run in the original run";
                }
            }
        }
        foreach (var r in runs.Values)
            yield return Status(r);

        try
        {
            while (true)
            {
                // 1. Settle: skip nodes whose paths are all closed, until nothing changes.
                foreach (var evt in Settle(graph, runs))
                    yield return evt;

                // 2. Start ready nodes.
                var ready = graph.Nodes.Select(n => runs[n.Id])
                    .Where(r => r.State == FlowNodeState.Pending && IsReady(graph, runs, r.Node.Id))
                    .ToList();
                var progressed = false;
                foreach (var r in ready)
                {
                    if (cancellationToken.IsCancellationRequested || stopReason != null) break;
                    if (running.Count >= maxParallel) break;
                    if (!r.Node.Stage.Enabled)
                    {
                        // A disabled node passes straight through: its edges count as taken, with no output.
                        r.State = FlowNodeState.Done;
                        r.Detail = "disabled";
                        r.Output = new FlowNodeOutput(r.Node.Id, r.CharacterId, "(this step is disabled)", null, r.Runs);
                        progressed = true;
                        yield return Status(r);
                        continue;
                    }
                    if (budget is { } cap && SpentOf(runs) >= cap)
                    {
                        budgetHit = true;
                        stopReason = $"Budget exceeded: {FlowUsageFormat.Cost(SpentOf(runs))} spent of the {FlowUsageFormat.Cost(cap)} cap. " +
                                     "Running steps finished their current call and no new step was started.";
                        break;
                    }
                    if (totalRuns >= options.MaxTotalRuns)
                    {
                        stopReason = $"Run limit reached ({options.MaxTotalRuns} node runs).";
                        break;
                    }
                    totalRuns++;
                    r.Runs++;
                    r.State = FlowNodeState.Running;
                    r.Detail = null;
                    r.Outcome = null;
                    var inputs = InputsOf(graph, runs, r.Node.Id);
                    foreach (var e in graph.Incoming(r.Node.Id).Where(e => IsTaken(e, runs[e.From])))
                        yield return new FlowEdgeTakenEvent(e.From, e.To, e.When, IsLoop: false);
                    yield return Status(r);
                    var feedback = r.Feedback.ToList();
                    r.Feedback.Clear();
                    running[r.Node.Id] = StartNode(r, inputs, feedback, task, sessionId, workingDirectory, options,
                        isolated.Contains(r.Node.Id), channel.Writer, runCts.Token, budget == null ? null : budget.Value - SpentOf(runs));
                }

                if (running.Count == 0)
                {
                    if (progressed) continue; // a disabled node passed through; its successors may be ready now
                    break;
                }

                // 3. Stream events until a node finishes, then handle it.
                string? finished = null;
                while (finished == null)
                {
                    while (channel.Reader.TryRead(out var evt))
                        yield return evt;
                    finished = running.FirstOrDefault(kv => kv.Value.IsCompleted).Key;
                    if (finished != null) break;
                    var anyDone = Task.WhenAny(running.Values);
                    var moreEvents = channel.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                    await Task.WhenAny(anyDone, moreEvents).ConfigureAwait(false);
                }
                while (channel.Reader.TryRead(out var evt))
                    yield return evt;

                var result = await running[finished].ConfigureAwait(false);
                running.Remove(finished);
                var before = runs[finished].Usage.Count;
                var trace = runs[finished].Current;
                var round = runs[finished].Runs;
                foreach (var evt in Complete(graph, runs, runs[finished], result, loopCounts, cancellationToken.IsCancellationRequested, budget))
                    yield return evt;
                finishOrder.AddRange(runs[finished].Usage.Skip(before));
                // "Stop flow" at a gate (or a gate nobody answered): nothing new starts; steps already running finish.
                if (result.Declined && !cancellationToken.IsCancellationRequested)
                    stopReason ??= $"Not confirmed: {finished}. The flow was stopped at its gate.";
                if (recorder != null && trace != null)
                    recorder.AddStep(StepRecord(runs[finished], trace, result, round, runs[finished].Usage.Skip(before).FirstOrDefault()), result.Text);
                if (runs[finished].BudgetStopped && !budgetHit)
                {
                    budgetHit = true;
                    stopReason ??= $"Budget exceeded: '{finished}' hit the {FlowUsageFormat.Cost(budget ?? 0)} cap ({FlowUsageFormat.Cost(SpentOf(runs))} spent). " +
                                   "No new step was started.";
                }
            }
        }
        finally
        {
            if (running.Count > 0)
            {
                runCts.Cancel();
                try { await Task.WhenAll(running.Values).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Flow nodes ended while the flow was being abandoned"); }
            }
        }

        // Anything still pending did not run.
        var cancelled = cancellationToken.IsCancellationRequested;
        foreach (var r in runs.Values.Where(r => r.State == FlowNodeState.Pending))
        {
            r.State = FlowNodeState.Skipped;
            r.Detail = cancelled ? "cancelled" : budgetHit ? "budget exceeded" : stopReason ?? "not reached";
            yield return Status(r);
        }

        var summaries = graph.Nodes.Select(n => runs[n.Id]).Select(r => new FlowNodeRunSummary(
            r.Node.Id, r.CharacterId, r.State, r.Runs, r.Outcome, r.Duration, r.Output?.Text, r.Detail)).ToList();
        var failedNodes = summaries.Where(s => s.State == FlowNodeState.Failed).Select(s => s.NodeId).ToList();
        var exhausted = runs.Values.Where(r => r.ConditionalLoopExhausted && !graph.Outgoing(r.Node.Id).Any(e => !e.IsLoop && IsTaken(e, r))).ToList();
        var declined = runs.Values.Where(r => r.Detail == "not confirmed").Select(r => r.Node.Id).ToList();
        var stoppedNodes = runs.Values.Where(r => r.Detail == "cancelled" && r.Runs > 0).Select(r => r.Node.Id).ToList();

        FlowRunResult final;
        if (cancelled)
            final = new FlowRunResult(FlowRunStatus.Cancelled, "The flow was cancelled.", summaries);
        else if (budgetHit)
            final = new FlowRunResult(FlowRunStatus.BudgetExceeded, stopReason ?? "Budget exceeded.", summaries);
        else if (failedNodes.Count > 0)
            final = new FlowRunResult(FlowRunStatus.Failed, $"Failed: {string.Join(", ", failedNodes)}.", summaries);
        else if (stopReason != null)
            final = new FlowRunResult(FlowRunStatus.Stopped, stopReason, summaries);
        else if (stoppedNodes.Count > 0)
            final = new FlowRunResult(FlowRunStatus.Stopped, $"Stopped before finishing: {string.Join(", ", stoppedNodes)}.", summaries);
        else if (declined.Count > 0)
            final = new FlowRunResult(FlowRunStatus.Stopped, $"Not confirmed: {string.Join(", ", declined)}.", summaries);
        else if (exhausted.Count > 0)
            final = new FlowRunResult(FlowRunStatus.Stopped,
                $"Loop limit reached at {string.Join(", ", exhausted.Select(r => r.Node.Id))} (last outcome: {string.Join(", ", exhausted.Select(r => r.Outcome))}).", summaries);
        else
            final = new FlowRunResult(FlowRunStatus.Succeeded,
                $"Done: {summaries.Count(s => s.State == FlowNodeState.Done)} of {summaries.Count} steps ran" + (reusedCount > 0 ? $" ({reusedCount} reused)." : "."), summaries);

        final = final with { StepRuns = finishOrder, BudgetUsd = budget };
        if (recorder != null) final = final with { RunId = recorder.Save(final) };
        _logger.LogInformation("Flow {Flow} finished: {Status} — {Message} ({Cost} spent)", pipeline.Name, final.Status, final.Message, FlowUsageFormat.Cost(final.Usage.CostUsd));
        yield return new FlowFinishedEvent(final);
    }

    // ---------------------------------------------------------------- scheduling rules

    private static FlowNodeStatusEvent Status(NodeRun r) =>
        new(r.Node.Id, r.State, r.Runs, r.Detail, r.Outcome, r.CharacterId);

    /// <summary>A forward edge is taken when its source is done and the condition matches the source's outcome.</summary>
    private static bool IsTaken(FlowGraphEdge e, NodeRun source)
    {
        if (source.State != FlowNodeState.Done) return false;
        if (e.When == null) return true;
        if (string.Equals(e.When, FlowGraph.LoopExhausted, StringComparison.OrdinalIgnoreCase)) return source.LoopExhausted;
        return string.Equals(e.When, source.Outcome, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResolved(NodeRun source) =>
        source.State is FlowNodeState.Done or FlowNodeState.Failed or FlowNodeState.Skipped;

    private static bool IsReady(FlowGraph graph, Dictionary<string, NodeRun> runs, string nodeId)
    {
        var incoming = graph.Incoming(nodeId).ToList();
        if (incoming.Count == 0) return true;
        return incoming.All(e => IsResolved(runs[e.From]))
               && !incoming.Any(e => IsBlocking(runs[e.From]))
               && incoming.Any(e => IsTaken(e, runs[e.From]));
    }

    /// <summary>A failed node (or one skipped because of a failure upstream) blocks everything after it.</summary>
    private static bool IsBlocking(NodeRun source) =>
        source.State == FlowNodeState.Failed || (source.State == FlowNodeState.Skipped && source.BlockedByFailure);

    /// <summary>Skip pending nodes whose incoming edges are all resolved and none was taken (dead-path elimination).</summary>
    private static IEnumerable<AgentEvent> Settle(FlowGraph graph, Dictionary<string, NodeRun> runs)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var node in graph.Nodes)
            {
                var r = runs[node.Id];
                if (r.State != FlowNodeState.Pending) continue;
                var incoming = graph.Incoming(node.Id).ToList();
                if (incoming.Count == 0) continue;
                if (!incoming.All(e => IsResolved(runs[e.From]))) continue;
                var blockers = incoming.Select(e => runs[e.From]).Where(IsBlocking).ToList();
                if (blockers.Count == 0 && incoming.Any(e => IsTaken(e, runs[e.From]))) continue;

                r.State = FlowNodeState.Skipped;
                r.BlockedByFailure = blockers.Count > 0;
                r.Detail = blockers.Count > 0
                    ? $"blocked by {string.Join(", ", blockers.Select(b => b.Node.Id))}"
                    : "path not chosen";
                changed = true;
                yield return Status(r);
            }
        } while (changed);
    }

    private static List<FlowNodeOutput> InputsOf(FlowGraph graph, Dictionary<string, NodeRun> runs, string nodeId) =>
        graph.Incoming(nodeId)
            .Where(e => IsTaken(e, runs[e.From]) && runs[e.From].Output != null)
            .Select(e => runs[e.From].Output!)
            .GroupBy(o => o.NodeId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

    /// <summary>Record a finished node and apply its loop edges.</summary>
    private IEnumerable<AgentEvent> Complete(FlowGraph graph, Dictionary<string, NodeRun> runs, NodeRun r, NodeResult result, Dictionary<int, int> loopCounts, bool flowCancelled, decimal? budget)
    {
        r.Duration += result.Duration;
        r.BudgetStopped = r.Current?.BudgetStopped ?? false;
        if (RecordUsage(r, result.Duration) is { } stepUsage)
            yield return new FlowUsageEvent(r.Node.Id, stepUsage, FlowUsageTotals.Sum(r.Usage), TotalsOf(runs), budget);
        if (result.Declined)
        {
            r.State = FlowNodeState.Skipped;
            r.Detail = "not confirmed";
            r.BlockedByFailure = true;
            yield return Status(r);
            yield break;
        }
        if (result.Skipped)
        {
            // Skipped on purpose: not a failure, so steps that have another way in still run.
            r.State = FlowNodeState.Skipped;
            r.Detail = "skipped by reviewer";
            r.BlockedByFailure = false;
            yield return Status(r);
            yield break;
        }
        if (result.Cancelled)
        {
            r.State = FlowNodeState.Skipped;
            r.Detail = "cancelled";
            // A step that stopped on its own (the flow itself was not cancelled) did not do its work:
            // what depends on it must not run as if it had.
            r.BlockedByFailure = !flowCancelled;
            yield return Status(r);
            yield break;
        }
        if (result.Failure != null)
        {
            r.State = FlowNodeState.Failed;
            r.Detail = result.Failure;
            r.Output = new FlowNodeOutput(r.Node.Id, r.CharacterId, result.Text, null, r.Runs);
            yield return Status(r);
            yield break;
        }

        r.State = FlowNodeState.Done;
        r.Outcome = result.Outcome;
        r.LoopExhausted = false;
        r.ConditionalLoopExhausted = false;
        r.Output = new FlowNodeOutput(r.Node.Id, r.CharacterId, result.Text, result.Outcome, r.Runs);
        yield return Status(r);

        // Loop edges: the first matching one with loops left sends work back.
        foreach (var loop in graph.Outgoing(r.Node.Id).Where(e => e.IsLoop))
        {
            if (loop.When != null && !string.Equals(loop.When, r.Outcome, StringComparison.OrdinalIgnoreCase)) continue;
            loopCounts.TryGetValue(loop.Index, out var used);
            if (used >= loop.MaxLoops)
            {
                r.LoopExhausted = true;
                if (loop.When != null) r.ConditionalLoopExhausted = true;
                yield return new StatusUpdateEvent("Loop limit", $"{loop.From} → {loop.To}: used all {loop.MaxLoops} loop(s).");
                continue;
            }
            loopCounts[loop.Index] = used + 1;
            _logger.LogInformation("Flow loop {From} → {To} ({When}), round {Round}/{Max}", loop.From, loop.To, loop.When, used + 1, loop.MaxLoops);
            yield return new FlowEdgeTakenEvent(loop.From, loop.To, loop.When, IsLoop: true, LoopCount: used + 1);

            var target = runs[loop.To];
            target.Feedback.Add(r.Output!);
            // The steps between the target and this node run again, and so does everything after the
            // target: a branch that was skipped (or taken) in this round must be decided again next round.
            var again = graph.NodesBetween(loop.To, loop.From);
            again.UnionWith(graph.Descendants(loop.To));
            foreach (var node in graph.Nodes.Where(n => again.Contains(n.Id)))
            {
                var reset = runs[node.Id];
                if (reset.State == FlowNodeState.Running) continue; // a parallel branch still at work keeps going
                reset.State = FlowNodeState.Pending;
                reset.Outcome = null;
                reset.Output = null;
                reset.BlockedByFailure = false;
                reset.LoopExhausted = false;
                reset.ConditionalLoopExhausted = false;
                reset.Detail = node.Id.Equals(loop.To, StringComparison.OrdinalIgnoreCase) ? $"sent back by {loop.From} (round {used + 1}/{loop.MaxLoops})" : "waiting for the next round";
                yield return Status(reset);
            }
            break;
        }
    }

    private static FlowStepRecord StepRecord(NodeRun r, StepUsage trace, NodeResult result, int round, FlowStepRunUsage? usage) => new()
    {
        NodeId = r.Node.Id,
        Round = round,
        CharacterId = r.CharacterId,
        Model = trace.Model,
        Provider = trace.Provider,
        State = result.Declined || result.Cancelled || result.Skipped ? FlowNodeState.Skipped : result.Failure != null ? FlowNodeState.Failed : FlowNodeState.Done,
        Outcome = result.Outcome,
        Detail = result.Declined ? "not confirmed" : result.Skipped ? "skipped by reviewer" : result.Cancelled ? "cancelled" : result.Failure,
        Prompt = trace.Prompt,
        InputFrom = trace.InputFrom,
        ToolCalls = trace.ToolCalls,
        Diffs = trace.Diffs,
        Usage = usage,
        Duration = result.Duration
    };

    /// <summary>Turns the usage a finished run collected into a row (null when the model was never called).</summary>
    private static FlowStepRunUsage? RecordUsage(NodeRun r, TimeSpan duration)
    {
        var current = r.Current;
        r.Current = null;
        if (current == null) return null;
        var u = current.Total;
        if (u.PromptTokens + u.CompletionTokens + u.CacheReadTokens + u.CacheCreationTokens == 0) return null;
        var row = new FlowStepRunUsage(r.Node.Id, r.Runs, current.Model, current.Provider,
            u.PromptTokens, u.CompletionTokens, u.CacheReadTokens, u.CacheCreationTokens,
            AiCodeAgent.Core.Usage.ModelPricing.Cost(current.Model, u), AiCodeAgent.Core.Usage.ModelPricing.Lookup(current.Model) is null, duration);
        r.Usage.Add(row);
        return row;
    }

    // ---------------------------------------------------------------- running one node

    private Task<NodeResult> StartNode(
        NodeRun r,
        IReadOnlyList<FlowNodeOutput> inputs,
        IReadOnlyList<FlowNodeOutput> feedback,
        string task,
        string sessionId,
        string workingDirectory,
        FlowRunOptions options,
        bool isolate,
        ChannelWriter<AgentEvent> events,
        CancellationToken cancellationToken,
        decimal? remainingBudget = null)
    {
        var node = r.Node;
        var iteration = r.Runs;
        if (node.Stage.IsHuman)
            return StartHumanNode(r, inputs, feedback, task, options, events, cancellationToken);
        // An 'acp' step, or a step whose character has runtime: acp, is run by an external ACP agent.
        ExternalAgentTarget? external;
        try { external = _stageBuilder.ResolveExternalTarget(node.Stage, options.Cast); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow node {Node} could not start", node.Id);
            return Task.FromResult(new NodeResult(string.Empty, ex.Message, false, false, null, TimeSpan.Zero));
        }
        if (external != null)
            return StartAcpNode(r, external, inputs, feedback, task, sessionId, workingDirectory, options, events, cancellationToken);
        // Build the step (character resolution, agent registration) here on the scheduler, not inside
        // the task: several nodes start at once and registration must not race.
        SessionStep step;
        var prompt = string.Empty;
        try
        {
            prompt = FlowPrompt.Build(node.Stage.PromptTemplate, task, inputs, feedback, node.Outcomes);
            step = _stageBuilder.BuildStageStep(node.Stage, prompt, workingDirectory, options.Model, options.MaxIterationsPerNode, options.Cast, agentId: node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow node {Node} could not start", node.Id);
            return Task.FromResult(new NodeResult(string.Empty, ex.Message, false, false, null, TimeSpan.Zero));
        }
        if (options.PermissionMode is { } mode)
            step = step with { Options = step.Options with { PermissionMode = mode } };
        // The step may spend at most what is left of the flow's budget: the orchestrator stops it after the call that crosses it.
        if (remainingBudget is { } left)
        {
            var own = step.Options.MaxBudgetUsd is > 0 ? step.Options.MaxBudgetUsd.Value : left;
            step = step with { Options = step.Options with { MaxBudgetUsd = Math.Min(own, left) } };
        }
        var usage = new StepUsage
        {
            Model = step.Options.Model,
            Provider = step.Options.Provider,
            Prompt = prompt,
            InputFrom = inputs.Select(i => i.NodeId).Concat(feedback.Select(f => f.NodeId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        r.Current = usage;
        var nodeSession = $"{sessionId}/{node.Id}#{iteration}";

        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            string text = string.Empty;
            string? failure = null;
            try
            {

                if (node.Stage.RequireConfirmation)
                {
                    var gate = await ConfirmAsync(step, events, options.ConfirmationTimeout, cancellationToken).ConfigureAwait(false);
                    if (gate.Choice == GateChoice.Stop)
                        return new NodeResult(string.Empty, null, cancellationToken.IsCancellationRequested, Declined: !cancellationToken.IsCancellationRequested, null, clock.Elapsed);
                    if (gate.Choice == GateChoice.Skip)
                        return new NodeResult(string.Empty, null, false, false, null, clock.Elapsed, Skipped: true);
                    if (FlowReviewerNote.Block(gate.Note) != null)
                    {
                        // "Approve with note": the note goes to the agent with the prompt (and into the saved run).
                        step = step with { Prompt = FlowReviewerNote.Append(step.Prompt, gate.Note) };
                        usage.Prompt = step.Prompt;
                    }
                }

                var finishedOnce = false;
                var stoppedByAgent = false;
                // Read the whole stream (no early return): the worktree is merged or kept at its end.
                await foreach (var evt in _coordinator.RunIsolatedStepAsync(step, nodeSession, isolate, cancellationToken).ConfigureAwait(false))
                {
                    await events.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                    failure ??= AgentSessionCoordinator.DetectStepFailure(evt);
                    var inner = evt is AgentTaggedEvent t ? t.Inner : evt;
                    if (inner is ToolCallEndEvent toolEnd)
                        usage.ToolCalls.Add(new FlowToolCallRecord(toolEnd.Call.Name, ToolSummary(toolEnd.Call), toolEnd.Result.IsError, toolEnd.Duration.TotalMilliseconds));
                    else if (inner is DiffProducedEvent produced)
                        usage.Diffs.Add(new FlowDiffRecord(produced.Diff.FilePath, produced.Diff.DiffText));
                    if (inner is AgentFinishedEvent fin)
                    {
                        text = fin.Response.Content;
                        finishedOnce = true;
                        usage.Add(fin.Response);
                        if (fin.Response.StopReason == "budget_exceeded") failure ??= "budget exceeded";
                        else stoppedByAgent |= fin.Response.WasCancelled;
                    }
                    // The step's changes could not be merged back: the steps after it would work without them.
                    else if (inner is StatusUpdateEvent { Status: AgentSessionCoordinator.WorktreeNeedsAttention } merge)
                        failure ??= $"its changes could not be merged back: {merge.Detail}";
                }
                if (cancellationToken.IsCancellationRequested || stoppedByAgent)
                    return new NodeResult(text, null, true, false, null, clock.Elapsed);
                if (failure != null)
                    return new NodeResult(text, failure, false, false, null, clock.Elapsed);
                if (!finishedOnce)
                    return new NodeResult(text, "the agent finished without an answer", false, false, null, clock.Elapsed);

                string? outcome = null;
                if (node.Outcomes.Count > 0)
                {
                    outcome = FlowPrompt.ParseOutcome(text, node.Outcomes);
                    if (outcome == null)
                    {
                        // One short follow-up on the same session asking only for the outcome line.
                        // No tools: it only has to answer, and it runs outside any worktree.
                        var reminder = step with
                        {
                            Prompt = FlowPrompt.OutcomeReminder(node.Outcomes),
                            RequireConfirmation = false,
                            Options = step.Options with { EnabledTools = new List<string> { NoTools }, MaxIterations = 2 }
                        };
                        await foreach (var evt in _coordinator.RunIsolatedStepAsync(reminder, nodeSession, isolate: false, cancellationToken).ConfigureAwait(false))
                        {
                            await events.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                            if ((evt is AgentTaggedEvent t ? t.Inner : evt) is AgentFinishedEvent fin)
                            {
                                usage.Add(fin.Response);
                                outcome = FlowPrompt.ParseOutcome(fin.Response.Content, node.Outcomes);
                            }
                        }
                        if (cancellationToken.IsCancellationRequested)
                            return new NodeResult(text, null, true, false, null, clock.Elapsed);
                        if (outcome == null)
                            return new NodeResult(text, $"no valid outcome (expected OUTCOME: {string.Join(" | ", node.Outcomes)})", false, false, null, clock.Elapsed);
                    }
                }
                return new NodeResult(text, null, false, false, outcome, clock.Elapsed);
            }
            catch (OperationCanceledException)
            {
                return new NodeResult(text, null, true, false, null, clock.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Flow node {Node} failed", node.Id);
                await events.WriteAsync(new AgentTaggedEvent(new AgentErrorEvent(ex), node.Id), CancellationToken.None).ConfigureAwait(false);
                return new NodeResult(text, ex.Message, false, false, null, clock.Elapsed);
            }
        }, CancellationToken.None);
    }

    /// <summary>The first argument of a tool call, shortened ("src/Program.cs"), for the history view.</summary>
    private static string? ToolSummary(ToolCall call)
    {
        var first = call.Arguments?.Values.OfType<string>().FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (first == null) return null;
        first = first.ReplaceLineEndings(" ").Trim();
        return first.Length > 80 ? first[..80] + "…" : first;
    }

    /// <summary>
    /// Asks the user whether the step may start. Answered through <see cref="ApprovalRequestEvent.Approval"/>:
    /// true = approve (the optional note is <c>Feedback.Text</c>), false = stop the flow, false with
    /// <c>Feedback.Skip</c> = skip this step only. No answer in time counts as stop.
    /// </summary>
    private static async Task<GateAnswer> ConfirmAsync(SessionStep step, ChannelWriter<AgentEvent> events, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = new ApprovalFeedback();
        var gateCall = new ToolCall
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "pipeline_stage",
            Arguments = new Dictionary<string, object?>
            {
                ["stage"] = step.AgentId,
                ["agent"] = step.CharacterId ?? step.Role,
                ["prompt"] = step.Prompt.Length > 300 ? step.Prompt[..300] + "…" : step.Prompt
            }
        };
        await events.WriteAsync(new AgentTaggedEvent(new ApprovalRequestEvent(gateCall, confirmation, RiskLevel.Execute) { Feedback = feedback }, step.AgentId, step.Role), CancellationToken.None).ConfigureAwait(false);
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateCts.CancelAfter(timeout);
        await Task.WhenAny(confirmation.Task, Task.Delay(Timeout.InfiniteTimeSpan, gateCts.Token))
            .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        if (!confirmation.Task.IsCompletedSuccessfully) return new GateAnswer(GateChoice.Stop);
        if (confirmation.Task.Result) return new GateAnswer(GateChoice.Approve, feedback.Text);
        return new GateAnswer(feedback.Skip ? GateChoice.Skip : GateChoice.Stop);
    }

    /// <summary>
    /// A human step: no agent runs. The user gets the step's prompt and picks one of its outcomes (with an optional note);
    /// the choice routes the edges like an agent's OUTCOME line, and the note travels in the step's output.
    /// </summary>
    private Task<NodeResult> StartHumanNode(
        NodeRun r, IReadOnlyList<FlowNodeOutput> inputs, IReadOnlyList<FlowNodeOutput> feedback, string task,
        FlowRunOptions options, ChannelWriter<AgentEvent> events, CancellationToken cancellationToken)
    {
        var node = r.Node;
        var prompt = FlowPrompt.Build(node.Stage.PromptTemplate, task, inputs, feedback, Array.Empty<string>());
        r.Current = new StepUsage
        {
            Prompt = prompt,
            InputFrom = inputs.Select(i => i.NodeId).Concat(feedback.Select(f => f.NodeId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var choice = new TaskCompletionSource<FlowHumanChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                await events.WriteAsync(new AgentTaggedEvent(new FlowHumanRequestEvent(node.Id, prompt, node.Outcomes, choice), node.Id, "human"), CancellationToken.None).ConfigureAwait(false);
                using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gateCts.CancelAfter(options.ConfirmationTimeout);
                await Task.WhenAny(choice.Task, Task.Delay(Timeout.InfiniteTimeSpan, gateCts.Token))
                    .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return new NodeResult(string.Empty, null, true, false, null, clock.Elapsed);
                if (!choice.Task.IsCompletedSuccessfully || choice.Task.Result.Stop)
                    return new NodeResult(string.Empty, null, false, Declined: true, null, clock.Elapsed);

                var picked = choice.Task.Result;
                string? outcome = null;
                if (node.Outcomes.Count > 0)
                {
                    outcome = node.Outcomes.FirstOrDefault(o => string.Equals(o, picked.Outcome?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (outcome == null)
                        return new NodeResult(string.Empty, $"'{picked.Outcome}' is not one of the outcomes ({string.Join(" | ", node.Outcomes)})", false, false, null, clock.Elapsed);
                }
                var text = outcome == null ? "The reviewer continued the flow." : $"The reviewer chose '{outcome}'.";
                if (!string.IsNullOrWhiteSpace(picked.Note)) text += $"\nReviewer note: {picked.Note.Trim()}";
                return new NodeResult(text, null, false, false, outcome, clock.Elapsed);
            }
            catch (OperationCanceledException)
            {
                return new NodeResult(string.Empty, null, true, false, null, clock.Elapsed);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// A step run by an external Agent Client Protocol agent (E6-S5/S6). The prompt, with its <c>{input:id}</c> values filled
    /// in and the character's instructions in front, goes to the agent; its final message is the step's output. The agent's
    /// permission requests follow the step's permission mode and, where the mode says "ask", appear as approval requests
    /// like those of built-in agents. A file the agent writes gets a checkpoint first and its diff is shown afterwards.
    /// The step runs in the flow's working directory (no worktree yet).
    /// </summary>
    private Task<NodeResult> StartAcpNode(
        NodeRun r, ExternalAgentTarget target, IReadOnlyList<FlowNodeOutput> inputs, IReadOnlyList<FlowNodeOutput> feedback, string task,
        string sessionId, string workingDirectory, FlowRunOptions options, ChannelWriter<AgentEvent> events, CancellationToken cancellationToken)
    {
        var node = r.Node;
        var prompt = FlowPrompt.Build(node.Stage.PromptTemplate, task, inputs, feedback, node.Outcomes);
        if (!string.IsNullOrWhiteSpace(target.Instructions)) prompt = target.Instructions + "\n\n" + prompt;
        var usage = new StepUsage
        {
            Model = target.Command,
            Provider = "acp",
            Prompt = prompt,
            InputFrom = inputs.Select(i => i.NodeId).Concat(feedback.Select(f => f.NodeId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        r.Current = usage;
        var nodeSession = $"{sessionId}/{node.Id}#{r.Runs}";
        var mode = options.PermissionMode ?? target.PermissionMode ?? PermissionMode.Ask;

        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                if (_externalAgents == null)
                    return new NodeResult(string.Empty, "this host cannot run external ACP agents", false, false, null, clock.Elapsed);
                if (string.IsNullOrWhiteSpace(target.Command))
                    return new NodeResult(string.Empty, "no command is set for the external agent", false, false, null, clock.Elapsed);

                var request = new ExternalAgentRequest(target.Command, target.Args, workingDirectory, prompt, mode, options.ConfirmationTimeout)
                {
                    Ask = (question, ct) => AskUserAsync(node, question, events, options.ConfirmationTimeout, ct),
                    BeforeWrite = async path =>
                    {
                        if (_checkpoints == null) return;
                        try
                        {
                            var checkpoint = await _checkpoints.CreateCheckpointAsync(path, nodeSession, sessionId).ConfigureAwait(false);
                            await events.WriteAsync(new AgentTaggedEvent(new CheckpointCreatedEvent(checkpoint), node.Id, "acp"), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not create a checkpoint for {Path}", path);
                        }
                    },
                    AfterWrite = (path, before, after) =>
                    {
                        var diff = new DiffEntry
                        {
                            FilePath = path,
                            OriginalContent = before,
                            ModifiedContent = after,
                            DiffText = UnifiedDiffBuilder.Build(before, after, path),
                            AgentId = node.Id
                        };
                        usage.Diffs.Add(new FlowDiffRecord(diff.FilePath, diff.DiffText));
                        events.TryWrite(new AgentTaggedEvent(new DiffProducedEvent(diff), node.Id, "acp"));
                    }
                };
                var result = await _externalAgents.RunAsync(request,
                    line => events.TryWrite(new AgentTaggedEvent(new StatusUpdateEvent(line), node.Id, "acp")),
                    cancellationToken).ConfigureAwait(false);

                usage.ToolCalls.AddRange(Enumerable.Range(0, result.ToolCalls).Select(_ => new FlowToolCallRecord("acp-tool", string.Empty, false, 0)));
                if (result.Usage != null) usage.Add(new AgentResponse { TotalUsage = result.Usage });
                if (result.StopReason == "cancelled")
                    return new NodeResult(result.Text, null, true, false, null, clock.Elapsed);
                if (result.StopReason == "refusal")
                    return new NodeResult(result.Text, "the external agent refused the request", false, false, null, clock.Elapsed);
                if (string.IsNullOrWhiteSpace(result.Text))
                    return new NodeResult(result.Text, "the external agent finished without an answer", false, false, null, clock.Elapsed);

                string? outcome = null;
                if (node.Outcomes.Count > 0)
                {
                    outcome = FlowPrompt.ParseOutcome(result.Text, node.Outcomes);
                    if (outcome == null)
                        return new NodeResult(result.Text, $"no valid outcome (expected OUTCOME: {string.Join(" | ", node.Outcomes)})", false, false, null, clock.Elapsed);
                }
                return new NodeResult(result.Text, null, false, false, outcome, clock.Elapsed);
            }
            catch (OperationCanceledException)
            {
                return new NodeResult(string.Empty, null, true, false, null, clock.Elapsed);
            }
            catch (ExternalAgentException ex)
            {
                _logger.LogWarning(ex, "ACP step {Node} failed", node.Id);
                return new NodeResult(string.Empty, ex.Message, false, false, null, clock.Elapsed);
            }
        }, CancellationToken.None);
    }

    /// <summary>One permission request of an external agent as an approval request (the same card built-in agents use).</summary>
    private static async Task<bool> AskUserAsync(FlowNode node, ExternalPermissionQuestion question, ChannelWriter<AgentEvent> events, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new ToolCall
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "acp_" + question.Kind,
            Arguments = new Dictionary<string, object?> { ["agent"] = node.Id, ["action"] = question.Title, ["kind"] = question.Kind }
        };
        var risk = question.Kind switch
        {
            "read" or "search" or "think" => RiskLevel.Read,
            "edit" or "move" or "delete" => RiskLevel.Write,
            _ => RiskLevel.Execute
        };
        await events.WriteAsync(new AgentTaggedEvent(new ApprovalRequestEvent(call, answer, risk), node.Id, "acp"), CancellationToken.None).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await Task.WhenAny(answer.Task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token))
            .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return answer.Task.IsCompletedSuccessfully && answer.Task.Result;
    }
}
