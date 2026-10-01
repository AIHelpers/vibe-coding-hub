using System.Net;
using System.Text;
using System.Text.Json;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Anthropic;
using AiCodeAgent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Providers.Tests.Anthropic;

/// <summary>Runs the real AnthropicProvider against a local fake of the Messages API.</summary>
public sealed class FakeAnthropicServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string BaseUrl { get; }
    public List<JsonElement> Requests { get; } = new();
    public string SseBody { get; set; } = string.Empty;

    public FakeAnthropicServer()
    {
        var port = new Random().Next(20_000, 40_000);
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (Requests) Requests.Add(JsonDocument.Parse(body).RootElement.Clone());
                var bytes = Encoding.UTF8.GetBytes(SseBody);
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { /* shutting down */ } }
}

public class AnthropicThinkingAndCachingTests
{
    private const string ThinkingStream =
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":10,\"output_tokens\":0,\"cache_read_input_tokens\":500,\"cache_creation_input_tokens\":40}}}\n\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Let me \"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"think.\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"SIG\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"123\"}}\n\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Reading.\"}}\n\n" +
        "data: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool1\",\"name\":\"read_file\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":33}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static AnthropicProvider Provider(FakeAnthropicServer server) => new(
        new ProviderConfiguration { Name = "anthropic", BaseUrl = server.BaseUrl, DefaultModel = "claude-sonnet-4-5", ApiKey = "k", TimeoutSeconds = 30, VerifySsl = false },
        NullLogger<AnthropicProvider>.Instance);

    private static CompletionRequest Request(string model, ReasoningEffort? effort, List<Message>? history = null) => new()
    {
        SystemPrompt = "You are a coding agent.",
        Messages = history ?? new List<Message> { new() { Role = MessageRole.User, Content = "read a.txt" } },
        Tools = new List<ToolDefinition>
        {
            new() { Name = "read_file", Description = "Read", Parameters = new JsonSchema { Properties = new() { ["path"] = new() { Type = "string" } }, Required = ["path"] } },
            new() { Name = "grep", Description = "Search", Parameters = new JsonSchema() }
        },
        Options = new CompletionOptions { Model = model, Reasoning = effort, MaxTokens = 8192, Temperature = 0.2f }
    };

    private static async Task<List<StreamChunk>> Drain(AnthropicProvider p, CompletionRequest r)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var c in p.StreamAsync(r)) chunks.Add(c);
        return chunks;
    }

    [Fact]
    public async Task HighEffort_SendsThinkingConfig_DropsTemperature_AndRaisesMaxTokens()
    {
        using var server = new FakeAnthropicServer { SseBody = ThinkingStream };
        await Drain(Provider(server), Request("claude-sonnet-4-5", ReasoningEffort.High));

        var body = server.Requests.Single();
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(24_000, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.True(body.GetProperty("max_tokens").GetInt32() > 24_000);
    }

    [Fact]
    public async Task NoEffort_KeepsClassicParameters()
    {
        using var server = new FakeAnthropicServer { SseBody = ThinkingStream };
        await Drain(Provider(server), Request("claude-sonnet-4-5", null));

        var body = server.Requests.Single();
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal(0.2f, body.GetProperty("temperature").GetSingle(), 3);
        Assert.Equal(8192, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task UnsupportedModel_IgnoresEffort()
    {
        using var server = new FakeAnthropicServer { SseBody = ThinkingStream };
        await Drain(Provider(server), Request("claude-3-5-sonnet-20241022", ReasoningEffort.High));

        Assert.False(server.Requests.Single().TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task PromptCaching_MarksSystemPromptAndLastTool()
    {
        using var server = new FakeAnthropicServer { SseBody = ThinkingStream };
        await Drain(Provider(server), Request("claude-sonnet-4-5", null));

        var body = server.Requests.Single();
        var system = body.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Equal("You are a coding agent.", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());

        var tools = body.GetProperty("tools");
        Assert.False(tools[0].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", tools[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Stream_ReturnsThinkingBlocks_WithSignature_ToolCalls_AndCacheUsage()
    {
        using var server = new FakeAnthropicServer { SseBody = ThinkingStream };
        var chunks = await Drain(Provider(server), Request("claude-sonnet-4-5", ReasoningEffort.Medium));

        Assert.Equal("Let me think.", string.Concat(chunks.Select(c => c.ThinkingDelta)));
        Assert.Equal("Reading.", string.Concat(chunks.Select(c => c.Delta)));   // thinking never leaks into the answer

        var last = chunks.Last();
        Assert.True(last.IsFinished);
        var block = Assert.Single(last.ThinkingBlocks!);
        Assert.Equal("Let me think.", block.Text);
        Assert.Equal("SIG123", block.Signature);
        Assert.Equal("read_file", Assert.Single(last.ToolCalls!).Name);
        Assert.Equal(500, last.Usage!.CacheReadTokens);
        Assert.Equal(40, last.Usage.CacheCreationTokens);
        Assert.Equal(10, last.Usage.PromptTokens);
        Assert.Equal(33, last.Usage.CompletionTokens);
    }

    [Fact]
    public async Task NextRequest_ReplaysSignedThinkingBeforeToolUse()
    {
        using var server = new FakeAnthropicServer { SseBody = ThinkingStream };
        var history = new List<Message>
        {
            new() { Role = MessageRole.User, Content = "read a.txt" },
            new()
            {
                Role = MessageRole.Assistant,
                Content = "Reading.",
                ToolCalls = new List<ToolCall> { new() { Id = "tool1", Name = "read_file", Arguments = new() { ["path"] = "a.txt" } } },
                ThinkingBlocks = new List<ThinkingBlock>
                {
                    new() { Type = "thinking", Text = "Let me think.", Signature = "SIG123" },
                    new() { Type = "redacted_thinking", Data = "OPAQUE" },
                    new() { Type = "thinking", Text = "unsigned", Signature = null }   // must be dropped: API rejects unsigned blocks
                }
            },
            new() { Role = MessageRole.Tool, ToolCallId = "tool1", Content = "file body" }
        };

        await Drain(Provider(server), Request("claude-sonnet-4-5", ReasoningEffort.Medium, history));

        var assistant = server.Requests.Single().GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(3, assistant.GetArrayLength());
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("SIG123", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("redacted_thinking", assistant[1].GetProperty("type").GetString());
        Assert.Equal("tool_use", assistant[2].GetProperty("type").GetString());
    }
}

public class ReasoningMappingTests
{
    [Theory]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("claude-opus-4-5", true)]
    [InlineData("claude-3-7-sonnet-20250219", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-3-5-sonnet-20241022", false)]
    [InlineData("claude-3-opus-20240229", false)]
    [InlineData("gpt-4o", false)]
    [InlineData(null, false)]
    public void AnthropicThinking_SupportedModels(string? model, bool expected) =>
        Assert.Equal(expected, AnthropicThinking.Supports(model));

    [Fact]
    public void Budgets_IncreaseWithEffort_AndOffMeansNone()
    {
        Assert.Null(AnthropicThinking.Budget(null));
        Assert.Null(AnthropicThinking.Budget(ReasoningEffort.Off));
        Assert.True(AnthropicThinking.Budget(ReasoningEffort.Low) < AnthropicThinking.Budget(ReasoningEffort.Medium));
        Assert.True(AnthropicThinking.Budget(ReasoningEffort.Medium) < AnthropicThinking.Budget(ReasoningEffort.High));
        Assert.True(AnthropicThinking.Budget(ReasoningEffort.Low) >= 1024);   // API minimum
    }

    [Theory]
    [InlineData("gpt-5", ReasoningEffort.High, "high")]
    [InlineData("gpt-5-mini", ReasoningEffort.Off, "minimal")]
    [InlineData("o3", ReasoningEffort.Off, "low")]
    [InlineData("o4-mini", ReasoningEffort.Medium, "medium")]
    [InlineData("gpt-4o", ReasoningEffort.High, null)]
    [InlineData("gpt-5", null, null)]
    public void OpenAiEffort_OnlyForReasoningModels(string model, ReasoningEffort? effort, string? expected) =>
        Assert.Equal(expected, OpenAiReasoning.Effort(model, effort));
}
