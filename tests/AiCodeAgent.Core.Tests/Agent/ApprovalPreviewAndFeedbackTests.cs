using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

/// <summary>E2-S5: approval requests carry a dry-run preview; "Deny with feedback" reaches the model and the agent continues.</summary>
public class ApprovalPreviewAndFeedbackTests
{
    private sealed class FakeProvider : IAiProvider
    {
        private readonly Func<CompletionRequest, IAsyncEnumerable<StreamChunk>> _stream;
        public FakeProvider(Func<CompletionRequest, IAsyncEnumerable<StreamChunk>> stream) => _stream = stream;
        public string Name => "Fake";
        public string[] SupportedModels => ["fake-model"];
        public List<CompletionRequest> Requests { get; } = new();
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _stream(request);
        }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(SupportedModels);
    }

    private static async IAsyncEnumerable<StreamChunk> Stream(params StreamChunk[] chunks)
    {
        foreach (var c in chunks) { await Task.Yield(); yield return c; }
    }

    private static (AgentOrchestrator Orchestrator, ITool Tool, IContextManager Context, FakeProvider Provider) Create(ToolPreview? preview)
    {
        var call = new ToolCall { Id = "c1", Name = "edit_file", Arguments = new() { ["path"] = "a.cs", ["old_string"] = "x", ["new_string"] = "y" } };
        var calls = 0;
        var provider = new FakeProvider(_ => ++calls == 1
            ? Stream(new StreamChunk { IsFinished = true, ToolCalls = new() { call } })
            : Stream(new StreamChunk { Delta = "Understood, using the helper.", IsFinished = true }));

        var tool = Substitute.For<ITool, IPreviewableTool>();
        tool.Name.Returns("edit_file");
        tool.Risk.Returns(RiskLevel.Write);
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>())
            .Returns(new ToolResult { ToolCallId = "c1", ToolName = "edit_file", Content = "edited" });
        ((IPreviewableTool)tool).PreviewAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>())
            .Returns(Task.FromResult(preview));

        var registry = Substitute.For<IToolRegistry>();
        registry.GetTool("edit_file").Returns(tool);
        registry.GetTools(Arg.Any<List<string>>()).Returns(new List<ITool> { tool });

        var context = Substitute.For<IContextManager>();
        context.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var orchestrator = new AgentOrchestrator(provider, context, registry, new AgentConfiguration(),
            Substitute.For<ILogger<AgentOrchestrator>>(), Substitute.For<IPermissionService>(), Substitute.For<ICheckpointManager>());
        return (orchestrator, tool, context, provider);
    }

    [Fact]
    public async Task ApprovalRequest_CarriesTheToolsPreview()
    {
        var preview = new ToolPreview { FileChange = new FileChangePreview("a.cs", "x\n", "y\n") };
        var (orchestrator, _, _, _) = Create(preview);
        ApprovalRequestEvent? seen = null;

        await foreach (var evt in orchestrator.StreamRunAsync("go", "s1", new AgentOptions()))
        {
            if (evt is ApprovalRequestEvent approval)
            {
                seen = approval;
                approval.Approval.SetResult(true);
            }
        }

        Assert.NotNull(seen);
        Assert.Same(preview, seen!.Preview);
    }

    [Fact]
    public async Task ApprovalRequest_WhenThePreviewThrows_StillAsks()
    {
        var (orchestrator, tool, _, _) = Create(null);
        ((IPreviewableTool)tool).PreviewAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>())
            .Returns<Task<ToolPreview?>>(_ => throw new IOException("disk on fire"));
        ApprovalRequestEvent? seen = null;

        await foreach (var evt in orchestrator.StreamRunAsync("go", "s1", new AgentOptions()))
        {
            if (evt is ApprovalRequestEvent approval)
            {
                seen = approval;
                approval.Approval.SetResult(false);
            }
        }

        Assert.NotNull(seen);
        Assert.Contains("disk on fire", seen!.Preview?.Error);
    }

    [Fact]
    public async Task DenyWithFeedback_SendsTheTextToTheModel_AndTheAgentContinues()
    {
        var (orchestrator, tool, context, provider) = Create(null);
        var events = new List<AgentEvent>();

        await foreach (var evt in orchestrator.StreamRunAsync("go", "s1", new AgentOptions()))
        {
            events.Add(evt);
            if (evt is ApprovalRequestEvent approval)
            {
                approval.Feedback.Text = "Use the existing DateHelper instead.";
                approval.Approval.SetResult(false);
            }
        }

        await tool.DidNotReceive().ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<AgentExecutionContext>());
        await context.Received().AddMessageAsync("s1", Arg.Is<Message>(m =>
            m.Role == MessageRole.Tool && m.ToolCallId == "c1" &&
            m.Content.Contains("User feedback: Use the existing DateHelper instead.")));

        var end = Assert.Single(events.OfType<ToolCallEndEvent>());
        Assert.True(end.Result.IsError);
        Assert.Contains("User feedback: Use the existing DateHelper instead.", end.Result.Content);

        Assert.Equal(2, provider.Requests.Count); // the model was asked again with the feedback
        Assert.Contains(events.OfType<TextDeltaEvent>(), d => d.Delta.Contains("using the helper"));
    }

    [Fact]
    public void DeniedContent_WithoutFeedback_IsUnchanged()
    {
        var call = new ToolCall { Name = "edit_file" };
        Assert.Equal("Tool execution denied by user: edit_file", AgentOrchestrator.DeniedContent(call, null));
        Assert.Equal("Tool execution denied by user: edit_file", AgentOrchestrator.DeniedContent(call, new ApprovalFeedback { Text = "  " }));
    }
}
