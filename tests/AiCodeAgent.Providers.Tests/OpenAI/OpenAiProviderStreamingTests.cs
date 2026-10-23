using System.Net;
using System.Text;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Providers.Tests.OpenAI;

/// <summary>Serves one canned SSE body to every request, so the real OpenAiProvider parses it.</summary>
internal sealed class OpenAiSseServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string BaseUrl { get; }

    public OpenAiSseServer(string sseBody)
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
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    await reader.ReadToEndAsync();
                var bytes = Encoding.UTF8.GetBytes(sseBody);
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { /* shutting down */ } }
}

public class OpenAiProviderStreamingTests
{
    private const string ToolCallWithFinish =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task FinishInSameChunkAsToolCallDelta_EmitsOneFinishedChunkWithToolCalls()
    {
        var chunks = await StreamAsync(ToolCallWithFinish);

        var only = Assert.Single(chunks);
        Assert.True(only.IsFinished);
        Assert.Equal("tool_calls", only.FinishReason);
        var call = Assert.Single(only.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.txt", call.Arguments["path"]?.ToString());
    }

    [Fact]
    public async Task FinishInSeparateChunk_EmitsOneFinishedChunkWithToolCalls()
    {
        var sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await StreamAsync(sse);

        var only = Assert.Single(chunks);
        Assert.True(only.IsFinished);
        var call = Assert.Single(only.ToolCalls!);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.txt", call.Arguments["path"]?.ToString());
    }

    [Fact]
    public async Task NoFinishReason_StreamEndEmitsAccumulatedToolCalls()
    {
        // Same tool-call delta as above, but the server closes the stream without any finish_reason.
        var sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]}}]}\n\n";

        var chunks = await StreamAsync(sse);

        var only = Assert.Single(chunks);
        Assert.True(only.IsFinished);
        Assert.Equal("tool_calls", only.FinishReason);
        Assert.Equal("read_file", Assert.Single(only.ToolCalls!).Name);
    }

    [Fact]
    public async Task UsageChunkAfterFinish_MergesUsageIntoTheSingleFinishedChunk()
    {
        var sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await StreamAsync(sse);

        var only = Assert.Single(chunks);
        Assert.True(only.IsFinished);
        Assert.Equal("stop", only.FinishReason);
        Assert.NotNull(only.Usage);
        Assert.Equal(12, only.Usage!.PromptTokens);
        Assert.Equal(3, only.Usage.CompletionTokens);
    }

    [Fact]
    public async Task TextStream_EmitsDeltasThenOneFinish()
    {
        var sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await StreamAsync(sse);

        Assert.Equal(1, chunks.Count(c => c.IsFinished));
        Assert.Equal("Hello", string.Concat(chunks.Where(c => !c.IsFinished).Select(c => c.Delta)));
        Assert.Equal("stop", chunks.Single(c => c.IsFinished).FinishReason);
    }

    private static async Task<List<StreamChunk>> StreamAsync(string sse)
    {
        using var server = new OpenAiSseServer(sse);
        var provider = new OpenAiProvider(
            new ProviderConfiguration
            {
                Name = "openai",
                BaseUrl = server.BaseUrl,
                DefaultModel = "gpt-4o",
                ApiKey = "k",
                TimeoutSeconds = 30,
                VerifySsl = false
            },
            NullLogger<OpenAiProvider>.Instance);

        var request = new CompletionRequest
        {
            SystemPrompt = "You are a coding agent.",
            Messages = new List<Message> { new() { Role = MessageRole.User, Content = "read a.txt" } }
        };

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.StreamAsync(request))
            chunks.Add(chunk);
        return chunks;
    }
}
