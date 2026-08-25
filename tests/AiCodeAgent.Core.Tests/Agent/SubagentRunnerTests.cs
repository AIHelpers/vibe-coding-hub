using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiCodeAgent.Core.Tests.Agent;

public class SubagentRunnerTests
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IContextManager _contextManager;
    private readonly IAgentEventBus _eventBus;

    public SubagentRunnerTests()
    {
        _orchestrator = Substitute.For<IAgentOrchestrator>();
        _contextManager = Substitute.For<IContextManager>();
        _eventBus = Substitute.For<IAgentEventBus>();
    }

    private static SubagentContext Context(bool withParent = false) => new()
    {
        ParentSessionId = withParent ? "parent-1" : string.Empty,
        WorkingDirectory = @"C:\work",
        Model = "gpt-4o-mini",
        MaxIterations = 5,
        MaxTokens = 4096,
        Timeout = TimeSpan.FromSeconds(30),
        EnabledTools = new() { "read_file", "search_files" },
        Role = "researcher",
    };

    private static AgentResponse Response(string content = "done", int toolCalls = 0) => new()
    {
        Content = content,
        ToolExecutions = Enumerable.Repeat(new ToolExecution(), toolCalls).ToList(),
        TotalUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 20 },
        Duration = TimeSpan.FromMilliseconds(150),
        WasCancelled = false,
    };

    [Fact]
    public async Task RunAsync_SpawnsFreshContext_ReturnsSummary()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("Research complete", 3));

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        var summary = await runner.RunAsync("Find usages of Foo", Context());

        Assert.Equal("Research complete", summary.Content);
        Assert.Equal(3, summary.ToolCallCount);
        Assert.False(summary.Forked);
        Assert.Equal(30, summary.Usage.TotalTokens);
        Assert.False(summary.WasCancelled);
        Assert.Null(summary.Error);
    }

    [Fact]
    public async Task RunAsync_DoesNotCopyParentMessages()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        await runner.RunAsync("task", Context(withParent: true));

        await _contextManager.DidNotReceive().GetContextAsync(Arg.Any<string>());
        await _contextManager.DidNotReceive().AddMessageAsync(Arg.Any<string>(), Arg.Any<Message>());
    }

    [Fact]
    public async Task RunAsync_PublishesStartedAndFinishedEvents()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        await runner.RunAsync("task", Context());

        _eventBus.Received(1).Publish(Arg.Is<SubagentStartedEvent>(e => !e.Forked));
        _eventBus.Received(1).Publish(Arg.Any<SubagentFinishedEvent>());
    }

    [Fact]
    public async Task RunAsync_BuildsOptionsFromContext()
    {
        AgentOptions? captured = null;
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<AgentOptions>(o => captured = o), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);
        var ctx = Context();

        await runner.RunAsync("task", ctx);

        Assert.NotNull(captured);
        Assert.Equal(ctx.Model, captured!.Model);
        Assert.Equal(ctx.WorkingDirectory, captured.WorkingDirectory);
        Assert.Equal(ctx.MaxIterations, captured.MaxIterations);
        Assert.Equal(ctx.MaxTokens, captured.MaxTokens);
        Assert.True(captured.AutoApprove);
        Assert.Equal(ctx.EnabledTools, captured.EnabledTools);
        Assert.Equal(ctx.Role, captured.Role);
    }

    [Fact]
    public async Task RunAsync_CleansUpSubagentContext_AfterRun()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        await runner.RunAsync("task", Context());

        await _contextManager.Received(1).ClearAsync(Arg.Is<string>(id => id.StartsWith("sub_")));
    }

    [Fact]
    public async Task RunAsync_WhenOrchestratorThrows_ReturnsErrorSummary()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        var summary = await runner.RunAsync("task", Context());

        Assert.Contains("Subagent error", summary.Content);
        Assert.Contains("boom", summary.Content);
        Assert.Equal("InvalidOperationException", summary.Error);
        Assert.False(summary.WasCancelled);
    }

    [Fact]
    public async Task ForkAsync_CopiesParentMessages_IntoSubagentSession()
    {
        var parentMessages = new List<Message>
        {
            new() { Role = MessageRole.User, Content = "Hello" },
            new() { Role = MessageRole.Assistant, Content = "Hi there" },
        };

        _contextManager.GetContextAsync("parent-1").Returns(parentMessages);
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("forked result"));

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        var summary = await runner.ForkAsync("Continue task", Context(withParent: true));

        Assert.True(summary.Forked);
        Assert.Equal("forked result", summary.Content);

        await _contextManager.Received(1).GetContextAsync("parent-1");
        await _contextManager.Received(2).AddMessageAsync(Arg.Is<string>(id => id.StartsWith("sub_")), Arg.Any<Message>());
    }

    [Fact]
    public async Task ForkAsync_PublishesStartedEvent_WithForkedTrue()
    {
        _contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        await runner.ForkAsync("task", Context(withParent: true));

        _eventBus.Received(1).Publish(Arg.Is<SubagentStartedEvent>(e => e.Forked));
    }

    [Fact]
    public async Task ForkAsync_WhenParentContextUnavailable_FallsBackToFresh()
    {
        _contextManager.GetContextAsync("parent-1").Throws(new InvalidOperationException("no session"));
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("fresh result"));

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        var summary = await runner.ForkAsync("task", Context(withParent: true));

        Assert.True(summary.Forked);
        Assert.Equal("fresh result", summary.Content);
    }

    [Fact]
    public async Task ForkAsync_WithEmptyParentSessionId_DoesNotCopyMessages()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        await runner.ForkAsync("task", Context(withParent: false));

        await _contextManager.DidNotReceive().GetContextAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsCancelledSummaryWithTimeoutError()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                // Simulate the linked CTS timing out
                var ct = c.ArgAt<CancellationToken>(3);
                while (!ct.IsCancellationRequested)
                    Thread.Sleep(10);
                ct.ThrowIfCancellationRequested();
                return Response();
            });

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        var ctx = Context() with { Timeout = TimeSpan.FromMilliseconds(100) };

        var summary = await runner.RunAsync("task", ctx);

        Assert.True(summary.WasCancelled);
        Assert.Equal("timeout", summary.Error);
        Assert.Contains("timed out", summary.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_PropagatesAndPublishesEvent()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var ct = c.ArgAt<CancellationToken>(3);
                ct.ThrowIfCancellationRequested();
                return Response();
            });

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunAsync("task", Context(), cts.Token));

        _eventBus.Received(1).Publish(Arg.Is<SubagentFinishedEvent>(e => e.Summary.WasCancelled));
    }

    [Fact]
    public async Task RunAsync_GeneratesUniqueSubagentId_WithSubPrefix()
    {
        string? capturedId = null;
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Do<string>(id => capturedId = id), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, _eventBus);

        await runner.RunAsync("task", Context());

        Assert.NotNull(capturedId);
        Assert.StartsWith("sub_", capturedId);
    }

    [Fact]
    public async Task RunAsync_WithoutEventBus_DoesNotThrow()
    {
        _orchestrator
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response());

        var runner = new SubagentRunner(_orchestrator, _contextManager, eventBus: null);

        var summary = await runner.RunAsync("task", Context());

        Assert.Equal("done", summary.Content);
    }

    [Fact]
    public void SubagentSummary_ToPromptBlock_IncludesRelevantFields()
    {
        var summary = new SubagentSummary
        {
            SubagentId = "sub_abc",
            Task = "find usages",
            Forked = true,
            Content = "Found 3 usages.",
            ToolCallCount = 5,
            Usage = new TokenUsage { PromptTokens = 60, CompletionTokens = 40 },
            Duration = TimeSpan.FromSeconds(2.5),
            WasCancelled = false,
            Error = null,
        };

        var block = summary.ToPromptBlock();

        Assert.Contains("sub_abc", block);
        Assert.Contains("find usages", block);
        Assert.Contains("forked", block);
        Assert.Contains("Found 3 usages.", block);
        Assert.Contains("completed", block);
    }

    [Fact]
    public void SubagentSummary_ToPromptBlock_WithCancelled_ShowsCancelledStatus()
    {
        var summary = new SubagentSummary
        {
            SubagentId = "sub_abc",
            Task = "task",
            Content = "partial",
            WasCancelled = true,
        };

        var block = summary.ToPromptBlock();

        Assert.Contains("cancelled", block);
    }

    [Fact]
    public void SubagentSummary_ToPromptBlock_WithError_ShowsErrorStatus()
    {
        var summary = new SubagentSummary
        {
            SubagentId = "sub_abc",
            Task = "task",
            Content = "failed",
            Error = "InvalidOperationException",
        };

        var block = summary.ToPromptBlock();

        Assert.Contains("error", block);
        Assert.Contains("InvalidOperationException", block);
    }
}