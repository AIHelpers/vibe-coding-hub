using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using static AiCodeAgent.Core.Tests.Flows.FlowCostAggregationTests;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E5-S4: a budget cap on a flow run (fake orchestrator with fixed usage: every step costs the same).</summary>
public class FlowBudgetTests
{
    private static readonly decimal Step = CostOf(Sonnet); // $0.0105

    private static SdlcPipelineDefinition Chain() => new()
    {
        Name = "chain",
        Stages = new() { Stage("first"), Stage("second"), Stage("third") },
        Edges = new() { Edge("first", "second"), Edge("second", "third") }
    };

    [Fact]
    public async Task BudgetBelowTheFirstStep_StopsTheFlowAfterIt_WithAClearMessage()
    {
        var orchestrator = new UsageOrchestrator();

        var (events, result) = await RunAsync(orchestrator, Chain(), maxBudgetUsd: Step / 10);

        Assert.Equal(FlowRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(new[] { "first" }, orchestrator.Calls.Select(c => c.Node).ToArray());
        Assert.Equal(FlowNodeState.Done, result.Nodes.Single(n => n.NodeId == "first").State);
        foreach (var id in new[] { "second", "third" })
        {
            var n = result.Nodes.Single(n => n.NodeId == id);
            Assert.Equal(FlowNodeState.Skipped, n.State);
            Assert.Equal("budget exceeded", n.Detail);
        }
        Assert.Contains("Budget exceeded", result.Message);
        Assert.Contains(FlowUsageFormat.Cost(Step), result.Message);          // what was spent
        Assert.Contains(FlowUsageFormat.Cost(Step / 10), result.Message);     // the cap
        Assert.Equal("budget_exceeded", FlowUsageFormat.StatusName(result.Status));
        Assert.Equal(Step, result.Usage.CostUsd);
    }

    [Fact]
    public async Task BudgetThatTheSecondStepCrosses_LetsTwoStepsRun()
    {
        var orchestrator = new UsageOrchestrator();

        var (_, result) = await RunAsync(orchestrator, Chain(), maxBudgetUsd: Step * 1.5m);

        Assert.Equal(FlowRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(new[] { "first", "second" }, orchestrator.Calls.Select(c => c.Node).ToArray());
        Assert.Equal(2 * Step, result.Usage.CostUsd);
    }

    [Fact]
    public async Task BudgetAboveTheWholeRun_ChangesNothing()
    {
        var (_, result) = await RunAsync(new UsageOrchestrator(), Chain(), maxBudgetUsd: 100m);

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(3, result.StepRuns.Count);
        Assert.Equal(100m, result.BudgetUsd);
    }

    [Fact]
    public async Task NoBudget_MeansNoCap()
    {
        var orchestrator = new UsageOrchestrator();

        var (_, result) = await RunAsync(orchestrator, Chain(), maxBudgetUsd: null);
        var (_, zero) = await RunAsync(new UsageOrchestrator(), Chain(), maxBudgetUsd: 0m);

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(FlowRunStatus.Succeeded, zero.Status);
        Assert.Null(result.BudgetUsd);
        Assert.Null(orchestrator.OptionsOf("first").MaxBudgetUsd);
    }

    [Fact]
    public async Task LastStepCrossingTheBudget_StillSucceeds_BecauseNothingWasLeftToRun()
    {
        var flow = new SdlcPipelineDefinition { Name = "two", Stages = new() { Stage("a"), Stage("b") }, Edges = new() { Edge("a", "b") } };

        var (_, result) = await RunAsync(new UsageOrchestrator(), flow, maxBudgetUsd: Step * 1.5m);

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ParallelSteps_BothFinishTheirCall_ThenNoJoinStarts()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "fan-in",
            Stages = new() { Stage("left"), Stage("right"), Stage("join", "reviewer") },
            Edges = new() { Edge("left", "join"), Edge("right", "join") }
        };
        var orchestrator = new UsageOrchestrator();

        var (_, result) = await RunAsync(orchestrator, flow, maxBudgetUsd: Step / 2);

        Assert.Equal(FlowRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(FlowNodeState.Done, result.Nodes.Single(n => n.NodeId == "left").State);
        Assert.Equal(FlowNodeState.Done, result.Nodes.Single(n => n.NodeId == "right").State);
        Assert.Equal(FlowNodeState.Skipped, result.Nodes.Single(n => n.NodeId == "join").State);
        Assert.DoesNotContain(orchestrator.Calls, c => c.Node == "join");
        Assert.Equal(2 * Step, result.Usage.CostUsd);
    }

    [Fact]
    public async Task EachStepGetsWhatIsLeftOfTheBudgetAsItsOwnCap()
    {
        var orchestrator = new UsageOrchestrator();

        await RunAsync(orchestrator, Chain(), maxBudgetUsd: 1m);

        Assert.Equal(1m, orchestrator.OptionsOf("first").MaxBudgetUsd);
        Assert.Equal(1m - Step, orchestrator.OptionsOf("second").MaxBudgetUsd);
        Assert.Equal(1m - 2 * Step, orchestrator.OptionsOf("third").MaxBudgetUsd);
    }

    [Fact]
    public async Task StepStoppedByItsOwnCap_FailsWithBudgetExceeded_AndTheFlowEndsAsBudgetExceeded()
    {
        var orchestrator = new UsageOrchestrator();
        orchestrator.StoppedByBudget.Add("second");

        var (_, result) = await RunAsync(orchestrator, Chain(), maxBudgetUsd: 5m);

        Assert.Equal(FlowRunStatus.BudgetExceeded, result.Status);
        var second = result.Nodes.Single(n => n.NodeId == "second");
        Assert.Equal(FlowNodeState.Failed, second.State);
        Assert.Equal("budget exceeded", second.Detail);
        Assert.Equal(FlowNodeState.Skipped, result.Nodes.Single(n => n.NodeId == "third").State);
        Assert.Equal(2, result.StepRuns.Count); // the stopped step's partial spend still counts
    }

    // ------------------------------------------------------------ the flow definition

    [Fact]
    public void MaxBudgetUsd_RoundTripsInTheFlowFile_AndOldFilesHaveNone()
    {
        var def = Chain() with { MaxBudgetUsd = 2.5m };

        var json = JsonSerializer.Serialize(def, SdlcPipelineLoader.FileJsonOptions);
        var back = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions)!;

        Assert.Contains("\"maxBudgetUsd\": 2.5", json);
        Assert.Equal(2.5m, back.MaxBudgetUsd);
        var old = JsonSerializer.Deserialize<SdlcPipelineDefinition>("""{ "name": "old", "stages": [] }""", SdlcPipelineLoader.FileJsonOptions)!;
        Assert.Null(old.MaxBudgetUsd);
        Assert.DoesNotContain("maxBudgetUsd", JsonSerializer.Serialize(old, SdlcPipelineLoader.FileJsonOptions));
    }

    [Fact]
    public async Task TheFlowsOwnBudget_IsUsed_AndTheRunOptionOverridesIt()
    {
        var capped = Chain() with { MaxBudgetUsd = Step / 10 };

        var (_, fromFile) = await RunAsync(new UsageOrchestrator(), capped);
        var (_, overridden) = await RunAsync(new UsageOrchestrator(), capped, maxBudgetUsd: 100m);

        Assert.Equal(FlowRunStatus.BudgetExceeded, fromFile.Status);
        Assert.Equal(Step / 10, fromFile.BudgetUsd);
        Assert.Equal(FlowRunStatus.Succeeded, overridden.Status);
        Assert.Equal(100m, overridden.BudgetUsd);
    }
}
