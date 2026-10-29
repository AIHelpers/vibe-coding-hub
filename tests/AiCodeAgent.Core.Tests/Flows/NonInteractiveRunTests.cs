using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>An agent that asks for approval of a tool call, then reports what it was told.</summary>
internal sealed class ApprovalAskingOrchestrator : IAgentOrchestrator
{
    public List<(bool Approved, string Note)> Answers { get; } = new();

    public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(string userMessage, string sessionId, AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ApprovalRequestEvent(new ToolCall { Name = "run_command", Arguments = new Dictionary<string, object?>() }, tcs);
        yield return request;
        var approved = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); // a hang would fail here, not block the suite
        lock (Answers) Answers.Add((approved, request.Feedback.Text ?? ""));
        yield return new AgentFinishedEvent(new AgentResponse { Content = approved ? "ran it" : "carried on without it" });
    }
}

/// <summary>An agent that never finishes (until cancelled).</summary>
internal sealed class HangingOrchestrator : IAgentOrchestrator
{
    public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(string userMessage, string sessionId, AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}

/// <summary>E5-S7: flows run with nobody at the keyboard — questions are answered at once, nothing waits for input.</summary>
public sealed class NonInteractiveRunTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    private static FlowHeadlessRunner Runner(IAgentOrchestrator orchestrator)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        return new FlowHeadlessRunner(new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance));
    }

    private static SdlcPipelineDefinition Flow(params SdlcStageDefinition[] stages) => new()
    {
        Name = "demo",
        Stages = stages.ToList(),
        Edges = stages.Zip(stages.Skip(1), (a, b) => Edge(a.Id, b.Id)).ToList()
    };

    private static Task<FlowHeadlessOutcome> Run(IAgentOrchestrator o, SdlcPipelineDefinition flow, FlowHeadlessOptions? options = null, string task = "review it") =>
        Runner(o).RunAsync(flow, task, Path.GetTempPath(), options).WaitAsync(Guard);

    [Fact]
    public async Task ToolApproval_IsDenied_WithANote_AndTheRunStillSucceeds()
    {
        var o = new ApprovalAskingOrchestrator();
        var outcome = await Run(o, Flow(Stage("review", "reviewer"), Stage("summary", "reviewer")));

        Assert.Equal(FlowExitCodes.Success, outcome.ExitCode);
        Assert.True(outcome.Succeeded);
        Assert.Equal(2, o.Answers.Count);
        Assert.All(o.Answers, a =>
        {
            Assert.False(a.Approved);
            Assert.Equal(FlowHeadlessRunner.DeniedNote, a.Note);
        });
        Assert.Equal(new[] { "review: run_command", "summary: run_command" }, outcome.Denied);
    }

    [Fact]
    public async Task Gate_FailsTheRun_WithCode1_AndAClearReason_AndTheStepNeverRuns()
    {
        var o = new ScriptedOrchestrator();
        var outcome = await Run(o, Flow(Stage("plan", "planner"), Stage("build") with { RequireConfirmation = true }, Stage("ship")));

        Assert.Equal(FlowExitCodes.Failed, outcome.ExitCode);
        Assert.Contains("'build' asks for confirmation", outcome.Reason);
        Assert.Contains("non-interactive", outcome.Reason);
        Assert.DoesNotContain(o.Calls, c => c.Node == "build");
        Assert.DoesNotContain(o.Calls, c => c.Node == "ship");
        Assert.Contains(o.Calls, c => c.Node == "plan");
    }

    [Fact]
    public async Task HumanStep_FailsTheRun_WithCode1()
    {
        var human = new SdlcStageDefinition { Id = "approval", Name = "approval", Kind = "human", PromptTemplate = "OK?", Outcomes = new() { "approved", "rejected" } };
        var o = new ScriptedOrchestrator();
        var outcome = await Run(o, Flow(Stage("design", "planner"), human, Stage("ship")));

        Assert.Equal(FlowExitCodes.Failed, outcome.ExitCode);
        Assert.Contains("'approval' is a human step", outcome.Reason);
        Assert.DoesNotContain(o.Calls, c => c.Node == "ship");
    }

    [Fact]
    public async Task AFlowThatNeverFinishes_IsStoppedByTheTimeout_InsteadOfHanging()
    {
        var started = DateTime.UtcNow;
        var outcome = await Run(new HangingOrchestrator(), Flow(Stage("a", "reviewer"), Stage("b", "reviewer")),
            new FlowHeadlessOptions { Timeout = TimeSpan.FromSeconds(1) });

        Assert.Equal(FlowExitCodes.Failed, outcome.ExitCode);
        Assert.Contains("did not finish within", outcome.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ThePermissionOverride_AppliesToEveryStep()
    {
        var o = new UsageOrchestrator();
        var outcome = await Run(o, Flow(Stage("a", "reviewer"), Stage("b", "implementer")),
            new FlowHeadlessOptions { Permission = PermissionMode.FullAuto });

        Assert.True(outcome.Succeeded);
        Assert.Equal(PermissionMode.FullAuto, o.OptionsOf("a").PermissionMode);
        Assert.Equal(PermissionMode.FullAuto, o.OptionsOf("b").PermissionMode);
    }

    [Fact]
    public async Task WithoutAnOverride_StepsKeepTheirRolePermissionMode()
    {
        var o = new UsageOrchestrator();
        var outcome = await Run(o, Flow(Stage("a", "reviewer"), Stage("b", "reviewer")));

        Assert.True(outcome.Succeeded);
        Assert.Equal(PermissionMode.Plan, o.OptionsOf("a").PermissionMode);
    }
}

/// <summary>E5-S7: 0 success, 1 failed step, 2 budget exceeded, 3 validation error, 4 configuration error.</summary>
public sealed class FlowExitCodeTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    private static Task<FlowHeadlessOutcome> Run(IAgentOrchestrator o, SdlcPipelineDefinition flow, string task = "go", FlowHeadlessOptions? options = null)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new FlowHeadlessRunner(new SdlcPipelineRunner(coordinator, Presets, o, NullLogger<SdlcPipelineRunner>.Instance));
        return runner.RunAsync(flow, task, Path.GetTempPath(), options).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static SdlcPipelineDefinition TwoSteps() => new()
    {
        Name = "two",
        Stages = new() { Stage("a", "reviewer"), Stage("b", "reviewer") },
        Edges = new() { Edge("a", "b") }
    };

    [Fact]
    public void TheCodesAreTheDocumentedOnes()
    {
        Assert.Equal(0, FlowExitCodes.Success);
        Assert.Equal(1, FlowExitCodes.Failed);
        Assert.Equal(2, FlowExitCodes.BudgetExceeded);
        Assert.Equal(3, FlowExitCodes.Validation);
        Assert.Equal(4, FlowExitCodes.Configuration);
    }

    [Fact]
    public void StatusesMapToCodes()
    {
        Assert.Equal(0, FlowExitCodes.FromStatus(FlowRunStatus.Succeeded));
        Assert.Equal(1, FlowExitCodes.FromStatus(FlowRunStatus.Failed));
        Assert.Equal(1, FlowExitCodes.FromStatus(FlowRunStatus.Stopped));
        Assert.Equal(1, FlowExitCodes.FromStatus(FlowRunStatus.Cancelled));
        Assert.Equal(2, FlowExitCodes.FromStatus(FlowRunStatus.BudgetExceeded));
    }

    [Fact]
    public async Task Success_Is0()
    {
        var outcome = await Run(new UsageOrchestrator(), TwoSteps());
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(string.Empty, outcome.Reason);
    }

    [Fact]
    public async Task AFailedStep_Is1()
    {
        var o = new ScriptedOrchestrator();
        o.Failing.Add("a");
        var outcome = await Run(o, TwoSteps());
        Assert.Equal(1, outcome.ExitCode);
        Assert.NotEmpty(outcome.Reason);
    }

    [Fact]
    public async Task ExceedingTheBudget_Is2()
    {
        var outcome = await Run(new UsageOrchestrator(), TwoSteps(), options: new FlowHeadlessOptions { MaxBudgetUsd = 0.0000001m, Model = FlowCostAggregationTests.Sonnet });
        Assert.Equal(2, outcome.ExitCode);
    }

    [Fact]
    public async Task ALinearPipeline_Is3()
    {
        var linear = new SdlcPipelineDefinition { Name = "lin", Stages = new() { Stage("a", "reviewer") } };
        var outcome = await Run(new UsageOrchestrator(), linear);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Contains("linear", outcome.Reason);
    }

    [Fact]
    public async Task AnEmptyTask_Is3()
    {
        var outcome = await Run(new UsageOrchestrator(), TwoSteps(), task: "  ");
        Assert.Equal(3, outcome.ExitCode);
    }

    [Fact]
    public async Task AnInvalidFlow_Is3_AndNothingRuns()
    {
        var o = new UsageOrchestrator();
        var broken = new SdlcPipelineDefinition
        {
            Name = "broken",
            Stages = new() { Stage("a", "reviewer"), Stage("b", "reviewer") },
            Edges = new() { Edge("a", "missing") }
        };
        var outcome = await Run(o, broken);
        Assert.Equal(3, outcome.ExitCode);
        Assert.StartsWith("The flow is invalid", outcome.Reason);
        Assert.Empty(o.Calls);
    }
}

public sealed class FlowReportTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    private static SdlcPipelineDefinition Review() => new()
    {
        Name = "pr-review",
        Stages = new() { Stage("security", "reviewer"), Stage("style", "reviewer"), Stage("summary", "reviewer") },
        Edges = new() { Edge("security", "summary"), Edge("style", "summary") }
    };

    private static async Task<(SdlcPipelineDefinition Flow, FlowHeadlessOutcome Outcome)> RunAsync(Func<string, int, string>? answer = null)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new FlowHeadlessRunner(new SdlcPipelineRunner(coordinator, Presets, new UsageOrchestrator(answer), NullLogger<SdlcPipelineRunner>.Instance));
        var flow = Review();
        var outcome = await runner.RunAsync(flow, "review PR 7", Path.GetTempPath(),
            new FlowHeadlessOptions { Model = FlowCostAggregationTests.Sonnet }).WaitAsync(TimeSpan.FromSeconds(20));
        return (flow, outcome);
    }

    [Fact]
    public async Task Markdown_StartsWithTheMarker_AndShowsStatusCostAndTheResult()
    {
        var (flow, outcome) = await RunAsync((node, _) => node == "summary" ? "LGTM, two nits." : $"{node} notes");
        var md = FlowReport.Markdown(flow, "review PR 7", outcome);

        Assert.StartsWith("<!-- vibe-coding-hub:flow:pr-review -->", md);
        Assert.Contains("Flow `pr-review`", md);
        Assert.Contains("3/3 steps", md);
        Assert.Contains("### Result", md);
        Assert.Contains("LGTM, two nits.", md);
        Assert.Contains("| `security` |", md);
        Assert.Contains("security notes", md);              // per-step output in a collapsible block
    }

    [Fact]
    public async Task Markdown_IsShortenedToFitAComment()
    {
        var (flow, outcome) = await RunAsync((node, _) => new string('x', 200_000));
        var md = FlowReport.Markdown(flow, "t", outcome);

        Assert.True(md.Length <= 65_000, $"{md.Length} characters");
        Assert.StartsWith("<!-- vibe-coding-hub:flow:pr-review -->", md);
    }

    [Fact]
    public async Task Json_HasTheExitCodeStatusAndSteps()
    {
        var (flow, outcome) = await RunAsync();
        using var doc = JsonDocument.Parse(FlowReport.Json(flow, outcome));
        var root = doc.RootElement;

        Assert.Equal("pr-review", root.GetProperty("flow").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("succeeded", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("steps", out _));
        Assert.True(root.TryGetProperty("totals", out _));
    }

    [Fact]
    public void Json_ForARunThatNeverStarted_SaysSo()
    {
        var outcome = new FlowHeadlessOutcome(FlowExitCodes.Validation, null, "The task is empty.", Array.Empty<string>());
        using var doc = JsonDocument.Parse(FlowReport.Json(Review(), outcome));

        Assert.Equal(3, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("not_run", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Markdown_ForARunThatNeverStarted_ShowsTheReason()
    {
        var outcome = new FlowHeadlessOutcome(FlowExitCodes.Validation, null, "The task is empty.", Array.Empty<string>());
        var md = FlowReport.Markdown(Review(), "", outcome);

        Assert.StartsWith("<!-- vibe-coding-hub:flow:pr-review -->", md);
        Assert.Contains("The task is empty.", md);
    }
}

public sealed class ProviderEnvironmentTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    private static readonly ProviderConfiguration FromFile = new()
    {
        Name = "openai", ApiKey = "file-key", BaseUrl = "https://api.example.com/v1", DefaultModel = "file-model"
    };

    [Fact]
    public void VariableNames_UseTheProviderName()
    {
        Assert.Equal("AI_CODE_AGENT_OPENAI_API_KEY", ProviderEnvironment.VariableName("openai", "API_KEY"));
        Assert.Equal("AIAGENT_MY_LLM_BASE_URL", ProviderEnvironment.VariableName("my-llm", "BASE_URL", legacy: true));
    }

    [Fact]
    public void WithoutVariables_TheConfigFileStands()
    {
        var result = ProviderEnvironment.Apply(FromFile, "openai", Env());
        Assert.Equal(FromFile, result);
    }

    [Fact]
    public void Variables_WinOverTheConfigFile()
    {
        var result = ProviderEnvironment.Apply(FromFile, "openai", Env(
            ("AI_CODE_AGENT_OPENAI_API_KEY", " env-key "),
            ("AI_CODE_AGENT_OPENAI_BASE_URL", "http://vllm.internal:8000/v1"),
            ("AI_CODE_AGENT_OPENAI_MODEL", "qwen")));

        Assert.Equal("env-key", result.ApiKey);
        Assert.Equal("http://vllm.internal:8000/v1", result.BaseUrl);
        Assert.Equal("qwen", result.DefaultModel);
    }

    [Fact]
    public void TheNewNameBeatsTheLegacyName()
    {
        var result = ProviderEnvironment.Apply(FromFile, "openai", Env(
            ("AIAGENT_OPENAI_API_KEY", "legacy"), ("AI_CODE_AGENT_OPENAI_API_KEY", "new")));
        Assert.Equal("new", result.ApiKey);

        var legacyOnly = ProviderEnvironment.Apply(FromFile, "openai", Env(("AIAGENT_OPENAI_BASE_URL", "http://legacy/v1")));
        Assert.Equal("http://legacy/v1", legacyOnly.BaseUrl);
    }

    [Fact]
    public void BlankVariables_AreIgnored()
    {
        var result = ProviderEnvironment.Apply(FromFile, "openai", Env(
            ("AI_CODE_AGENT_OPENAI_API_KEY", "  "), ("AIAGENT_OPENAI_BASE_URL", "")));
        Assert.Equal("file-key", result.ApiKey);
        Assert.Equal("https://api.example.com/v1", result.BaseUrl);
    }
}
