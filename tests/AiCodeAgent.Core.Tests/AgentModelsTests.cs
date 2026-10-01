using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Tests;

public class AgentModelsTests
{
    [Fact]
    public void TokenUsage_TotalTokens_IsSumOfPromptAndCompletion()
    {
        var usage = new TokenUsage { PromptTokens = 100, CompletionTokens = 50 };

        Assert.Equal(150, usage.TotalTokens);
    }

    [Fact]
    public void TokenUsage_DefaultValues_AreZero()
    {
        var usage = new TokenUsage();

        Assert.Equal(0, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
        Assert.Equal(0, usage.TotalTokens);
    }

    [Fact]
    public void ToolCall_DefaultValues_AreCorrect()
    {
        var call = new ToolCall();

        Assert.False(string.IsNullOrEmpty(call.Id));
        Assert.Equal(string.Empty, call.Name);
        Assert.NotNull(call.Arguments);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void ToolResult_DefaultValues_AreCorrect()
    {
        var result = new ToolResult();

        Assert.Equal(string.Empty, result.ToolCallId);
        Assert.Equal(string.Empty, result.ToolName);
        Assert.Equal(string.Empty, result.Content);
        Assert.False(result.IsError);
        Assert.Null(result.Data);
    }

    [Fact]
    public void AgentExecutionContext_DefaultValues_AreCorrect()
    {
        var context = new AgentExecutionContext();

        Assert.Equal(string.Empty, context.SessionId);
        Assert.Equal(Directory.GetCurrentDirectory(), context.WorkingDirectory);
        Assert.NotNull(context.Environment);
        Assert.Empty(context.Environment);
        Assert.False(context.IsReadOnly);
        Assert.NotNull(context.AllowedPaths);
        Assert.Empty(context.AllowedPaths);
    }

    [Fact]
    public void AgentOptions_DefaultValues_AreCorrect()
    {
        var options = new AgentOptions();

        Assert.Null(options.Model);
        Assert.Equal(Directory.GetCurrentDirectory(), options.WorkingDirectory);
        Assert.Equal(50, options.MaxIterations);
        Assert.Equal(200_000, options.MaxTokens);
        Assert.False(options.AutoApprove);
        Assert.False(options.Verbose);
        Assert.NotNull(options.EnabledTools);
        Assert.Empty(options.EnabledTools);
    }

    [Fact]
    public void Message_DefaultValues_AreCorrect()
    {
        var message = new Message();

        Assert.Equal(MessageRole.System, message.Role);
        Assert.Equal(string.Empty, message.Content);
        Assert.Null(message.Name);
        Assert.Null(message.ToolCalls);
        Assert.Null(message.ToolCallId);
        Assert.True(message.Timestamp <= DateTime.UtcNow);
        Assert.Equal(0, message.TokenCount);
    }

    [Fact]
    public void Message_WithRoleAndContent_SetsProperties()
    {
        var message = new Message { Role = MessageRole.User, Content = "hello" };

        Assert.Equal(MessageRole.User, message.Role);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public void CompletionRequest_DefaultValues_AreCorrect()
    {
        var request = new CompletionRequest();

        Assert.NotNull(request.Messages);
        Assert.Empty(request.Messages);
        Assert.NotNull(request.Tools);
        Assert.Empty(request.Tools);
        Assert.Null(request.SystemPrompt);
        Assert.NotNull(request.Options);
    }

    [Fact]
    public void CompletionOptions_DefaultValues_AreCorrect()
    {
        var options = new CompletionOptions();

        Assert.Equal(0.7f, options.Temperature);
        Assert.Equal(8192, options.MaxTokens);
        Assert.True(options.Stream);
        Assert.Null(options.Model);
        Assert.Equal(1.0f, options.TopP);
    }

    [Fact]
    public void ToolDefinition_DefaultValues_AreCorrect()
    {
        var def = new ToolDefinition();

        Assert.Equal(string.Empty, def.Name);
        Assert.Equal(string.Empty, def.Description);
        Assert.NotNull(def.Parameters);
    }

    [Fact]
    public void JsonSchema_DefaultValues_AreCorrect()
    {
        var schema = new JsonSchema();

        Assert.Equal("object", schema.Type);
        Assert.NotNull(schema.Properties);
        Assert.Empty(schema.Properties);
        Assert.NotNull(schema.Required);
        Assert.Empty(schema.Required);
    }

    [Fact]
    public void PropertySchema_DefaultValues_AreCorrect()
    {
        var schema = new PropertySchema();

        Assert.Equal("string", schema.Type);
        Assert.Equal(string.Empty, schema.Description);
        Assert.Null(schema.Enum);
        Assert.Null(schema.Items);
    }

    [Fact]
    public void StreamChunk_DefaultValues_AreCorrect()
    {
        var chunk = new StreamChunk();

        Assert.Equal(string.Empty, chunk.Delta);
        Assert.Null(chunk.ToolCalls);
        Assert.False(chunk.IsFinished);
        Assert.Null(chunk.FinishReason);
    }

    [Fact]
    public void MemoryEntry_DefaultValues_AreCorrect()
    {
        var entry = new MemoryEntry();

        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(string.Empty, entry.Content);
        Assert.NotNull(entry.Metadata);
        Assert.Empty(entry.Metadata);
        Assert.Equal(0f, entry.Score);
        Assert.True(entry.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public void CodeSnippet_DefaultValues_AreCorrect()
    {
        var snippet = new CodeSnippet();

        Assert.Equal(string.Empty, snippet.FilePath);
        Assert.Equal(string.Empty, snippet.Content);
        Assert.Equal(0, snippet.StartLine);
        Assert.Equal(0, snippet.EndLine);
        Assert.Equal(string.Empty, snippet.Language);
        Assert.Equal(0f, snippet.Score);
    }

    [Fact]
    public void AgentResponse_DefaultValues_AreCorrect()
    {
        var response = new AgentResponse();

        Assert.Equal(string.Empty, response.Content);
        Assert.NotNull(response.ToolExecutions);
        Assert.Empty(response.ToolExecutions);
        Assert.NotNull(response.TotalUsage);
        Assert.Equal(TimeSpan.Zero, response.Duration);
        Assert.False(response.WasCancelled);
    }

    [Fact]
    public void ToolExecution_DefaultValues_AreCorrect()
    {
        var execution = new ToolExecution();

        Assert.Null(execution.Call);
        Assert.Null(execution.Result);
        Assert.Equal(TimeSpan.Zero, execution.Duration);
    }

    [Fact]
    public void AgentEvent_DerivedTypes_AreCorrect()
    {
        var textDelta = new TextDeltaEvent("hello");
        Assert.Equal("hello", textDelta.Delta);

        var thinking = new ThinkingEvent("thinking");
        Assert.Equal("thinking", thinking.Content);

        var toolCall = new ToolCall { Name = "test" };
        var toolStart = new ToolCallStartEvent(toolCall);
        Assert.Equal("test", toolStart.Call.Name);

        var toolResult = new ToolResult { Content = "result" };
        var toolEnd = new ToolCallEndEvent(toolCall, toolResult, TimeSpan.FromMilliseconds(100));
        Assert.Equal("result", toolEnd.Result.Content);
        Assert.Equal(100, toolEnd.Duration.TotalMilliseconds);

        var response = new AgentResponse { Content = "done" };
        var finished = new AgentFinishedEvent(response);
        Assert.Equal("done", finished.Response.Content);

        var error = new AgentErrorEvent(new InvalidOperationException("test"));
        Assert.Equal("test", error.Error.Message);

        var approval = new ApprovalRequestEvent(toolCall, new TaskCompletionSource<bool>());
        Assert.Equal("test", approval.Call.Name);
    }
}
