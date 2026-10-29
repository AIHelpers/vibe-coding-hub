using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>
/// Orchestrator double with fixed usage: every node run reports the same tokens, answers with a scripted text and
/// remembers the options it got (model, budget cap).
/// </summary>
internal sealed class UsageOrchestrator : IAgentOrchestrator
{
    private readonly Func<string, int, string> _answer;
    public int PromptTokens { get; init; } = 1000;
    public int CompletionTokens { get; init; } = 500;
    /// <summary>Nodes whose run ends with stop reason "budget_exceeded", as the real orchestrator does when its cap is crossed.</summary>
    public HashSet<string> StoppedByBudget { get; } = new();
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(20);
    public ConcurrentQueue<(string Node, AgentOptions Options)> Calls { get; } = new();
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public UsageOrchestrator(Func<string, int, string>? answer = null) => _answer = answer ?? ((node, _) => $"{node} done");

    public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(string userMessage, string sessionId, AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var node = options.AgentId ?? "?";
        var call = _counts.AddOrUpdate(node, 1, (_, n) => n + 1);
        Calls.Enqueue((node, options));
        await Task.Delay(Delay, cancellationToken);
        yield return new AgentFinishedEvent(new AgentResponse
        {
            Content = _answer(node, call),
            TotalUsage = new TokenUsage { PromptTokens = PromptTokens, CompletionTokens = CompletionTokens },
            StopReason = StoppedByBudget.Contains(node) ? "budget_exceeded" : null
        });
    }

    public AgentOptions OptionsOf(string node, int call = 1) => Calls.Where(c => c.Node == node).ElementAt(call - 1).Options;
}

public class FlowCostAggregationTests
{
    internal const string Sonnet = "claude-sonnet-test";   // priced $3 in / $15 out per million
    internal const string Haiku = "claude-haiku-test";     // priced $1 in / $5 out per million

    internal static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);

    internal static decimal CostOf(string model, int prompt = 1000, int completion = 500) =>
        ModelPricing.Cost(model, new TokenUsage { PromptTokens = prompt, CompletionTokens = completion });

    internal static async Task<(List<AgentEvent> Events, FlowRunResult Result)> RunAsync(
        UsageOrchestrator orchestrator, SdlcPipelineDefinition flow, string? model = Sonnet, decimal? maxBudgetUsd = null)
    {
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, orchestrator, NullLogger<SdlcPipelineRunner>.Instance);
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(flow, "ship it", "s1", Path.GetTempPath(), model: model, maxBudgetUsd: maxBudgetUsd))
            events.Add(e);
        return (events, Assert.Single(events.OfType<FlowFinishedEvent>()).Result);
    }

    private static SdlcPipelineDefinition Diamond() => new()
    {
        Name = "diamond",
        Stages = new()
        {
            Stage("design", "planner"),
            Stage("backend") with { Model = Haiku },
            Stage("model"),
            Stage("review", "reviewer")
        },
        Edges = new() { Edge("design", "backend"), Edge("design", "model"), Edge("backend", "review"), Edge("model", "review") }
    };

    [Fact]
    public async Task PerStepCosts_AddUpToTheRunTotal()
    {
        var (events, result) = await RunAsync(new UsageOrchestrator(), Diamond());

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(4, result.StepRuns.Count);

        // design, model and review use the session model; backend asked for haiku.
        var expected = 3 * CostOf(Sonnet) + CostOf(Haiku);
        Assert.Equal(expected, result.StepRuns.Sum(r => r.CostUsd));
        Assert.Equal(expected, result.Usage.CostUsd);
        Assert.Equal(CostOf(Haiku), result.StepRuns.Single(r => r.NodeId == "backend").CostUsd);
        Assert.Equal(Haiku, result.StepRuns.Single(r => r.NodeId == "backend").Model);
        Assert.All(result.StepRuns.Where(r => r.NodeId != "backend"), r => Assert.Equal(Sonnet, r.Model));

        Assert.Equal(4 * 1000, result.Usage.TokensIn);
        Assert.Equal(4 * 500, result.Usage.TokensOut);
        Assert.False(result.Usage.HasUnpriced);
        Assert.All(result.StepRuns, r => Assert.True(r.Duration > TimeSpan.Zero));
    }

    [Fact]
    public async Task UsageEvents_CarryTheRunningTotal_AndTheLastOneMatchesTheResult()
    {
        var (events, result) = await RunAsync(new UsageOrchestrator(), Diamond(), maxBudgetUsd: 10m);

        var usage = events.OfType<FlowUsageEvent>().ToList();
        Assert.Equal(4, usage.Count);
        Assert.All(usage, u => Assert.Equal(10m, u.BudgetUsd));
        // The total only grows, and ends at the result's total.
        Assert.Equal(usage.OrderBy(u => u.Run.CostUsd).Select(u => u.Run.CostUsd), usage.Select(u => u.Run.CostUsd));
        Assert.Equal(result.Usage.CostUsd, usage.Last().Run.CostUsd);
        Assert.Equal(10m, result.BudgetUsd);
    }

    [Fact]
    public async Task LoopRounds_AreRowsOfTheirOwn_AndTheNodeTotalCoversAllRounds()
    {
        var flow = new SdlcPipelineDefinition
        {
            Name = "loop",
            Stages = new() { Stage("dev"), Stage("review", "reviewer", null, "approved", "rejected") },
            Edges = new() { Edge("dev", "review"), Edge("review", "dev", "rejected", maxLoops: 1) }
        };
        var orchestrator = new UsageOrchestrator((node, call) =>
            node == "review" ? (call == 1 ? "Needs work.\nOUTCOME: rejected" : "Good.\nOUTCOME: approved") : $"dev round {call}");

        var (events, result) = await RunAsync(orchestrator, flow);

        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal(new[] { ("dev", 1), ("review", 1), ("dev", 2), ("review", 2) }, result.StepRuns.Select(r => (r.NodeId, r.Round)).ToArray());
        Assert.Equal(4 * CostOf(Sonnet), result.Usage.CostUsd);

        var lastDev = events.OfType<FlowUsageEvent>().Last(u => u.NodeId == "dev");
        Assert.Equal(2, lastDev.Step.Round);
        Assert.Equal(2 * CostOf(Sonnet), lastDev.Node.CostUsd); // the node's badge covers both rounds
    }

    [Fact]
    public async Task ModelWithoutAKnownPrice_IsFlaggedUnpriced_AndCostsNothing()
    {
        var flow = new SdlcPipelineDefinition { Name = "local", Stages = new() { Stage("a"), Stage("b") }, Edges = new() { Edge("a", "b") } };

        var (_, result) = await RunAsync(new UsageOrchestrator(), flow, model: "my-local-model-without-a-price");

        Assert.All(result.StepRuns, r => Assert.True(r.Unpriced));
        Assert.Equal(0m, result.Usage.CostUsd);
        Assert.True(result.Usage.HasUnpriced);
        Assert.Contains("unpriced", FlowUsageFormat.Table(result));
    }

    [Fact]
    public async Task StepWithoutModelCalls_HasNoRow()
    {
        var flow = new SdlcPipelineDefinition { Name = "free", Stages = new() { Stage("a"), Stage("b") }, Edges = new() { Edge("a", "b") } };

        var (_, result) = await RunAsync(new UsageOrchestrator { PromptTokens = 0, CompletionTokens = 0 }, flow);

        Assert.Empty(result.StepRuns);
        Assert.Equal(string.Empty, FlowUsageFormat.Table(result));
    }

    // ------------------------------------------------------------ formatting

    [Theory]
    [InlineData(0, "0")]
    [InlineData(950, "950")]
    [InlineData(1200, "1.2k")]
    [InlineData(15_000, "15k")]
    [InlineData(3_400_000, "3.4M")]
    public void Tokens_AreShortened(int tokens, string expected) => Assert.Equal(expected, FlowUsageFormat.Tokens(tokens));

    [Theory]
    [InlineData(0, "$0")]
    [InlineData(0.004, "$0.004")]
    [InlineData(0.0042, "$0.0042")]
    [InlineData(1.2, "$1.20")]
    public void Cost_ShowsTheSmallestUsefulDigits(double usd, string expected) => Assert.Equal(expected, FlowUsageFormat.Cost((decimal)usd));

    [Fact]
    public void Badge_And_Header_ReadLikeTheStory()
    {
        var totals = new FlowUsageTotals(1000, 200, 0, 0, 0.004m, false);

        Assert.Equal("1.2k tok · $0.004", FlowUsageFormat.Badge(totals));
        Assert.Equal("Run: 1.2k tok · $0.004", FlowUsageFormat.Header(totals, null));
        Assert.Equal("Run: 1.2k tok · $0.004 of $1.00", FlowUsageFormat.Header(totals, 1m));
    }

    [Fact]
    public async Task TableAndJson_ListEveryStepRun_AndTheTotal()
    {
        var (_, result) = await RunAsync(new UsageOrchestrator(), Diamond());

        var table = FlowUsageFormat.Table(result);
        foreach (var id in new[] { "design", "backend", "model", "review", "total" }) Assert.Contains(id, table);
        Assert.Contains(Haiku, table);
        Assert.Contains(FlowUsageFormat.Cost(result.Usage.CostUsd), table);

        using var doc = JsonDocument.Parse(FlowUsageFormat.ToJson("diamond", result));
        var root = doc.RootElement;
        Assert.Equal("succeeded", root.GetProperty("status").GetString());
        Assert.Equal(4, root.GetProperty("steps").GetArrayLength());
        Assert.Equal(result.Usage.CostUsd, root.GetProperty("totals").GetProperty("costUsd").GetDecimal());
        Assert.Equal(result.StepRuns.Sum(r => r.CostUsd),
            root.GetProperty("steps").EnumerateArray().Sum(s => s.GetProperty("costUsd").GetDecimal()));
    }

    [Fact]
    public void StatusName_UsesSnakeCaseForBudgetExceeded()
    {
        Assert.Equal("budget_exceeded", FlowUsageFormat.StatusName(FlowRunStatus.BudgetExceeded));
        Assert.Equal("succeeded", FlowUsageFormat.StatusName(FlowRunStatus.Succeeded));
    }
}
