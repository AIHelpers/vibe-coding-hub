using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.Board;

/// <summary>E11-S1: the adapter that hands one session's events from the shared bus to the board.</summary>
public class AgentServiceBoardRunnerTests
{
    private static ToolCall Call(string name) => new() { Name = name };

    private static async Task<List<AgentEvent>> CollectAsync(AgentServiceBoardRunner runner, string session, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync("task", session, new AgentOptions(), ct)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task OnlyTheEventsOfItsOwnSession_ComeBack_UnwrappedAndInOrder_UntilTheRunFinishes()
    {
        using var bus = new AgentEventBus();
        var runner = new AgentServiceBoardRunner(bus, (_, session, _, _) =>
        {
            // Everything is published at once, before the reader asks for its first event: nothing may be lost.
            bus.Publish(new SessionScopedEvent(session, new ToolCallStartEvent(Call("edit_file"))));
            bus.Publish(new SessionScopedEvent("someone-else", new ToolCallStartEvent(Call("other"))));
            bus.Publish(new SessionScopedEvent(session, new TextDeltaEvent("hi")));
            bus.Publish(new SessionScopedEvent(session, new AgentFinishedEvent(new AgentResponse { Content = "done" })));
            bus.Publish(new SessionScopedEvent(session, new TextDeltaEvent("after the end")));
            return Task.CompletedTask;
        });

        var events = await CollectAsync(runner, "card-1");

        Assert.Equal("edit_file", Assert.IsType<ToolCallStartEvent>(events[0]).Call.Name);
        Assert.Equal("hi", Assert.IsType<TextDeltaEvent>(events[1]).Delta);
        Assert.Equal("done", Assert.IsType<AgentFinishedEvent>(events[2]).Response.Content);
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task AnErrorEvent_EndsTheStream()
    {
        using var bus = new AgentEventBus();
        var runner = new AgentServiceBoardRunner(bus, (_, session, _, _) =>
        {
            bus.Publish(new SessionScopedEvent(session, new AgentErrorEvent(new InvalidOperationException("boom"))));
            return Task.CompletedTask;
        });

        var events = await CollectAsync(runner, "card-1");

        Assert.Equal("boom", Assert.IsType<AgentErrorEvent>(Assert.Single(events)).Error.Message);
    }

    [Fact]
    public async Task Cancelling_StopsTheAgent_AndTheCancelledFinishArrives()
    {
        using var bus = new AgentEventBus();
        var runner = new AgentServiceBoardRunner(bus, async (_, session, _, ct) =>
        {
            bus.Publish(new SessionScopedEvent(session, new ToolCallStartEvent(Call("edit_file"))));
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                bus.Publish(new SessionScopedEvent(session, new AgentFinishedEvent(new AgentResponse { WasCancelled = true })));
            }
        });
        using var cts = new CancellationTokenSource();

        var collecting = CollectAsync(runner, "card-1", cts.Token);
        cts.CancelAfter(100);
        var events = await collecting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<ToolCallStartEvent>(events[0]);
        Assert.True(Assert.IsType<AgentFinishedEvent>(events[^1]).Response.WasCancelled);
    }

    [Fact]
    public async Task ARunThatEndsWithoutAClosingEvent_DoesNotHangTheBoard()
    {
        using var bus = new AgentEventBus();
        var runner = new AgentServiceBoardRunner(bus, (_, session, _, _) =>
        {
            bus.Publish(new SessionScopedEvent(session, new TextDeltaEvent("only text")));
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(100));

        var events = await CollectAsync(runner, "card-1").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("only text", Assert.IsType<TextDeltaEvent>(Assert.Single(events)).Delta);
    }

    [Fact]
    public async Task APromptAndOptions_ArePassedToTheAgentService()
    {
        using var bus = new AgentEventBus();
        (string Prompt, string Session, string? Dir)? seen = null;
        var runner = new AgentServiceBoardRunner(bus, (prompt, session, options, _) =>
        {
            seen = (prompt, session, options.WorkingDirectory);
            bus.Publish(new SessionScopedEvent(session, new AgentFinishedEvent(new AgentResponse())));
            return Task.CompletedTask;
        });

        await foreach (var _ in runner.RunAsync("fix it", "card-9", new AgentOptions { WorkingDirectory = "/wt/9" }, CancellationToken.None)) { }

        Assert.Equal(("fix it", "card-9", "/wt/9"), seen);
    }
}
