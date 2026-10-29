using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowCostAggregationTests;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

public sealed class FlowReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flowreplay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static SdlcPipelineDefinition Diamond() => new()
    {
        Name = "diamond",
        Stages = new() { Stage("design", "planner"), Stage("backend"), Stage("model"), Stage("review", "reviewer") },
        Edges = new() { Edge("design", "backend"), Edge("design", "model"), Edge("backend", "review"), Edge("model", "review") }
    };

    private async Task<(List<AgentEvent> Events, FlowRunResult Result)> RunAsync(
        UsageOrchestrator orchestrator, FlowRunStore store, SdlcPipelineDefinition flow, FlowReplayPlan? replay = null)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance,
            null, null, null, store);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(flow, "ship it", "s1", Path.GetTempPath(), model: Sonnet, replay: replay))
            events.Add(e);
        return (events, Assert.Single(events.OfType<FlowFinishedEvent>()).Result);
    }

    [Fact]
    public async Task AfterARun_TheHistoryListsIt()
    {
        var store = new FlowRunStore(_root);
        var (_, result) = await RunAsync(new UsageOrchestrator(), store, Diamond());

        var run = Assert.Single(store.List("diamond"));
        Assert.Equal(result.RunId, run.RunId);
        Assert.Equal("ship it", run.Task);
        Assert.Equal(FlowRunStatus.Succeeded, run.Status);
        Assert.Equal(result.Usage.CostUsd, run.CostUsd);
        Assert.True(run.CostUsd > 0);
        Assert.True(run.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task TheSavedRun_HoldsTheDefinitionAndEveryStep()
    {
        var store = new FlowRunStore(_root);
        var (_, result) = await RunAsync(new UsageOrchestrator((node, _) => $"{node} says hi"), store, Diamond());

        var record = store.Load("diamond", result.RunId!)!;

        Assert.Equal(4, record.Definition.Stages.Count);
        Assert.Equal(new[] { "backend", "design", "model", "review" }, record.Steps.Select(s => s.NodeId).OrderBy(n => n));
        var review = record.Steps.Single(s => s.NodeId == "review");
        Assert.False(string.IsNullOrEmpty(review.Prompt));
        Assert.Contains("backend says hi", review.Prompt);
        Assert.Equal("review says hi", store.LoadOutput("diamond", record.Id, review));
        Assert.Contains("backend", review.InputFrom);
        Assert.NotNull(review.Usage);
        Assert.NotEmpty(record.Events);
    }

    [Fact]
    public async Task RerunFromReview_DoesNotCallTheProviderForUpstreamSteps()
    {
        var store = new FlowRunStore(_root);
        var (_, first) = await RunAsync(new UsageOrchestrator((node, _) => $"{node} v1"), store, Diamond());
        var record = store.Load("diamond", first.RunId!)!;
        var plan = store.BuildReplay(record, "review", out var error);
        Assert.Null(error);

        var second = new UsageOrchestrator((node, _) => $"{node} v2");
        var (events, result) = await RunAsync(second, store, record.Definition, plan);

        Assert.Equal("review", Assert.Single(second.Calls).Node);
        Assert.True(result.Succeeded);
        foreach (var node in new[] { "design", "backend", "model" })
            Assert.Contains(events.OfType<FlowNodeStatusEvent>(), e => e.NodeId == node && e.State == FlowNodeState.Done && e.Detail == "reused");
    }

    [Fact]
    public async Task RerunFromReview_FeedsTheRecordedOutputsIntoTheReviewPrompt()
    {
        var store = new FlowRunStore(_root);
        var (_, first) = await RunAsync(new UsageOrchestrator((node, _) => $"{node} v1"), store, Diamond());
        var record = store.Load("diamond", first.RunId!)!;
        var plan = store.BuildReplay(record, "review", out _);

        var (_, result) = await RunAsync(new UsageOrchestrator((node, _) => $"{node} v2"), store, record.Definition, plan);

        var rerun = store.Load("diamond", result.RunId!)!;
        var review = rerun.Steps.Single(s => s.NodeId == "review" && !s.Reused);
        Assert.Contains("backend v1", review.Prompt);
        Assert.Contains("model v1", review.Prompt);
        Assert.Equal("review v2", store.LoadOutput("diamond", rerun.Id, review));
    }

    [Fact]
    public async Task Rerun_IsANewRun_LinkedToTheOriginal_AndCostsOnlyTheNewSteps()
    {
        var store = new FlowRunStore(_root);
        var (_, first) = await RunAsync(new UsageOrchestrator(), store, Diamond());
        var record = store.Load("diamond", first.RunId!)!;
        var plan = store.BuildReplay(record, "review", out _);

        var (_, result) = await RunAsync(new UsageOrchestrator(), store, record.Definition, plan);

        Assert.NotEqual(first.RunId, result.RunId);
        var rerun = store.Load("diamond", result.RunId!)!;
        Assert.Equal(first.RunId, rerun.ReplayOfRunId);
        Assert.Equal("review", rerun.ReplayFromNodeId);
        Assert.Equal(2, store.List("diamond").Count);
        Assert.Equal(CostOf(Sonnet), result.Usage.CostUsd);
        Assert.Single(result.StepRuns);
        Assert.Equal(3, rerun.Steps.Count(s => s.Reused));
    }

    [Fact]
    public async Task RerunFromAMiddleStep_RunsItAndItsDescendantsOnly()
    {
        var store = new FlowRunStore(_root);
        var (_, first) = await RunAsync(new UsageOrchestrator(), store, Diamond());
        var record = store.Load("diamond", first.RunId!)!;
        var plan = store.BuildReplay(record, "backend", out _);

        var second = new UsageOrchestrator();
        await RunAsync(second, store, record.Definition, plan);

        // model is a sibling branch: it is neither upstream nor downstream of backend, so it is reused too.
        Assert.Equal(new[] { "backend", "review" }, second.Calls.Select(c => c.Node).OrderBy(n => n));
    }

    [Fact]
    public async Task ARunWithoutAStore_IsNotSaved()
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, new UsageOrchestrator(), NullLogger<SdlcPipelineRunner>.Instance);
        FlowRunResult? result = null;
        await foreach (var e in runner.RunAsync(Diamond(), "t", "s", Path.GetTempPath(), model: Sonnet))
            if (e is FlowFinishedEvent f) result = f.Result;

        Assert.Null(result!.RunId);
    }
}
