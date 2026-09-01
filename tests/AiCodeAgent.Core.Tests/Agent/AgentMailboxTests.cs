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

public class AgentMailboxTests
{
    [Fact]
    public void Post_And_Drain_DeliversDirectMessage()
    {
        var mailbox = new AgentMailbox();
        mailbox.EnsureAgent("receiver");

        mailbox.Post("sender", "receiver", "hello");

        var drained = mailbox.Drain("receiver");
        var message = Assert.Single(drained);
        Assert.Equal("sender", message.FromAgentId);
        Assert.Equal("receiver", message.ToAgentId);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public void Drain_RemovesMessages_FromMailbox()
    {
        var mailbox = new AgentMailbox();
        mailbox.EnsureAgent("receiver");
        mailbox.Post("sender", "receiver", "hello");

        mailbox.Drain("receiver");

        Assert.Empty(mailbox.Drain("receiver"));
    }

    [Fact]
    public void Post_Broadcast_ReachesAllRegisteredAgents()
    {
        var mailbox = new AgentMailbox();
        mailbox.EnsureAgent("a1");
        mailbox.EnsureAgent("a2");

        mailbox.Post("sender", AgentMailbox.BroadcastId, "announcement");

        var m1 = Assert.Single(mailbox.Drain("a1"));
        var m2 = Assert.Single(mailbox.Drain("a2"));
        Assert.True(m1.IsBroadcast);
        Assert.True(m2.IsBroadcast);
        Assert.Equal("announcement", m1.Content);
    }

    [Fact]
    public void Post_BroadcastBeforeRegistration_IsReplayedOnRegistration()
    {
        var mailbox = new AgentMailbox();
        mailbox.Post("sender", "*", "early");

        mailbox.EnsureAgent("late-agent");

        var message = Assert.Single(mailbox.Drain("late-agent"));
        Assert.Equal("early", message.Content);
    }

    [Fact]
    public void Post_UnknownRecipient_QueuedUntilDrained()
    {
        var mailbox = new AgentMailbox();

        mailbox.Post("sender", "stranger", "ping");

        Assert.Equal(1, mailbox.PendingCount);
        var message = Assert.Single(mailbox.Drain("stranger"));
        Assert.Equal("ping", message.Content);
    }

    [Fact]
    public void Peek_DoesNotRemoveMessages()
    {
        var mailbox = new AgentMailbox();
        mailbox.EnsureAgent("a1");
        mailbox.Post("s", "a1", "m");

        Assert.Single(mailbox.Peek("a1"));
        Assert.Single(mailbox.Peek("a1"));
        mailbox.Drain("a1");
        Assert.Empty(mailbox.Peek("a1"));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var mailbox = new AgentMailbox();
        mailbox.EnsureAgent("a1");
        mailbox.EnsureAgent("a2");
        mailbox.Post("s", "*", "bcast");
        mailbox.Post("s", "a1", "direct");

        mailbox.Clear();

        Assert.Equal(0, mailbox.PendingCount);
        Assert.Empty(mailbox.Drain("a1"));
        Assert.Empty(mailbox.Drain("a2"));
    }

    [Fact]
    public void PendingCount_SumsAcrossAgents()
    {
        var mailbox = new AgentMailbox();
        mailbox.EnsureAgent("a1");
        mailbox.EnsureAgent("a2");
        mailbox.Post("s", "a1", "m1");
        mailbox.Post("s", "a1", "m2");
        mailbox.Post("s", "a2", "m3");

        Assert.Equal(3, mailbox.PendingCount);
    }
}

public class MultiAgentCoordinatorTests
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

    private static async Task<List<AgentEvent>> CollectAsync(
        AgentSessionCoordinator coordinator, SessionPlan plan)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in coordinator.RunAsync(plan))
            events.Add(evt);
        return events;
    }

    [Fact]
    public async Task RunAsync_ParallelGroup_RunsBothAgentsAndEmitsGroupEvents()
    {
        var coordinator = CreateCoordinator();
        var a = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse { Content = "A" }));
        var b = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse { Content = "B" }));
        coordinator.RegisterAgent("agent-a", a);
        coordinator.RegisterAgent("agent-b", b);

        var plan = new SessionPlan
        {
            SessionId = "s1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "agent-a", Role = "impl", Prompt = "Do A", Options = new AgentOptions(), ParallelGroup = "g1" },
                new() { AgentId = "agent-b", Role = "impl", Prompt = "Do B", Options = new AgentOptions(), ParallelGroup = "g1" },
            }
        };

        var events = await CollectAsync(coordinator, plan);

        var started = events.OfType<ParallelGroupStartedEvent>().Single();
        Assert.Equal("g1", started.GroupName);
        Assert.Equal(2, started.AgentIds.Count);

        var finished = events.OfType<ParallelGroupFinishedEvent>().Single();
        Assert.Equal("g1", finished.GroupName);

        var tagged = events.OfType<AgentTaggedEvent>().ToList();
        Assert.Equal(2, tagged.Count);
        Assert.Contains(tagged, t => t.AgentId == "agent-a");
        Assert.Contains(tagged, t => t.AgentId == "agent-b");
    }

    [Fact]
    public async Task RunAsync_MixedPlan_GroupStepsDoNotBlockSequentialSteps()
    {
        var coordinator = CreateCoordinator();
        var solo = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse()));
        var a = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse()));
        var b = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse()));
        coordinator.RegisterAgent("solo", solo);
        coordinator.RegisterAgent("agent-a", a);
        coordinator.RegisterAgent("agent-b", b);

        var plan = new SessionPlan
        {
            SessionId = "s1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "solo", Role = "plan", Prompt = "P", Options = new AgentOptions() },
                new() { AgentId = "agent-a", Role = "impl", Prompt = "A", Options = new AgentOptions(), ParallelGroup = "g1" },
                new() { AgentId = "agent-b", Role = "impl", Prompt = "B", Options = new AgentOptions(), ParallelGroup = "g1" },
                new() { AgentId = "solo", Role = "review", Prompt = "R", Options = new AgentOptions() },
            }
        };

        var events = await CollectAsync(coordinator, plan);

        Assert.Equal(2, events.OfType<ParallelGroupStartedEvent>().Count() + events.OfType<ParallelGroupFinishedEvent>().Count());
        var tagged = events.OfType<AgentTaggedEvent>().ToList();
        Assert.Equal(4, tagged.Count);

        Assert.Equal("solo", tagged[0].AgentId);
        Assert.Equal("solo", tagged[^1].AgentId);
    }

    [Fact]
    public async Task RunAsync_MailboxMessage_IsInjectedIntoRecipientPrompt()
    {
        var coordinator = CreateCoordinator();
        string? capturedPrompt = null;
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.StreamRunAsync(
                Arg.Do<string>(p => capturedPrompt = p),
                Arg.Any<string>(),
                Arg.Any<AgentOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new AgentEvent[] { new AgentFinishedEvent(new AgentResponse()) }));
        coordinator.RegisterAgent("reviewer", orchestrator);

        coordinator.Mailbox.Post("planner", "reviewer", "use the dark theme");

        var plan = new SessionPlan
        {
            SessionId = "s1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "reviewer", Role = "reviewer", Prompt = "Review the PR", Options = new AgentOptions() }
            }
        };

        await CollectAsync(coordinator, plan);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Review the PR", capturedPrompt);
        Assert.Contains("Messages from other agents", capturedPrompt);
        Assert.Contains("use the dark theme", capturedPrompt);
        Assert.Contains("From planner", capturedPrompt);
    }

    [Fact]
    public async Task RunAsync_AgentFailuresInsideGroup_DoNotFailTheSession()
    {
        var coordinator = CreateCoordinator();
        var failing = Substitute.For<IAgentOrchestrator>();
        failing.StreamRunAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AgentOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"));
        var healthy = CreateOrchestrator(new AgentFinishedEvent(new AgentResponse()));
        coordinator.RegisterAgent("failing", failing);
        coordinator.RegisterAgent("healthy", healthy);

        var plan = new SessionPlan
        {
            SessionId = "s1",
            Steps = new List<SessionStep>
            {
                new() { AgentId = "failing", Role = "impl", Prompt = "x", Options = new AgentOptions(), ParallelGroup = "g" },
                new() { AgentId = "healthy", Role = "impl", Prompt = "y", Options = new AgentOptions(), ParallelGroup = "g" },
            }
        };

        var events = await CollectAsync(coordinator, plan);

        Assert.Single(events.OfType<ParallelGroupFinishedEvent>());
        Assert.Contains(events.OfType<AgentTaggedEvent>(), t => t.AgentId == "healthy");
        var errors = events.OfType<AgentTaggedEvent>()
            .Where(t => t.Inner is AgentErrorEvent)
            .ToList();
        Assert.Single(errors);
        Assert.Equal("failing", errors[0].AgentId);
    }

    [Fact]
    public void Reset_ClearsMailbox()
    {
        var coordinator = CreateCoordinator();
        coordinator.Mailbox.Post("a", "b", "m");

        coordinator.Reset();

        Assert.Equal(0, coordinator.Mailbox.PendingCount);
    }
}