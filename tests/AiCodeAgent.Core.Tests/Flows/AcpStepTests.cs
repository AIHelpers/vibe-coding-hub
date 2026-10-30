using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E6-S5 prototype: a flow step run by an external ACP agent; its final message reaches the next step as {input:id}.</summary>
public class AcpStepTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    private sealed class FakeExternalAgent : IExternalAgentRunner
    {
        private readonly Func<ExternalAgentRequest, ExternalAgentResult> _answer;
        public List<ExternalAgentRequest> Requests { get; } = new();
        public FakeExternalAgent(Func<ExternalAgentRequest, ExternalAgentResult> answer) => _answer = answer;
        public Task<ExternalAgentResult> RunAsync(ExternalAgentRequest request, Action<string>? onProgress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            onProgress?.Invoke("ACP tool: Read file");
            return Task.FromResult(_answer(request));
        }
    }

    private static SdlcPipelineDefinition Flow(string[]? outcomes = null) => new()
    {
        Name = "acp-demo",
        Stages = new()
        {
            new SdlcStageDefinition
            {
                Id = "external-review", Name = "external-review", Kind = "acp", AcpCommand = "gemini", AcpArgs = new() { "--acp" },
                PromptTemplate = "Review this task: {task}", Outcomes = outcomes?.ToList()
            },
            Stage("summarize") with { PromptTemplate = "Summarize for the team: {input:external-review}" }
        },
        Edges = new() { Edge("external-review", "summarize") }
    };

    private static async Task<(List<AgentEvent> Events, ScriptedOrchestrator Orchestrator)> RunAsync(
        SdlcPipelineDefinition flow, IExternalAgentRunner? external, PermissionMode? mode = null)
    {
        var o = new ScriptedOrchestrator();
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, o, NullLogger<SdlcPipelineRunner>.Instance, externalAgents: external);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(flow, "add pagination", "s1", Path.GetTempPath(), permissionMode: mode))
            events.Add(e);
        return (events, o);
    }

    private static FlowRunResult Result(List<AgentEvent> events) => Assert.Single(events.OfType<FlowFinishedEvent>()).Result;

    [Fact]
    public async Task ExternalStep_OutputFlowsIntoTheNextStep()
    {
        var external = new FakeExternalAgent(_ => new ExternalAgentResult("The pagination needs a stable sort.", "end_turn", 2, Array.Empty<ExternalAgentPermission>()));

        var (events, o) = await RunAsync(Flow(), external, PermissionMode.AutoEdit);

        var result = Result(events);
        Assert.True(result.Succeeded);
        var request = Assert.Single(external.Requests);
        Assert.Equal("gemini", request.Command);
        Assert.Equal(new[] { "--acp" }, request.Args);
        Assert.Equal("Review this task: add pagination", request.Prompt);
        Assert.Equal(PermissionMode.AutoEdit, request.PermissionMode);
        Assert.Equal("The pagination needs a stable sort.", result.Nodes.Single(n => n.NodeId == "external-review").Output);
        // The next step got the external agent's final message through {input:external-review}.
        Assert.Contains("Summarize for the team: The pagination needs a stable sort.", o.Calls.Single(c => c.Node == "summarize").Prompt);
        Assert.DoesNotContain(o.Calls, c => c.Node == "external-review"); // no built-in agent ran for the ACP step
    }

    [Fact]
    public async Task ExternalStep_CanRouteOnAnOutcome()
    {
        var external = new FakeExternalAgent(_ => new ExternalAgentResult("Looks good.\nOUTCOME: approved", "end_turn", 0, Array.Empty<ExternalAgentPermission>()));
        var (events, _) = await RunAsync(Flow(new[] { "approved", "rejected" }), external);
        Assert.Equal("approved", Result(events).Nodes.Single(n => n.NodeId == "external-review").Outcome);
    }

    [Fact]
    public async Task WithoutAnExternalRunner_TheStepFailsClearly()
    {
        var (events, o) = await RunAsync(Flow(), external: null);
        var result = Result(events);
        Assert.False(result.Succeeded);
        Assert.Contains("cannot run external ACP agents", result.Nodes.Single(n => n.NodeId == "external-review").Detail);
        Assert.DoesNotContain(o.Calls, c => c.Node == "summarize");
    }

    [Fact]
    public async Task AgentFailure_StopsTheBranch()
    {
        var failing = new ThrowingExternalAgent();
        var (events, o) = await RunAsync(Flow(), failing);
        Assert.Contains("needs you to sign in", Result(events).Nodes.Single(n => n.NodeId == "external-review").Detail);
        Assert.DoesNotContain(o.Calls, c => c.Node == "summarize");
    }

    private sealed class ThrowingExternalAgent : IExternalAgentRunner
    {
        public Task<ExternalAgentResult> RunAsync(ExternalAgentRequest request, Action<string>? onProgress, CancellationToken cancellationToken) =>
            throw new ExternalAgentException("The agent needs you to sign in first.");
    }

    [Fact]
    public void Validation_RequiresACommand_AndAcceptsTheKind()
    {
        var ok = FlowGraph.Build(Flow());
        Assert.DoesNotContain(ok.Problems, p => p.IsError);

        var broken = Flow();
        broken.Stages[0] = broken.Stages[0] with { AcpCommand = null };
        Assert.Contains(FlowGraph.Build(broken).Problems, p => p.IsError && p.Message.Contains("acpCommand"));
    }
}
