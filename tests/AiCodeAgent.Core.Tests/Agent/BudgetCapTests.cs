using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Usage;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

/// <summary>
/// E4-S5: the spend cap from Settings → Cost. A fake provider reports usage with every response and asks for a tool, so the
/// loop would go on until the step limit if the budget did not stop it.
/// </summary>
[Collection("ModelPricing")]
public class BudgetCapTests
{
    // 1000 prompt + 1000 completion tokens at $10 per million each = $0.02 per response, twice the cap of one cent.
    private const string PricedModel = "budget-test-model";
    private const string UnpricedModel = "budget-test-local-model";

    private sealed class CountingProvider : IAiProvider
    {
        public int Calls { get; private set; }
        public string Name => "Fake";
        public string[] SupportedModels => [PricedModel];

        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return OneResponse();
        }

        private static async IAsyncEnumerable<StreamChunk> OneResponse()
        {
            yield return new StreamChunk
            {
                IsFinished = true,
                ToolCalls = new() { new ToolCall { Id = "call1", Name = "test", Arguments = new() } },
                Usage = new TokenUsage { PromptTokens = 1000, CompletionTokens = 1000 }
            };
            await Task.CompletedTask;
        }

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(SupportedModels);
    }

    private static (AgentOrchestrator Orchestrator, CountingProvider Provider) Build(AgentConfiguration config, Action? onToolRun = null)
    {
        ModelPricing.SetOverridesForTests(new() { [PricedModel] = new ModelPrice(10m, 10m) });

        var provider = new CountingProvider();
        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("test");
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>()).Returns(_ =>
        {
            onToolRun?.Invoke();
            return new ToolResult { Content = "ok" };
        });
        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("test").Returns(tool);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var orchestrator = new AgentOrchestrator(
            provider, contextManager, toolRegistry, config,
            Substitute.For<ILogger<AgentOrchestrator>>(),
            Substitute.For<IPermissionService>(),
            Substitute.For<ICheckpointManager>(),
            costTracker: new SessionCostTracker());
        return (orchestrator, provider);
    }

    private static async Task<List<AgentEvent>> Run(AgentOrchestrator orchestrator, AgentOptions options)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in orchestrator.StreamRunAsync("build it", "s1", options))
            events.Add(e);
        return events;
    }

    private static AgentResponse Finished(List<AgentEvent> events) =>
        events.OfType<AgentFinishedEvent>().Single().Response;

    [Fact]
    public async Task CapOfOneCent_StopsTheTask_AndSaysBudgetExceeded()
    {
        var (orchestrator, provider) = Build(new AgentConfiguration());

        var events = await Run(orchestrator, new AgentOptions { Model = PricedModel, MaxBudgetUsd = 0.01m, MaxIterations = 10 });

        Assert.Equal(1, provider.Calls);   // $0.02 spent by the first response; the second model call never happens
        var response = Finished(events);
        Assert.Equal("budget_exceeded", response.StopReason);
        Assert.Contains("budget exceeded", response.Content);
        Assert.Contains("$0.01", response.Content);

        var limit = events.OfType<StatusUpdateEvent>().Single(s => s.Status == "Limit");
        Assert.Contains("budget exceeded", limit.Detail);
    }

    [Fact]
    public async Task CapFromSettings_AppliesWithoutAPerRunOption()
    {
        var config = new AgentConfiguration { MaxBudgetUsd = 0.01m };
        var (orchestrator, provider) = Build(config);

        var events = await Run(orchestrator, new AgentOptions { Model = PricedModel, MaxIterations = 10 });

        Assert.Equal(1, provider.Calls);
        Assert.Equal("budget_exceeded", Finished(events).StopReason);
    }

    [Fact]
    public async Task CapChangedWhileTheTaskRuns_StopsItBeforeTheNextModelCall()
    {
        var config = new AgentConfiguration();   // no cap when the task starts
        var (orchestrator, provider) = Build(config, onToolRun: () => config.MaxBudgetUsd = 0.01m);

        var events = await Run(orchestrator, new AgentOptions { Model = PricedModel, MaxIterations = 10 });

        Assert.Equal(1, provider.Calls);
        Assert.Equal("budget_exceeded", Finished(events).StopReason);
    }

    [Fact]
    public async Task WithoutACap_TheTaskRunsUntilTheStepLimit()
    {
        var (orchestrator, provider) = Build(new AgentConfiguration());

        var events = await Run(orchestrator, new AgentOptions { Model = PricedModel, MaxIterations = 3 });

        Assert.Equal(3, provider.Calls);
        Assert.Equal("max_iterations", Finished(events).StopReason);
    }

    [Fact]
    public async Task AModelWithoutAPrice_CostsNothing_SoTheCapNeverStopsIt()
    {
        var (orchestrator, provider) = Build(new AgentConfiguration { MaxBudgetUsd = 0.01m });

        var events = await Run(orchestrator, new AgentOptions { Model = UnpricedModel, MaxIterations = 3 });

        Assert.Equal(3, provider.Calls);
        Assert.Equal("max_iterations", Finished(events).StopReason);
    }
}
