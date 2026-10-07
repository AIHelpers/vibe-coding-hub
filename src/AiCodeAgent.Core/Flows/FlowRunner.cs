using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
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
    /// <summary>How long a confirmation gate waits before it counts as declined.</summary>
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromMinutes(10);
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

    public FlowRunner(AgentSessionCoordinator coordinator, SdlcPipelineRunner stageBuilder, ILogger logger, ICharacterRegistry? characters = null)
    {
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
    }

    private sealed record NodeResult(string Text, string? Failure, bool Cancelled, bool Declined, string? Outcome, TimeSpan Duration);

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        FlowRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new FlowRunOptions();
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

        _logger.LogInformation("Starting flow {Flow} ({Nodes} nodes, max {Parallel} in parallel): {Waves}",
            pipeline.Name, graph.Nodes.Count, maxParallel, graph.DescribeWaves());
        yield return new FlowStartedEvent(pipeline.Name, graph.Nodes.Select(n => n.Id).ToList(), graph.Waves);
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
                        isolated.Contains(r.Node.Id), channel.Writer, runCts.Token);
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
                foreach (var evt in Complete(graph, runs, runs[finished], result, loopCounts, cancellationToken.IsCancellationRequested))
                    yield return evt;
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
            r.Detail = cancelled ? "cancelled" : stopReason ?? "not reached";
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
            final = new FlowRunResult(FlowRunStatus.Succeeded, $"Done: {summaries.Count(s => s.State == FlowNodeState.Done)} of {summaries.Count} steps ran.", summaries);

        _logger.LogInformation("Flow {Flow} finished: {Status} — {Message}", pipeline.Name, final.Status, final.Message);
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
    private IEnumerable<AgentEvent> Complete(FlowGraph graph, Dictionary<string, NodeRun> runs, NodeRun r, NodeResult result, Dictionary<int, int> loopCounts, bool flowCancelled)
    {
        r.Duration += result.Duration;
        if (result.Declined)
        {
            r.State = FlowNodeState.Skipped;
            r.Detail = "not confirmed";
            r.BlockedByFailure = true;
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
        CancellationToken cancellationToken)
    {
        var node = r.Node;
        var iteration = r.Runs;
        // Build the step (character resolution, agent registration) here on the scheduler, not inside
        // the task: several nodes start at once and registration must not race.
        SessionStep step;
        try
        {
            var prompt = FlowPrompt.Build(node.Stage.PromptTemplate, task, inputs, feedback, node.Outcomes);
            step = _stageBuilder.BuildStageStep(node.Stage, prompt, workingDirectory, options.Model, options.MaxIterationsPerNode, options.Cast, agentId: node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow node {Node} could not start", node.Id);
            return Task.FromResult(new NodeResult(string.Empty, ex.Message, false, false, null, TimeSpan.Zero));
        }
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
                    var confirmed = await ConfirmAsync(step, events, options.ConfirmationTimeout, cancellationToken).ConfigureAwait(false);
                    if (!confirmed)
                        return new NodeResult(string.Empty, null, cancellationToken.IsCancellationRequested, Declined: !cancellationToken.IsCancellationRequested, null, clock.Elapsed);
                }

                var finishedOnce = false;
                var stoppedByAgent = false;
                // Read the whole stream (no early return): the worktree is merged or kept at its end.
                await foreach (var evt in _coordinator.RunIsolatedStepAsync(step, nodeSession, isolate, cancellationToken).ConfigureAwait(false))
                {
                    await events.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                    failure ??= AgentSessionCoordinator.DetectStepFailure(evt);
                    var inner = evt is AgentTaggedEvent t ? t.Inner : evt;
                    if (inner is AgentFinishedEvent fin)
                    {
                        text = fin.Response.Content;
                        finishedOnce = true;
                        stoppedByAgent |= fin.Response.WasCancelled;
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
                                outcome = FlowPrompt.ParseOutcome(fin.Response.Content, node.Outcomes);
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

    private static async Task<bool> ConfirmAsync(SessionStep step, ChannelWriter<AgentEvent> events, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        await events.WriteAsync(new AgentTaggedEvent(new ApprovalRequestEvent(gateCall, confirmation, RiskLevel.Execute), step.AgentId, step.Role), CancellationToken.None).ConfigureAwait(false);
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateCts.CancelAfter(timeout);
        await Task.WhenAny(confirmation.Task, Task.Delay(Timeout.InfiniteTimeSpan, gateCts.Token))
            .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        return confirmation.Task.IsCompletedSuccessfully && confirmation.Task.Result;
    }
}
