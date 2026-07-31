using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class AgentOrchestratorTests
{
    private class FakeProvider : IAiProvider
    {
        public string Name => "Fake";
        public string[] SupportedModels => ["fake-model"];
        private readonly Func<CompletionRequest, CancellationToken, IAsyncEnumerable<StreamChunk>> _streamFactory;
        public FakeProvider(Func<CompletionRequest, CancellationToken, IAsyncEnumerable<StreamChunk>> streamFactory) => _streamFactory = streamFactory;
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => _streamFactory(request, cancellationToken);
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(SupportedModels);
    }

    private static AgentOrchestrator CreateOrchestrator(
        IAiProvider? provider = null,
        IContextManager? contextManager = null,
        IToolRegistry? toolRegistry = null,
        AgentConfiguration? config = null,
        IPermissionService? permissionService = null,
        ICheckpointManager? checkpointManager = null,
        IAgentEventBus? eventBus = null)
    {
        provider ??= Substitute.For<IAiProvider>();
        contextManager ??= Substitute.For<IContextManager>();
        toolRegistry ??= Substitute.For<IToolRegistry>();
        config ??= new AgentConfiguration();
        permissionService ??= Substitute.For<IPermissionService>();
        checkpointManager ??= Substitute.For<ICheckpointManager>();
        var logger = Substitute.For<ILogger<AgentOrchestrator>>();

        return new AgentOrchestrator(provider, contextManager, toolRegistry, config, logger, permissionService, checkpointManager, eventBus);
    }

    private static async IAsyncEnumerable<StreamChunk> CreateStream(params StreamChunk[] chunks)
    {
        foreach (var chunk in chunks)
            yield return chunk;
    }

    private static async IAsyncEnumerable<StreamChunk> CreateEmptyStream()
    {
        yield return new StreamChunk { Delta = "", IsFinished = true };
    }

    [Fact]
    public async Task RunAsync_ReturnsResponse_WithContent()
    {
        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { Delta = "Hello", IsFinished = true }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager);

        var response = await orchestrator.RunAsync("Hi", "session1", new AgentOptions());

        Assert.Equal("Hello", response.Content);
    }

    [Fact]
    public async Task RunAsync_AddsUserMessage_ToContext()
    {
        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { Delta = "Hi", IsFinished = true }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager);

        await orchestrator.RunAsync("Hello world", "session1", new AgentOptions());

        await contextManager.Received(1).AddMessageAsync("session1",
            Arg.Is<Message>(m => m.Role == MessageRole.User && m.Content == "Hello world"));
    }

    [Fact]
    public async Task RunAsync_ExecutesToolCalls_WhenProviderReturnsToolCalls()
    {
        var toolCall = new ToolCall { Id = "call1", Name = "read_file", Arguments = new() { ["path"] = "test.txt" } };

        var callCount = 0;
        var provider = new FakeProvider((_, __) =>
        {
            callCount++;
            if (callCount == 1)
                return CreateStream(
                    new StreamChunk { Delta = "Let me check", IsFinished = false },
                    new StreamChunk { IsFinished = true, ToolCalls = new() { toolCall } });
            // Second call returns empty to stop the loop
            return CreateEmptyStream();
        });

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("read_file");
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>()).Returns(
            new ToolResult { ToolCallId = "call1", ToolName = "read_file", Content = "file content" });

        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("read_file").Returns(tool);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager, toolRegistry: toolRegistry);

        var response = await orchestrator.RunAsync("Read test.txt", "session1", new AgentOptions());

        Assert.Single(response.ToolExecutions);
        Assert.Equal("read_file", response.ToolExecutions[0].Call.Name);
        Assert.Equal("file content", response.ToolExecutions[0].Result.Content);
    }

    [Fact]
    public async Task RunAsync_ReturnsErrorResult_WhenToolNotFound()
    {
        var toolCall = new ToolCall { Id = "call1", Name = "unknown_tool", Arguments = new() };

        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { IsFinished = true, ToolCalls = new() { toolCall } }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("unknown_tool").Returns((ITool?)null);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool>());

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager, toolRegistry: toolRegistry);

        var response = await orchestrator.RunAsync("Do something", "session1", new AgentOptions());

        Assert.Single(response.ToolExecutions);
        Assert.True(response.ToolExecutions[0].Result.IsError);
        Assert.Contains("Unknown tool", response.ToolExecutions[0].Result.Content);
    }

    [Fact]
    public async Task RunAsync_RespectsMaxIterations()
    {
        var toolCall = new ToolCall { Id = "call1", Name = "test", Arguments = new() };

        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { IsFinished = true, ToolCalls = new() { toolCall } }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("test");
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>()).Returns(
            new ToolResult { Content = "result" });

        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("test").Returns(tool);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager, toolRegistry: toolRegistry);

        var response = await orchestrator.RunAsync("Test", "session1", new AgentOptions { MaxIterations = 3 });

        Assert.Equal(3, response.ToolExecutions.Count);
    }

    [Fact]
    public async Task RunAsync_SetsToolCallId_OnResult()
    {
        var toolCall = new ToolCall { Id = "custom-id", Name = "read_file", Arguments = new() };

        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { IsFinished = true, ToolCalls = new() { toolCall } }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("read_file");
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>()).Returns(
            new ToolResult { Content = "result" });

        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("read_file").Returns(tool);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager, toolRegistry: toolRegistry);

        var response = await orchestrator.RunAsync("Test", "session1", new AgentOptions());

        Assert.Equal("custom-id", response.ToolExecutions[0].Result.ToolCallId);
    }

    [Fact]
    public async Task RunAsync_HandlesToolException_Gracefully()
    {
        var toolCall = new ToolCall { Id = "call1", Name = "failing_tool", Arguments = new() };

        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { IsFinished = true, ToolCalls = new() { toolCall } }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("failing_tool");
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>())
            .Returns(Task.FromException<ToolResult>(new InvalidOperationException("Tool failed")));

        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("failing_tool").Returns(tool);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager, toolRegistry: toolRegistry);

        var response = await orchestrator.RunAsync("Test", "session1", new AgentOptions());

        Assert.Single(response.ToolExecutions);
        Assert.True(response.ToolExecutions[0].Result.IsError);
        Assert.Contains("Tool failed", response.ToolExecutions[0].Result.Content);
    }

    [Fact]
    public async Task RunAsync_TrimsContext_BeforeEachIteration()
    {
        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { Delta = "Hi", IsFinished = true }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager);

        await orchestrator.RunAsync("Test", "session1", new AgentOptions { MaxTokens = 1000 });

        await contextManager.Received(1).TrimContextAsync("session1", 1000);
    }

    [Fact]
    public async Task StreamRunAsync_YieldsTextDeltaEvents()
    {
        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(
                new StreamChunk { Delta = "Hello", IsFinished = false },
                new StreamChunk { Delta = " World", IsFinished = true }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager);

        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.StreamRunAsync("Hi", "session1", new AgentOptions()))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e is TextDeltaEvent { Delta: "Hello" });
        Assert.Contains(events, e => e is TextDeltaEvent { Delta: " World" });
        Assert.Contains(events, e => e is AgentFinishedEvent);
    }

    [Fact]
    public async Task StreamRunAsync_YieldsToolCallStartAndEndEvents()
    {
        var toolCall = new ToolCall { Id = "call1", Name = "read_file", Arguments = new() };

        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateStream(new StreamChunk { IsFinished = true, ToolCalls = new() { toolCall } }));

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("read_file");
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>()).Returns(
            new ToolResult { Content = "result" });

        var toolRegistry = Substitute.For<IToolRegistry>();
        toolRegistry.GetTool("read_file").Returns(tool);
        toolRegistry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager, toolRegistry: toolRegistry);

        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.StreamRunAsync("Hi", "session1", new AgentOptions()))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e is ToolCallStartEvent);
        Assert.Contains(events, e => e is ToolCallEndEvent);
    }

    [Fact]
    public async Task StreamRunAsync_RespectsCancellation()
    {
        var provider = new FakeProvider((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return CreateStream(new StreamChunk { Delta = "Hi", IsFinished = true });
        });

        var contextManager = Substitute.For<IContextManager>();
        contextManager.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var orchestrator = CreateOrchestrator(provider: provider, contextManager: contextManager);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var events = new List<AgentEvent>();
        await foreach (var evt in orchestrator.StreamRunAsync("Hi", "session1", new AgentOptions { MaxIterations = 100 }, cts.Token))
        {
            events.Add(evt);
        }

        var finished = events.OfType<AgentFinishedEvent>().FirstOrDefault();
        Assert.NotNull(finished);
        Assert.True(finished!.Response.WasCancelled);
    }
}
