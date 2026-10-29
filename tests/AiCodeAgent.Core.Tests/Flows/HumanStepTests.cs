using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E5-S6: <c>"kind": "human"</c> steps run no agent; the user picks an outcome and the edges route on it.</summary>
public sealed class HumanStepTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    private static SdlcStageDefinition Human(string id, params string[] outcomes) => new()
    {
        Id = id,
        Name = id,
        Kind = "human",
        PromptTemplate = "Please review the design.",
        Outcomes = outcomes.Length == 0 ? null : outcomes.ToList()
    };

    /// <summary>design → approval (human: approved / rejected) → ship on approved, → rework on rejected.</summary>
    private static SdlcPipelineDefinition Approval() => new()
    {
        Name = "manual-approval",
        Stages = new() { Stage("design", "planner"), Human("approval", "approved", "rejected"), Stage("ship"), Stage("rework") },
        Edges = new() { Edge("design", "approval"), Edge("approval", "ship", "approved"), Edge("approval", "rework", "rejected") }
    };

    private static async Task<(List<AgentEvent> Events, FlowRunResult Result)> RunAsync(
        ScriptedOrchestrator orchestrator, SdlcPipelineDefinition flow, Func<FlowHumanRequestEvent, FlowHumanChoice> choose, FlowRunStore? store = null)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance, null, null, null, store);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(flow, "add pagination", "s1", Path.GetTempPath()))
        {
            events.Add(e);
            if (e is AgentTaggedEvent { Inner: FlowHumanRequestEvent request }) request.Choice.SetResult(choose(request));
        }
        return (events, Assert.Single(events.OfType<FlowFinishedEvent>()).Result);
    }

    private static string PromptOf(ScriptedOrchestrator o, string node, int call = 1) => o.Calls.Where(c => c.Node == node).ElementAt(call - 1).Prompt;

    [Fact]
    public async Task Approved_RoutesLikeAnAgentOutcome()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice("approved"));

        Assert.True(result.Succeeded);
        Assert.Equal("approved", result.Nodes.Single(n => n.NodeId == "approval").Outcome);
        Assert.Contains(o.Calls, c => c.Node == "ship");
        Assert.DoesNotContain(o.Calls, c => c.Node == "rework");
        Assert.Equal(FlowNodeState.Skipped, result.Nodes.Single(n => n.NodeId == "rework").State);
    }

    [Fact]
    public async Task Rejected_TakesTheOtherEdge()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice("rejected"));

        Assert.True(result.Succeeded);
        Assert.Contains(o.Calls, c => c.Node == "rework");
        Assert.DoesNotContain(o.Calls, c => c.Node == "ship");
    }

    [Fact]
    public async Task AHumanStep_NeverCallsTheModel()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice("approved"));

        Assert.DoesNotContain(o.Calls, c => c.Node == "approval");
        Assert.Equal(0m, result.Usage.CostUsd);
        Assert.DoesNotContain(result.StepRuns, u => u.NodeId == "approval");
    }

    [Fact]
    public async Task TheRequest_CarriesThePromptAndTheOutcomes()
    {
        FlowHumanRequestEvent? seen = null;
        var o = new ScriptedOrchestrator((node, _, _) => node == "design" ? "the design text" : null);
        await RunAsync(o, Approval(), r => { seen = r; return new FlowHumanChoice("approved"); });

        Assert.NotNull(seen);
        Assert.Equal("approval", seen!.NodeId);
        Assert.Equal(new[] { "approved", "rejected" }, seen.Outcomes);
        Assert.Contains("Please review the design.", seen.Prompt);
        Assert.Contains("the design text", seen.Prompt); // the step's inputs are shown to the reviewer
    }

    [Fact]
    public async Task TheNote_TravelsToTheStepsAfter()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice("approved", "looks good, ship it Friday"));

        Assert.Contains("Reviewer note: looks good, ship it Friday", PromptOf(o, "ship"));
        Assert.Contains("looks good, ship it Friday", result.Nodes.Single(n => n.NodeId == "approval").Output);
    }

    [Fact]
    public async Task InALoop_ARejectionWithANote_SendsTheFeedbackBack()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "loop",
            Stages = new() { Stage("design", "planner"), Human("approval", "approved", "rejected"), Stage("ship") },
            Edges = new() { Edge("design", "approval"), Edge("approval", "ship", "approved"), Edge("approval", "design", "rejected", maxLoops: 2) }
        };
        var round = 0;
        var o = new ScriptedOrchestrator();

        var (_, result) = await RunAsync(o, flow, _ => ++round == 1
            ? new FlowHumanChoice("rejected", "add a section on error handling")
            : new FlowHumanChoice("approved"));

        Assert.True(result.Succeeded);
        Assert.Equal(2, o.Calls.Count(c => c.Node == "design"));
        Assert.Contains("add a section on error handling", PromptOf(o, "design", 2));
        Assert.Contains(o.Calls, c => c.Node == "ship");
    }

    [Fact]
    public async Task Stop_EndsTheFlow()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice(null, null, Stop: true));

        Assert.Equal(FlowRunStatus.Stopped, result.Status);
        Assert.DoesNotContain(o.Calls, c => c.Node is "ship" or "rework");
    }

    [Fact]
    public async Task AnOutcomeThatIsNotOffered_FailsTheStep()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice("maybe"));

        Assert.Equal(FlowRunStatus.Failed, result.Status);
        Assert.Contains("maybe", result.Nodes.Single(n => n.NodeId == "approval").Detail);
    }

    [Fact]
    public async Task TheOutcome_IsMatchedIgnoringCase()
    {
        var o = new ScriptedOrchestrator();
        var (_, result) = await RunAsync(o, Approval(), _ => new FlowHumanChoice("APPROVED"));

        Assert.Equal("approved", result.Nodes.Single(n => n.NodeId == "approval").Outcome);
    }

    [Fact]
    public async Task WithoutOutcomes_TheStepIsAPauseThatContinues()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "pause",
            Stages = new() { Stage("design", "planner"), Human("look"), Stage("ship") },
            Edges = new() { Edge("design", "look"), Edge("look", "ship") }
        };
        var o = new ScriptedOrchestrator();

        var (_, result) = await RunAsync(o, flow, _ => new FlowHumanChoice(null, "go"));

        Assert.True(result.Succeeded);
        Assert.Contains(o.Calls, c => c.Node == "ship");
    }

    [Fact]
    public async Task ADisabledHumanStep_IsNotAsked()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "off",
            Stages = new() { Stage("design", "planner"), Human("look") with { Enabled = false }, Stage("ship") },
            Edges = new() { Edge("design", "look"), Edge("look", "ship") }
        };
        var asked = false;

        var (_, result) = await RunAsync(new ScriptedOrchestrator(), flow, _ => { asked = true; return new FlowHumanChoice(null); });

        Assert.False(asked);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task TheQuestionAndAnswer_AreSavedWithTheRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "humanstep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FlowRunStore(root);
            var (_, result) = await RunAsync(new ScriptedOrchestrator(), Approval(), _ => new FlowHumanChoice("approved", "fine"), store);

            var record = store.Load("manual-approval", result.RunId!)!;
            var step = record.Steps.Single(s => s.NodeId == "approval");
            Assert.Equal("approved", step.Outcome);
            Assert.Contains("Please review the design.", step.Prompt);
            Assert.Contains("fine", store.LoadOutput("manual-approval", record.Id, step));
            Assert.Null(step.Usage);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AHumanStep_NeedsNoCharacterOrRole()
    {
        var graph = FlowGraph.Build(Approval(), characterLookup: null);

        Assert.DoesNotContain(graph.Problems, p => p.IsError && p.NodeId == "approval");
        Assert.True(graph.GetNode("approval")!.Stage.IsHuman);
    }

    [Fact]
    public void AnUnknownKind_IsAnError()
    {
        var flow = Approval();
        flow.Stages[1] = flow.Stages[1] with { Kind = "robot" };

        var graph = FlowGraph.Build(flow, characterLookup: null);

        Assert.Contains(graph.Problems, p => p.IsError && p.NodeId == "approval" && p.Message.Contains("robot"));
    }

    [Fact]
    public void Kind_RoundTripsThroughTheFlowFile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Approval(), SdlcPipelineLoader.FileJsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, SdlcPipelineLoader.FileJsonOptions)!;

        Assert.Contains("\"kind\": \"human\"", json);
        Assert.True(back.Stages[1].IsHuman);
        Assert.False(back.Stages[0].IsHuman);
    }
}
