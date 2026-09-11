using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.OpenAI;

public class OpenAiProvider : BaseHttpProvider
{
    private static readonly string[] Models =
    [
        "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-4",
        "gpt-3.5-turbo", "o1-preview", "o1-mini"
    ];

    public OpenAiProvider(ProviderConfiguration config, ILogger<OpenAiProvider> logger)
        : base(config, logger) { }

    public override string Name => "OpenAI";
    public override string[] SupportedModels => Models;

    public override async Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<OpenAiModelsResponse>(
                "/v1/models", cancellationToken);
            return response?.Data.Select(m => m.Id).ToArray() ?? Models;
        }
        catch
        {
            return Models;
        }
    }

    public override async Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(request, stream: false);
        var response = await HttpClient.PostAsJsonAsync("/v1/chat/completions", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiResponse>(
            cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Empty response");

        return MapResponse(result);
    }

    public override async IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(request, stream: true);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };

        var response = await HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var toolCallsAccumulator = new Dictionary<int, ToolCallAccumulator>();

        // When include_usage is true, OpenAI sends a final chunk with the usage
        // populated but an empty choices array. The orchestrator stops as soon as
        // it sees IsFinished, so we buffer the finished state and keep reading
        // until the usage chunk arrives (or the stream ends), then emit a single
        // finished StreamChunk carrying both the finish reason and the usage.
        StreamChunk? pendingFinishedChunk = null;

        await foreach (var line in ReadSseStreamAsync(response, cancellationToken))
        {
            if (line == "[DONE]") break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            OpenAiStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(line, JsonOptions.Default);
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Failed to parse streaming chunk: {Line}", line);
                continue;
            }

            // Terminal usage chunk: choices is empty, usage is populated.
            // Merge it into the buffered finished chunk and emit it.
            if (chunk?.Usage != null)
            {
                var usage = new TokenUsage
                {
                    PromptTokens = chunk.Usage.PromptTokens,
                    CompletionTokens = chunk.Usage.CompletionTokens
                };

                if (pendingFinishedChunk != null)
                {
                    yield return pendingFinishedChunk with { Usage = usage };
                    pendingFinishedChunk = null;
                }
                else
                {
                    yield return new StreamChunk { IsFinished = true, Usage = usage };
                }
                continue;
            }

            if (chunk?.Choices is not { Count: > 0 }) continue;
            var choice = chunk.Choices[0];

            // Accumulate tool calls
            if (choice.Delta?.ToolCalls != null)
            {
                foreach (var tc in choice.Delta.ToolCalls)
                {
                    if (!toolCallsAccumulator.TryGetValue(tc.Index, out var acc))
                    {
                        acc = new ToolCallAccumulator { Id = tc.Id ?? string.Empty, Name = tc.Function?.Name ?? string.Empty };
                        toolCallsAccumulator[tc.Index] = acc;
                    }
                    if (tc.Function?.Arguments != null)
                        acc.Arguments += tc.Function.Arguments;
                    if (!string.IsNullOrEmpty(tc.Function?.Name))
                        acc.Name = tc.Function.Name;
                    if (!string.IsNullOrEmpty(tc.Id))
                        acc.Id = tc.Id;
                }
                continue;
            }

            var delta = choice.Delta?.Content ?? string.Empty;
            var isFinished = choice.FinishReason != null;

            if (isFinished)
            {
                StreamChunk finishedChunk;
                if (toolCallsAccumulator.Count > 0)
                {
                    var toolCalls = toolCallsAccumulator.Values
                        .Select(acc => new ToolCall
                        {
                            Id = acc.Id,
                            Name = acc.Name,
                            Arguments = ParseArguments(acc.Arguments)
                        })
                        .ToList();

                    finishedChunk = new StreamChunk
                    {
                        IsFinished = true,
                        ToolCalls = toolCalls,
                        FinishReason = choice.FinishReason
                    };
                }
                else
                {
                    finishedChunk = new StreamChunk
                    {
                        Delta = delta,
                        IsFinished = true,
                        FinishReason = choice.FinishReason
                    };
                }

                // Buffer the finished chunk: a usage chunk may follow. If the
                // stream ends without one, we still need to emit it.
                if (pendingFinishedChunk != null)
                    yield return pendingFinishedChunk;
                pendingFinishedChunk = finishedChunk;
            }
            else
            {
                yield return new StreamChunk
                {
                    Delta = delta,
                    IsFinished = false
                };
            }
        }

        // Stream ended without a separate usage chunk; emit the buffered finish.
        if (pendingFinishedChunk != null)
            yield return pendingFinishedChunk;
    }

    /// <summary>
    /// Builds OpenAI's mixed content-parts array for a user message carrying
    /// image attachments: a leading text part (if any) followed by one
    /// image_url part per attachment, using inline data: URLs.
    /// </summary>
    private static List<object> BuildUserContentParts(Message msg)
    {
        var parts = new List<object>();
        if (!string.IsNullOrEmpty(msg.Content))
            parts.Add(new { type = "text", text = msg.Content });
        foreach (var image in msg.Images!)
        {
            parts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{image.MediaType};base64,{image.Base64Data}" }
            });
        }
        return parts;
    }

    private object BuildPayload(CompletionRequest request, bool stream)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User when msg.Images is { Count: > 0 }:
                    messages.Add(new { role = "user", content = BuildUserContentParts(msg) });
                    break;
                case MessageRole.User:
                    messages.Add(new { role = "user", content = msg.Content });
                    break;
                case MessageRole.Assistant when msg.ToolCalls?.Count > 0:
                    messages.Add(new
                    {
                        role = "assistant",
                        content = msg.Content,
                        tool_calls = msg.ToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new
                            {
                                name = tc.Name,
                                arguments = JsonSerializer.Serialize(tc.Arguments)
                            }
                        })
                    });
                    break;
                case MessageRole.Assistant:
                    messages.Add(new { role = "assistant", content = msg.Content });
                    break;
                case MessageRole.Tool:
                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = msg.ToolCallId,
                        content = msg.Content
                    });
                    break;
            }
        }

        var tools = request.Tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new
                {
                    type = t.Parameters.Type,
                    properties = t.Parameters.Properties.ToDictionary(
                        p => p.Key,
                        p => (object)new
                        {
                            type = p.Value.Type,
                            description = p.Value.Description,
                            @enum = p.Value.Enum
                        }),
                    required = t.Parameters.Required
                }
            }
        }).ToList();

        return new
        {
            model = request.Options.Model ?? Config.DefaultModel,
            messages,
            tools = tools.Count > 0 ? tools : null,
            tool_choice = tools.Count > 0 ? "auto" : null,
            temperature = request.Options.Temperature,
            max_tokens = request.Options.MaxTokens,
            stream,
            stream_options = stream ? new { include_usage = true } : null
        };
    }

    private static CompletionResponse MapResponse(OpenAiResponse result)
    {
        if (result.Choices is not { Count: > 0 })
            throw new InvalidOperationException("OpenAI response contains no choices");

        var choice = result.Choices[0];
        var toolCalls = choice.Message?.ToolCalls?.Select(tc => new ToolCall
        {
            Id = tc.Id,
            Name = tc.Function.Name,
            Arguments = ParseArguments(tc.Function.Arguments)
        }).ToList() ?? new List<ToolCall>();

        return new CompletionResponse
        {
            Content = choice.Message?.Content ?? string.Empty,
            ToolCalls = toolCalls,
            Usage = new TokenUsage
            {
                PromptTokens = result.Usage?.PromptTokens ?? 0,
                CompletionTokens = result.Usage?.CompletionTokens ?? 0
            },
            Model = result.Model ?? string.Empty,
            StopReason = choice.FinishReason ?? string.Empty
        };
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private class ToolCallAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
    }
}