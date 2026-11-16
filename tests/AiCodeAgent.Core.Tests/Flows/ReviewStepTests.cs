using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Evidence;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E10-S4b: the built-in "review" flow step: validation, routing and what a failed reviewer does.</summary>
public sealed class ReviewStepTests
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    // ------------------------------------------------------------------ fakes

    /// <summary>The author step "impl" changes one file; every other step just answers.</summary>
    private sealed class AuthorOrchestrator : IAgentOrchestrator
    {
        public bool ProducesDiff { get; init; } = true;
        public List<string> Prompts { get; } = new();

        public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentEvent> StreamRunAsync(string userMessage, string sessionId, AgentOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Prompts.Add(userMessage);
            await Task.Yield();
            if (ProducesDiff && options.AgentId == "impl")
                yield return new DiffProducedEvent(new DiffEntry { FilePath = "src/A.cs", DiffText = "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-old\n+new\n", AgentId = "impl" });
            yield return new AgentFinishedEvent(new AgentResponse { Content = "impl done" });
        }
    }

    private sealed class ScriptedProvider : IAiProvider
    {
        private readonly Queue<string> _answers;
        public int Calls;
        public Exception? Throw { get; init; }
        public ScriptedProvider(string name, params string[] answers) { Name = name; _answers = new Queue<string>(answers); }
        public string Name { get; }
        public string[] SupportedModels => Array.Empty<string>();

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw != null) throw Throw;
            return Task.FromResult(new CompletionResponse { Content = _answers.Count > 0 ? _answers.Dequeue() : "", Usage = new TokenUsage { PromptTokens = 100, CompletionTokens = 20 } });
        }

        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<string>());
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        private readonly Dictionary<string, IAiProvider> _providers;
        public FakeResolver(string defaultName, Dictionary<string, IAiProvider> providers) { DefaultProviderName = defaultName; _providers = providers; }
        public string? DefaultProviderName { get; }
        public IReadOnlyList<string> ProviderNames => _providers.Keys.ToList();
        public IAiProvider? Resolve(string providerName) => _providers.GetValueOrDefault(providerName);
        public string? DefaultModel(string providerName) => providerName + "-model";
    }

    private static SdlcStageDefinition ReviewStage(string id = "check", string? provider = null, string? model = null) => new()
    {
        Id = id,
        Name = "Independent review",
        Kind = "review",
        Provider = provider,
        Model = model
    };

    private static SdlcPipelineDefinition Flow() => new()
    {
        Name = "reviewed",
        Stages = new() { Stage("impl"), ReviewStage(), Stage("ship") },
        Edges = new() { Edge("impl", "check"), Edge("check", "ship", "approved"), Edge("check", "impl", "rejected", 1) }
    };

    private static async Task<FlowRunResult> RunAsync(SdlcPipelineDefinition flow, IAgentOrchestrator orchestrator, ReviewService? service)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance, reviewService: service);
        FlowRunResult? result = null;
        await foreach (var e in runner.RunAsync(flow, "add pagination", "s1", Path.GetTempPath()))
            if (e is FlowFinishedEvent f) result = f.Result;
        return Assert.IsType<FlowRunResult>(result);
    }

    private static ReviewService Service(ScriptedProvider reviewer) =>
        new(new FakeResolver("anthropic", new() { ["anthropic"] = new ScriptedProvider("anthropic"), ["openai"] = reviewer }));

    private const string Approve = """{"verdict":"approve","summary":"Fine.","findings":[]}""";
    private const string Reject = """{"verdict":"reject","summary":"Broken.","findings":[{"severity":"high","title":"Off by one","detail":"d","file":"src/A.cs"}]}""";

    // ------------------------------------------------------------------ validation

    [Fact]
    public void ReviewStep_IsValid_WithoutCharacterOrPrompt_AndDefaultsToApprovedRejected()
    {
        var g = FlowGraph.Build(Flow());
        Assert.True(g.IsValid, string.Join("; ", g.Problems.Select(p => p.Message)));
        Assert.Equal(new[] { "approved", "rejected" }, g.Nodes.Single(n => n.Id == "check").Outcomes);
    }

    [Fact]
    public void ReviewStep_NeedsBothOutcomes_AndWarnsWhenNothingComesBefore()
    {
        var stage = new SdlcStageDefinition { Id = "check", Name = "r", Kind = "review", Outcomes = new() { "approved" } };
        var g = FlowGraph.Build(new SdlcPipelineDefinition { Name = "x", Stages = new() { stage }, Edges = new() });
        Assert.Contains(g.Problems, p => p.IsError && p.Message.Contains("rejected"));

        var alone = FlowGraph.Build(new SdlcPipelineDefinition { Name = "y", Stages = new() { ReviewStage() }, Edges = new() });
        Assert.Contains(alone.Problems, p => !p.IsError && p.NodeId == "check");
    }

    // ------------------------------------------------------------------ findings text

    [Fact]
    public void FindingsText_ListsVerdictSummaryAndFindings()
    {
        var review = new IndependentReview
        {
            Verdict = ReviewVerdict.Reject,
            Summary = "Broken.",
            Findings = new[] { new ReviewFinding(FindingSeverity.High, "Off by one", "d", "src/A.cs", 3) }
        };
        var text = FlowReviewText.ForAgents(review);
        Assert.Contains("changes requested", text);
        Assert.Contains("Broken.", text);
        Assert.Contains("[high] Off by one (src/A.cs:3): d", text);
    }

    // ------------------------------------------------------------------ running

    [Fact]
    public async Task Approved_ContinuesTheFlow_AndCountsReviewerTokens()
    {
        var reviewer = new ScriptedProvider("openai", Approve);
        var result = await RunAsync(Flow(), new AuthorOrchestrator(), Service(reviewer));

        Assert.True(result.Status == FlowRunStatus.Succeeded, result.Message + " | " + string.Join(" ; ", result.Nodes.Select(n => $"{n.NodeId}:{n.State}:{n.Detail}")));
        Assert.Equal("approved", result.Nodes.Single(n => n.NodeId == "check").Outcome);
        Assert.Equal(FlowNodeState.Done, result.Nodes.Single(n => n.NodeId == "ship").State);
        var step = result.StepRuns.Single(r => r.NodeId == "check");
        Assert.Equal(100, step.PromptTokens);
        Assert.Equal(20, step.CompletionTokens);
        Assert.Equal("openai", step.Provider);
    }

    [Fact]
    public async Task Rejected_LoopsBackToTheAuthor_WithTheFindingsAsFeedback()
    {
        var reviewer = new ScriptedProvider("openai", Reject, Approve);
        var author = new AuthorOrchestrator();
        var result = await RunAsync(Flow(), author, Service(reviewer));

        Assert.True(result.Status == FlowRunStatus.Succeeded, result.Message + " | " + string.Join(" ; ", result.Nodes.Select(n => $"{n.NodeId}:{n.State}:{n.Detail}")));
        Assert.Equal(2, reviewer.Calls);
        Assert.Contains(author.Prompts, p => p.Contains("Off by one"));
    }

    [Fact]
    public async Task NothingChanged_IsApproved_WithoutCallingTheReviewer()
    {
        var reviewer = new ScriptedProvider("openai", Reject);
        var result = await RunAsync(Flow(), new AuthorOrchestrator { ProducesDiff = false }, Service(reviewer));

        Assert.Equal(0, reviewer.Calls);
        Assert.Equal("approved", result.Nodes.Single(n => n.NodeId == "check").Outcome);
        Assert.Contains("Nothing to review", result.Nodes.Single(n => n.NodeId == "check").Output);
    }

    [Fact]
    public async Task WithoutReviewService_OrWhenTheReviewerIsDown_TheStepFails_InsteadOfPassing()
    {
        var noService = await RunAsync(Flow(), new AuthorOrchestrator(), null);
        Assert.NotEqual(FlowRunStatus.Succeeded, noService.Status);
        Assert.Equal(FlowNodeState.Failed, noService.Nodes.Single(n => n.NodeId == "check").State);

        var down = new ScriptedProvider("openai") { Throw = new HttpRequestException("503") };
        var failed = await RunAsync(Flow(), new AuthorOrchestrator(), Service(down));
        Assert.NotEqual(FlowRunStatus.Succeeded, failed.Status);
        Assert.Equal(FlowNodeState.Failed, failed.Nodes.Single(n => n.NodeId == "check").State);
    }
}
