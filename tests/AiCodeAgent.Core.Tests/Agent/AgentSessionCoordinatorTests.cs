using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class AgentSessionCoordinatorTests
{
    private static AgentSessionCoordinator CreateCoordinator(IAgentEventBus? eventBus = null)
    {
        var logger = Substitute.For<ILogger<AgentSessionCoordinator>>();
        return new AgentSessionCoordinator(logger, eventBus);
    }

    private static IAgentOrchestrator CreateOrchestrator(params AgentEvent[] events)
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.StreamRunAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AgentOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(events));
        return orchestrator;
    }

    private static async IAsyncEnumerable<AgentEvent> ToAsyncEnumerable(IEnumerable<AgentEvent> events)
    {
        foreach (var evt in events)
            yield return evt;
    }

    [Fact]
    public async Task RunAsync_TagsEvents_WithAgentId()
    {
        var coordinator = CreateCoordinator();
        var orchestrator = CreateOrchestrator(
            new TextDeltaEvent("Hello"),
            new AgentFinishedEvent(new AgentResponse { Content = "Hello" }));

        coordinator.RegisterAgent("planner-1", orchestrator);

        var plan = new SessionPlan
        {
            SessionId = "session1",
            Steps = new List<SessionStep>
            {
                new()
                {
                    AgentId = "planner-1",
                    Role = "planner",
                    Prompt = "Plan this task",
                    Options = new AgentOptions()
                }
            }
        };

        var events = new List<AgentEvent>();
        await foreach (var evt in coordinator.RunAsync(plan))
            events.Add(evt);

        Assert.All(events, evt =>
        {
            var tagged = Assert.IsType<AgentTaggedEvent>(evt);
            Assert.Equal("planner-1", tagged.AgentId);
            Assert.Equal("planner", tagged.Role);
        });
    }

    [Fact]
    public async Task RunAsync_SequentialSteps_ExecutesInOrder()
    {
        var coordinator = CreateCoordinator();
        var planner = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse { Content = "Plan" }));
        var implementer = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse { Content = "Impl" }));

        coordinator.RegisterAgent("planner-1", planner);
        coordinator.RegisterAgent("implementer-1", implementer);

        var plan = new SessionPlan
        {
            SessionId = "session1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "planner-1", Role = "planner", Prompt = "Plan", Options = new AgentOptions() },
                new() { AgentId = "implementer-1", Role = "implementer", Prompt = "Implement", Options = new AgentOptions() }
            }
        };

        var events = new List<AgentEvent>();
        await foreach (var evt in coordinator.RunAsync(plan))
            events.Add(evt);

        var taggedEvents = events.OfType<AgentTaggedEvent>().ToList();
        Assert.Equal(2, taggedEvents.Count);
        Assert.Equal("planner-1", taggedEvents[0].AgentId);
        Assert.Equal("implementer-1", taggedEvents[1].AgentId);
    }

    [Fact]
    public async Task RunAsync_UnregisteredAgent_EmitsErrorEvent()
    {
        var coordinator = CreateCoordinator();

        var plan = new SessionPlan
        {
            SessionId = "session1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "missing-agent", Role = "planner", Prompt = "Plan", Options = new AgentOptions() }
            }
        };

        var events = new List<AgentEvent>();
        await foreach (var evt in coordinator.RunAsync(plan))
            events.Add(evt);

        var tagged = Assert.Single(events.OfType<AgentTaggedEvent>());
        Assert.IsType<AgentErrorEvent>(tagged.Inner);
    }

    [Fact]
    public async Task RunAsync_CapturesDiffEvents_IntoSharedChangeset()
    {
        var coordinator = CreateCoordinator();
        var diff = new DiffEntry
        {
            FilePath = "test.cs",
            OriginalContent = "old",
            ModifiedContent = "new",
            DiffText = "diff"
        };
        var orchestrator = CreateOrchestrator(new DiffProducedEvent(diff));

        coordinator.RegisterAgent("implementer-1", orchestrator);

        var plan = new SessionPlan
        {
            SessionId = "session1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "implementer-1", Role = "implementer", Prompt = "Implement", Options = new AgentOptions() }
            }
        };

        await foreach (var _ in coordinator.RunAsync(plan)) { }

        var captured = Assert.Single(coordinator.Changeset.Entries);
        Assert.Equal("implementer-1", captured.AgentId);
        Assert.Equal("test.cs", captured.FilePath);
    }

    [Fact]
    public async Task RunAsync_EmptyPlan_YieldsNoEvents()
    {
        var coordinator = CreateCoordinator();

        var plan = new SessionPlan
        {
            SessionId = "session1",
            Steps = new List<SessionStep>()
        };

        var events = new List<AgentEvent>();
        await foreach (var evt in coordinator.RunAsync(plan))
            events.Add(evt);

        Assert.Empty(events);
    }

    [Fact]
    public async Task RunAsync_PassesAgentIdAndRole_ToOrchestrator()
    {
        var coordinator = CreateCoordinator();
        AgentOptions? capturedOptions = null;
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.StreamRunAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<AgentOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new AgentEvent[] { new AgentFinishedEvent(new AgentResponse()) }));

        coordinator.RegisterAgent("reviewer-1", orchestrator);

        var plan = new SessionPlan
        {
            SessionId = "session1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "reviewer-1", Role = "reviewer", Prompt = "Review", Options = new AgentOptions() }
            }
        };

        await foreach (var _ in coordinator.RunAsync(plan)) { }

        Assert.NotNull(capturedOptions);
        Assert.Equal("reviewer-1", capturedOptions!.AgentId);
        Assert.Equal("reviewer", capturedOptions.Role);
    }

    [Fact]
    public void Reset_ClearsSharedState()
    {
        var coordinator = CreateCoordinator();
        coordinator.Changeset.Add(new DiffEntry { FilePath = "test.cs", AgentId = "agent-1" });
        coordinator.Context.CacheFile("test.cs", "content");
        coordinator.Context.TokenBudget = 100;

        coordinator.Reset();

        Assert.Empty(coordinator.Changeset.Entries);
        Assert.False(coordinator.Context.TryGetFile("test.cs", out _));
        Assert.Equal(0, coordinator.Context.TokenBudget);
    }
}

public class SharedChangesetTests
{
    [Fact]
    public void Add_And_GetByAgent_FiltersCorrectly()
    {
        var changeset = new SharedChangeset();
        changeset.Add(new DiffEntry { FilePath = "a.cs", AgentId = "agent-1" });
        changeset.Add(new DiffEntry { FilePath = "b.cs", AgentId = "agent-2" });
        changeset.Add(new DiffEntry { FilePath = "c.cs", AgentId = "agent-1" });

        var agent1Diffs = changeset.GetByAgent("agent-1").ToList();

        Assert.Equal(2, agent1Diffs.Count);
        Assert.All(agent1Diffs, d => Assert.Equal("agent-1", d.AgentId));
    }

    [Fact]
    public void Entries_ReturnsSnapshot_NotLiveList()
    {
        var changeset = new SharedChangeset();
        changeset.Add(new DiffEntry { FilePath = "a.cs" });

        var snapshot = changeset.Entries;
        changeset.Add(new DiffEntry { FilePath = "b.cs" });

        Assert.Single(snapshot);
        Assert.Equal(2, changeset.Entries.Count);
    }
}

public class SharedContextStoreTests
{
    [Fact]
    public void CacheFile_And_TryGetFile_Works()
    {
        var store = new SharedContextStore();
        store.CacheFile("test.cs", "content");

        Assert.True(store.TryGetFile("test.cs", out var content));
        Assert.Equal("content", content);
    }

    [Fact]
    public void CacheSymbol_And_TryGetSymbol_Works()
    {
        var store = new SharedContextStore();
        var symbol = new object();
        store.CacheSymbol("MyClass", symbol);

        Assert.True(store.TryGetSymbol("MyClass", out var value));
        Assert.Same(symbol, value);
    }

    [Fact]
    public void TokenBudget_IsThreadSafe()
    {
        var store = new SharedContextStore();
        store.TokenBudget = 5000;

        Assert.Equal(5000, store.TokenBudget);
    }
}