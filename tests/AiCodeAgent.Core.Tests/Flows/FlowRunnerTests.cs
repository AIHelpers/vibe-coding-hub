using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>
/// Orchestrator double for flows: each node (options.AgentId) answers with a scripted function of
/// (prompt, call number). Records prompts, sessions and how many nodes ran at the same time.
/// </summary>
internal sealed class ScriptedOrchestrator : IAgentOrchestrator
{
    private readonly Func<string, string, int, string?> _answer;
    private int _active;
    public int MaxConcurrent;
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(40);
    public ConcurrentQueue<(string Node, string Prompt, string Session)> Calls { get; } = new();
    public ConcurrentDictionary<string, int> CallCounts { get; } = new();
    public HashSet<string> Failing { get; } = new();
    /// <summary>Nodes whose agent stops on its own (response marked cancelled).</summary>
    public HashSet<string> StoppingOnTheirOwn { get; } = new();
    /// <summary>Runs that ended because their cancellation token fired.</summary>
    public int CancelledRuns;
    /// <summary>Nodes that wait (up to 5 s) until all of them are running: proves parallelism without timing luck.</summary>
    public HashSet<string> MeetUp { get; init; } = new();
    private int _arrived;
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="answer">(nodeId, prompt, callNumber) → answer text; null = default "&lt;node&gt; done".</param>
    public ScriptedOrchestrator(Func<string, string, int, string?>? answer = null) =>
        _answer = answer ?? ((_, _, _) => null);

    public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(string userMessage, string sessionId, AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var node = options.AgentId ?? "?";
        var call = CallCounts.AddOrUpdate(node, 1, (_, n) => n + 1);
        Calls.Enqueue((node, userMessage, sessionId));
        var now = Interlocked.Increment(ref _active);
        lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            yield return new TextDeltaEvent($"{node} working");
            if (MeetUp.Contains(node))
            {
                if (Interlocked.Increment(ref _arrived) == MeetUp.Count) _allArrived.TrySetResult();
                await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            await Task.Delay(Delay, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            if (cancellationToken.IsCancellationRequested) Interlocked.Increment(ref CancelledRuns);
        }
        if (StoppingOnTheirOwn.Contains(node))
        {
            yield return new AgentFinishedEvent(new AgentResponse { Content = "stopped", WasCancelled = true });
            yield break;
        }
        if (Failing.Contains(node))
        {
            yield return new AgentErrorEvent(new InvalidOperationException($"{node} crashed"));
            yield break;
        }
        yield return new AgentFinishedEvent(new AgentResponse { Content = _answer(node, userMessage, call) ?? $"{node} done" });
    }
}

public class FlowRunnerTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    private static (SdlcPipelineRunner Runner, AgentSessionCoordinator Coordinator) Create(ScriptedOrchestrator orchestrator)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance);
        return (runner, coordinator);
    }

    private static async Task<List<AgentEvent>> RunAsync(ScriptedOrchestrator orchestrator, SdlcPipelineDefinition flow,
        int? maxParallel = null, CancellationToken ct = default, Action<AgentEvent>? onEvent = null)
    {
        var (runner, _) = Create(orchestrator);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(flow, "add pagination", "s1", Path.GetTempPath(), cancellationToken: ct, maxParallel: maxParallel))
        {
            events.Add(e);
            onEvent?.Invoke(e);
        }
        return events;
    }

    private static FlowRunResult Result(List<AgentEvent> events) => Assert.Single(events.OfType<FlowFinishedEvent>()).Result;

    private static FlowNodeRunSummary Node(FlowRunResult r, string id) => r.Nodes.Single(n => n.NodeId == id);

    private static string PromptOf(ScriptedOrchestrator o, string node, int call = 1) =>
        o.Calls.Where(c => c.Node == node).ElementAt(call - 1).Prompt;

    private static SdlcPipelineDefinition Diamond() => new()
    {
        Name = "diamond",
        Stages = new() { Stage("design", "planner"), Stage("backend"), Stage("model"), Stage("review", "reviewer") },
        Edges = new() { Edge("design", "backend"), Edge("design", "model"), Edge("backend", "review"), Edge("model", "review") }
    };

    [Fact]
    public async Task Diamond_RunsBranchesInParallel_AndJoinGetsBothOutputs()
    {
        // backend and model wait for each other: they only finish quickly if they really run together.
        var o = new ScriptedOrchestrator { MeetUp = new HashSet<string> { "backend", "model" } };
        var events = await RunAsync(o, Diamond());

        var result = Result(events);
        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(2, o.MaxConcurrent);
        Assert.All(result.Nodes, n => Assert.Equal(FlowNodeState.Done, n.State));

        var review = PromptOf(o, "review");
        Assert.Contains("Do the review part of: add pagination", review);
        Assert.Contains("<input from=\"backend\">\nbackend done\n</input>", review);
        Assert.Contains("<input from=\"model\">\nmodel done\n</input>", review);
        Assert.DoesNotContain("from=\"design\"", review); // only nodes connected into it

        // Each node has its own session; design starts first, review last.
        Assert.Equal(4, o.Calls.Select(c => c.Session).Distinct().Count());
        Assert.Contains("s1/backend#1", o.Calls.Select(c => c.Session));
        Assert.Equal("design", o.Calls.First().Node);
        Assert.Equal("review", o.Calls.Last().Node);

        Assert.IsType<FlowStartedEvent>(events[0]);
        Assert.Contains(events.OfType<FlowEdgeTakenEvent>(), e => e.From == "model" && e.To == "review");
        Assert.Contains(events.OfType<AgentTaggedEvent>(), t => t.AgentId == "model" && t.Inner is TextDeltaEvent);
    }

    [Fact]
    public async Task MaxParallelOne_RunsOneAtATime()
    {
        var o = new ScriptedOrchestrator();
        var result = Result(await RunAsync(o, Diamond(), maxParallel: 1));
        Assert.True(result.Succeeded);
        Assert.Equal(1, o.MaxConcurrent);
    }

    [Fact]
    public async Task Failure_BlocksOnlyDownstream()
    {
        var o = new ScriptedOrchestrator();
        o.Failing.Add("model");
        var flow = Diamond() with
        {
            Stages = Diamond().Stages.Append(Stage("docs")).ToList(),
            Edges = Diamond().Edges!.Append(Edge("design", "docs")).ToList()
        };
        var result = Result(await RunAsync(o, flow));

        Assert.Equal(FlowRunStatus.Failed, result.Status);
        Assert.Equal(FlowNodeState.Failed, Node(result, "model").State);
        Assert.Contains("model crashed", Node(result, "model").Detail);
        Assert.Equal(FlowNodeState.Done, Node(result, "backend").State);
        Assert.Equal(FlowNodeState.Done, Node(result, "docs").State);
        Assert.Equal(FlowNodeState.Skipped, Node(result, "review").State);
        Assert.Equal("blocked by model", Node(result, "review").Detail);
        Assert.DoesNotContain(o.Calls, c => c.Node == "review");
    }

    [Fact]
    public async Task ReviewLoop_SendsWorkBack_WithFeedback_ThenApproves()
    {
        var o = new ScriptedOrchestrator((node, _, call) => node == "review"
            ? call == 1 ? "Missing tests.\nOUTCOME: rejected" : "All good.\nOUTCOME: approved"
            : null);
        var events = await RunAsync(o, FeatureTeam());
        var result = Result(events);

        Assert.True(result.Status == FlowRunStatus.Succeeded, result.Message + " | " + string.Join("; ", result.Nodes.Select(n => $"{n.NodeId}:{n.State}:{n.Detail}")));
        Assert.Equal(2, Node(result, "backend").Runs);
        Assert.Equal(1, Node(result, "model").Runs);   // not between backend and review: kept its output
        Assert.Equal(1, Node(result, "design").Runs);
        Assert.Equal(2, Node(result, "review").Runs);
        Assert.Equal("approved", Node(result, "review").Outcome);
        Assert.Equal(FlowNodeState.Done, Node(result, "deploy").State);

        var secondBackend = PromptOf(o, "backend", 2);
        Assert.Contains("<feedback from=\"review\" outcome=\"rejected\">\nMissing tests.", secondBackend);
        Assert.Contains("from=\"design\"", secondBackend);
        Assert.Contains("OUTCOME: <approved | rejected>", PromptOf(o, "review"));
        Assert.Contains("s1/backend#2", o.Calls.Select(c => c.Session));

        var loop = Assert.Single(events.OfType<FlowEdgeTakenEvent>(), e => e.IsLoop);
        Assert.Equal(("review", "backend", 1), (loop.From, loop.To, loop.LoopCount));
        Assert.Contains(events.OfType<FlowNodeStatusEvent>(), s => s.NodeId == "backend" && s.State == FlowNodeState.Pending && s.Detail?.Contains("sent back by review") == true);
    }

    [Fact]
    public async Task ReviewLoop_StopsAtLimit_AndSkipsDeploy()
    {
        var o = new ScriptedOrchestrator((node, _, _) => node == "review" ? "Still broken.\nOUTCOME: rejected" : null);
        var result = Result(await RunAsync(o, FeatureTeam(maxLoops: 2)));

        Assert.Equal(FlowRunStatus.Stopped, result.Status);
        Assert.Contains("Loop limit reached at review", result.Message);
        Assert.Equal(3, Node(result, "backend").Runs);
        Assert.Equal(3, Node(result, "review").Runs);
        Assert.Equal(FlowNodeState.Skipped, Node(result, "deploy").State);
        Assert.Equal("path not chosen", Node(result, "deploy").Detail);
    }

    [Fact]
    public async Task LoopExhaustedEdge_RoutesSomewhereElse()
    {
        var flow = FeatureTeam(maxLoops: 1);
        flow = flow with
        {
            Stages = flow.Stages.Append(Stage("escalate", "planner")).ToList(),
            Edges = flow.Edges!.Append(Edge("review", "escalate", FlowGraph.LoopExhausted)).ToList()
        };
        var o = new ScriptedOrchestrator((node, _, _) => node == "review" ? "OUTCOME: rejected" : null);
        var result = Result(await RunAsync(o, flow));

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(FlowNodeState.Done, Node(result, "escalate").State);
        Assert.Equal(FlowNodeState.Skipped, Node(result, "deploy").State);
        Assert.Contains("OUTCOME: rejected", PromptOf(o, "escalate"));
    }

    [Fact]
    public async Task Branching_SkipsPathNotChosen_AndJoinStillRuns()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "triage",
            Stages = new() { Stage("triage", "planner", null, "bug", "feature"), Stage("fix"), Stage("build"), Stage("report", "reviewer") },
            Edges = new() { Edge("triage", "fix", "bug"), Edge("triage", "build", "feature"), Edge("fix", "report"), Edge("build", "report") }
        };
        var o = new ScriptedOrchestrator((node, _, _) => node == "triage" ? "It's a bug.\nOUTCOME: bug" : null);
        var result = Result(await RunAsync(o, flow));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(FlowNodeState.Done, Node(result, "fix").State);
        Assert.Equal(FlowNodeState.Skipped, Node(result, "build").State);
        Assert.Equal(FlowNodeState.Done, Node(result, "report").State);
        Assert.Contains("from=\"fix\"", PromptOf(o, "report"));
        Assert.DoesNotContain("from=\"build\"", PromptOf(o, "report"));
    }

    [Fact]
    public async Task MissingOutcome_AsksOnce_ThenFailsIfStillMissing()
    {
        // First answer has no outcome; the reminder answers with one.
        var o = new ScriptedOrchestrator((node, prompt, _) => node == "review"
            ? prompt.StartsWith("You did not end", StringComparison.Ordinal) ? "OUTCOME: approved" : "Looks fine."
            : null);
        var result = Result(await RunAsync(o, FeatureTeam()));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, o.CallCounts["review"]);

        var stubborn = new ScriptedOrchestrator((node, _, _) => node == "review" ? "Looks fine." : null);
        var failed = Result(await RunAsync(stubborn, FeatureTeam()));
        Assert.Equal(FlowRunStatus.Failed, failed.Status);
        Assert.Contains("no valid outcome", Node(failed, "review").Detail);
        Assert.Equal(FlowNodeState.Skipped, Node(failed, "deploy").State);
    }

    [Fact]
    public async Task Confirmation_Declined_StopsThatBranch()
    {
        var flow = FeatureTeam();
        flow.Stages[4] = flow.Stages[4] with { RequireConfirmation = true };
        var o = new ScriptedOrchestrator((node, _, _) => node == "review" ? "OUTCOME: approved" : null);
        var events = await RunAsync(o, flow, onEvent: e =>
        {
            if (e is AgentTaggedEvent { Inner: ApprovalRequestEvent approval }) approval.Approval.SetResult(false);
        });
        var result = Result(events);

        Assert.Equal(FlowRunStatus.Stopped, result.Status);
        Assert.Equal("not confirmed", Node(result, "deploy").Detail);
        Assert.DoesNotContain(o.Calls, c => c.Node == "deploy");
    }

    [Fact]
    public async Task Confirmation_Approved_RunsTheNode()
    {
        var flow = FeatureTeam();
        flow.Stages[4] = flow.Stages[4] with { RequireConfirmation = true };
        var o = new ScriptedOrchestrator((node, _, _) => node == "review" ? "OUTCOME: approved" : null);
        var result = Result(await RunAsync(o, flow, onEvent: e =>
        {
            if (e is AgentTaggedEvent { Inner: ApprovalRequestEvent approval }) approval.Approval.SetResult(true);
        }));
        Assert.True(result.Succeeded);
        Assert.Contains(o.Calls, c => c.Node == "deploy");
    }

    [Fact]
    public async Task Cancel_StopsRunningAndPendingNodes()
    {
        using var cts = new CancellationTokenSource();
        var o = new ScriptedOrchestrator { Delay = TimeSpan.FromSeconds(5) };
        var events = await RunAsync(o, Diamond(), ct: cts.Token, onEvent: e =>
        {
            if (e is FlowNodeStatusEvent { NodeId: "design", State: FlowNodeState.Running }) cts.CancelAfter(50);
        });
        var result = Result(events);
        Assert.Equal(FlowRunStatus.Cancelled, result.Status);
        Assert.All(result.Nodes, n => Assert.Equal(FlowNodeState.Skipped, n.State));
    }

    [Fact]
    public async Task RunLimit_StopsRunawayLoops()
    {
        var flow = FeatureTeam(maxLoops: 50);
        var o = new ScriptedOrchestrator((node, _, _) => node == "review" ? "OUTCOME: rejected" : null) { Delay = TimeSpan.Zero };
        var (runner, coordinator) = Create(o);
        var flowRunner = new FlowRunner(coordinator, runner, NullLogger.Instance);
        var events = new List<AgentEvent>();
        await foreach (var e in flowRunner.RunAsync(flow, "t", "s", Path.GetTempPath(), new FlowRunOptions { MaxTotalRuns = 7 }))
            events.Add(e);
        var result = Result(events);
        Assert.Equal(FlowRunStatus.Stopped, result.Status);
        Assert.Contains("Run limit reached (7", result.Message);
        Assert.Equal(7, o.Calls.Count);
    }

    [Fact]
    public async Task DisabledNode_PassesThrough()
    {
        var flow = Diamond();
        flow.Stages[1] = flow.Stages[1] with { Enabled = false };
        var o = new ScriptedOrchestrator();
        var result = Result(await RunAsync(o, flow));
        Assert.True(result.Succeeded);
        Assert.Equal("disabled", Node(result, "backend").Detail);
        Assert.DoesNotContain(o.Calls, c => c.Node == "backend");
        Assert.Contains(o.Calls, c => c.Node == "review");
    }

    [Fact]
    public async Task InvalidFlow_DoesNotRun()
    {
        var o = new ScriptedOrchestrator();
        var flow = Diamond() with { Edges = new() { Edge("design", "ghost") } };
        var result = Result(await RunAsync(o, flow));
        Assert.Equal(FlowRunStatus.Failed, result.Status);
        Assert.Contains("unknown node 'ghost'", result.Message);
        Assert.Empty(o.Calls);
    }

    [Fact]
    public async Task LinearPipeline_StillUsesSharedHistory()
    {
        var o = new ScriptedOrchestrator();
        var linear = new SdlcPipelineDefinition { Name = "l", Stages = new() { new() { Role = "planner", Name = "A", PromptTemplate = "a {task}" }, new() { Role = "implementer", Name = "B", PromptTemplate = "b" } } };
        var events = await RunAsync(o, linear);
        Assert.Empty(events.OfType<FlowFinishedEvent>());
        Assert.Equal(new[] { "s1", "s1" }, o.Calls.Select(c => c.Session));
    }

    [Fact]
    public async Task Loop_DecidesBranchesAgain_InTheNextRound()
    {
        // impl → test; test → perf only when "pass"; test → review; review → impl when "rejected" (loop);
        // review → deploy when "approved". Round 1: test fails (perf skipped), review rejects.
        // Round 2: test passes, so perf must run now; review approves.
        var flow = new SdlcPipelineDefinition
        {
            Name = "rounds",
            Stages = new() { Stage("impl"), Stage("test", "tester", null, "pass", "fail"), Stage("perf"), Stage("review", "reviewer", null, "approved", "rejected"), Stage("deploy", "deployer") },
            Edges = new()
            {
                Edge("impl", "test"), Edge("test", "perf", "pass"), Edge("test", "review"),
                Edge("review", "impl", "rejected", 2), Edge("review", "deploy", "approved")
            }
        };
        var o = new ScriptedOrchestrator((node, _, call) => node switch
        {
            "test" => call == 1 ? "OUTCOME: fail" : "OUTCOME: pass",
            "review" => call == 1 ? "OUTCOME: rejected" : "OUTCOME: approved",
            _ => null
        });

        var result = Result(await RunAsync(o, flow));

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(FlowNodeState.Done, Node(result, "perf").State);
        Assert.Equal(1, Node(result, "perf").Runs);
        Assert.Equal(FlowNodeState.Done, Node(result, "deploy").State);
        Assert.Equal(2, Node(result, "test").Runs);
    }

    [Fact]
    public async Task MergeConflict_FailsTheStep_AndBlocksWhatDependsOnIt()
    {
        var isolation = new RecordingIsolation { ConflictFor = "backend" };
        var o = new ScriptedOrchestrator();
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets, workspaceIsolation: isolation);
        var runner = new SdlcPipelineRunner(coordinator, Presets, o, NullLogger<SdlcPipelineRunner>.Instance);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(Diamond(), "t", "s", Path.GetTempPath()))
            events.Add(e);

        var result = Result(events);
        Assert.Equal(FlowRunStatus.Failed, result.Status);
        Assert.Equal(FlowNodeState.Failed, Node(result, "backend").State);
        Assert.Contains("could not be merged back", Node(result, "backend").Detail);
        Assert.Equal(FlowNodeState.Done, Node(result, "model").State);
        Assert.Equal(FlowNodeState.Skipped, Node(result, "review").State);
        Assert.Contains("blocked by backend", Node(result, "review").Detail);
    }

    [Fact]
    public async Task StepThatStopsOnItsOwn_BlocksWhatDependsOnIt_AndStopsTheFlow()
    {
        var o = new ScriptedOrchestrator();
        o.StoppingOnTheirOwn.Add("backend");

        var result = Result(await RunAsync(o, Diamond()));

        Assert.Equal(FlowRunStatus.Stopped, result.Status);
        Assert.Contains("backend", result.Message);
        Assert.Equal(FlowNodeState.Skipped, Node(result, "backend").State);
        Assert.Contains("blocked by backend", Node(result, "review").Detail);
        Assert.DoesNotContain(o.Calls, c => c.Node == "review");
    }

    [Fact]
    public async Task AbandoningTheRun_CancelsStepsStillRunning()
    {
        var o = new ScriptedOrchestrator { Delay = TimeSpan.FromSeconds(20) };
        var (runner, _) = Create(o);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var e in runner.RunAsync(Diamond(), "t", "s1", Path.GetTempPath()))
        {
            if (e is AgentTaggedEvent { AgentId: "design", Inner: TextDeltaEvent })
                break; // design is working; the caller walks away (e.g. the panel was closed)
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Equal(1, o.CancelledRuns);
    }

    [Fact]
    public void DisabledStepWithConditionalArrows_IsInvalid()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "d",
            Stages = new() { Stage("impl"), Stage("review", "reviewer", null, "approved", "rejected") with { Enabled = false }, Stage("deploy", "deployer") },
            Edges = new() { Edge("impl", "review"), Edge("review", "deploy", "approved") }
        };
        var graph = FlowGraph.Build(flow);
        Assert.False(graph.IsValid);
        Assert.Contains(graph.Problems, p => p.IsError && p.NodeId == "review" && p.Message.Contains("disabled"));
    }

    private sealed class RecordingIsolation : IWorkspaceIsolation
    {
        public ConcurrentBag<string> Acquired { get; } = new();
        public string? ConflictFor { get; init; }
        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
        {
            Acquired.Add(agentId);
            return Task.FromResult<WorkspaceLease?>(new WorkspaceLease(agentId, "agent/" + agentId, baseDirectory, baseDirectory, baseDirectory));
        }

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default) =>
            Task.FromResult(lease.AgentId == ConflictFor
                ? new WorkspaceMergeResult(WorkspaceMergeOutcome.Conflict, lease.Branch, "Merge conflicts in: api.cs.", new[] { "api.cs" }, lease.RootPath)
                : new WorkspaceMergeResult(WorkspaceMergeOutcome.Merged, lease.Branch, "merged", Array.Empty<string>()));
    }

    [Fact]
    public async Task OnlyNodesThatCanRunTogether_GetWorktrees()
    {
        var isolation = new RecordingIsolation();
        var o = new ScriptedOrchestrator();
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets, workspaceIsolation: isolation);
        var runner = new SdlcPipelineRunner(coordinator, Presets, o, NullLogger<SdlcPipelineRunner>.Instance);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(Diamond(), "t", "s", Path.GetTempPath()))
            events.Add(e);

        Assert.True(Result(events).Succeeded);
        Assert.Equal(new[] { "backend", "model" }, isolation.Acquired.OrderBy(x => x));
        Assert.Contains(events.OfType<AgentTaggedEvent>(), t => t.AgentId == "model" && t.Inner is StatusUpdateEvent { Status: "Worktree merged" });
    }
}
