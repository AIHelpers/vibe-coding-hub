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
        var response = await SendWithRetryAsync(
            () => HttpClient.PostAsJsonAsync("/v1/chat/completions", payload, cancellationToken),
            cancellationToken);
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

        // A fresh HttpRequestMessage (and JsonContent) must be built on every
        // attempt — HttpRequestMessage cannot be sent more than once, so the
        // previous single-shot SendAsync could never have been retried as-is.
        var response = await SendWithRetryAsync(
            () => HttpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(payload) },
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var toolCallsAccumulator = new Dictionary<int, ToolCallAccumulator>();

        // The finish is buffered until a usage chunk arrives or the stream ends, so the caller gets
        // exactly one finished StreamChunk carrying the finish reason, the tool calls and the usage.
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

            // Terminal usage chunk: choices is empty, usage is populated. Merge it into the buffered
            // finished chunk, or emit a finished chunk with the usage if no finish came before it.
            if (chunk?.Usage != null)
            {
                var usage = new TokenUsage
                {
                    PromptTokens = chunk.Usage.PromptTokens,
                    CompletionTokens = chunk.Usage.CompletionTokens
                };

                yield return pendingFinishedChunk != null
                    ? pendingFinishedChunk with { Usage = usage }
                    : BuildFinishedChunk(null, string.Empty, toolCallsAccumulator, usage);
                pendingFinishedChunk = null;
                continue;
            }

            if (chunk?.Choices is not { Count: > 0 }) continue;
            var choice = chunk.Choices[0];

            // Accumulate tool-call deltas. The same chunk can also carry finish_reason (some
            // OpenAI-compatible servers send it that way), so the finish is evaluated below
            // instead of skipping the chunk.
            var hasToolCallDelta = choice.Delta?.ToolCalls is { Count: > 0 };
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
            }

            var delta = choice.Delta?.Content ?? string.Empty;

            if (choice.FinishReason != null)
            {
                // Replace any earlier finish rather than stacking a second one: exactly one finished chunk goes out.
                pendingFinishedChunk = BuildFinishedChunk(choice.FinishReason, delta, toolCallsAccumulator);
            }
            else if (!hasToolCallDelta || delta.Length > 0)
            {
                // Tool-call-only deltas produce no text, so they yield nothing here.
                yield return new StreamChunk
                {
                    Delta = delta,
                    IsFinished = false
                };
            }
        }

        // No usage chunk followed the finish: emit it now. If the server sent no finish at all but
        // accumulated tool calls before closing the stream, emit those calls as the finish.
        if (pendingFinishedChunk != null)
            yield return pendingFinishedChunk;
        else if (toolCallsAccumulator.Count > 0)
            yield return BuildFinishedChunk("tool_calls", string.Empty, toolCallsAccumulator);
    }

    /// <summary>Builds the single finished chunk: the accumulated tool calls, or the text when there are none.</summary>
    private static StreamChunk BuildFinishedChunk(
        string? finishReason,
        string delta,
        IReadOnlyDictionary<int, ToolCallAccumulator> accumulator,
        TokenUsage? usage = null)
    {
        if (accumulator.Count == 0)
            return new StreamChunk { Delta = delta, IsFinished = true, FinishReason = finishReason, Usage = usage };

        var toolCalls = accumulator.Values
            .Select(acc => new ToolCall
            {
                Id = acc.Id,
                Name = acc.Name,
                Arguments = ParseArguments(acc.Arguments)
            })
            .ToList();

        return new StreamChunk { IsFinished = true, ToolCalls = toolCalls, FinishReason = finishReason, Usage = usage };
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

    // Recursively converts a PropertySchema into the plain-object shape the
    // OpenAI-style function-calling API expects, preserving nested "items"
    // (array element schema) and "properties"/"required" (object field
    // schemas) instead of the old inline lambda, which kept enum but still
    // silently dropped items/nested-properties for any non-trivial tool
    // parameter. Shared by OpenAiCompatibleProvider via inheritance.
    protected static object BuildJsonSchemaProperty(PropertySchema schema)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = schema.Type,
            ["description"] = schema.Description
        };

        if (schema.Enum is { Count: > 0 })
            result["enum"] = schema.Enum;

        if (schema.Items != null)
            result["items"] = BuildJsonSchemaProperty(schema.Items);

        if (schema.Properties is { Count: > 0 })
        {
            result["properties"] = schema.Properties.ToDictionary(
                p => p.Key,
                p => BuildJsonSchemaProperty(p.Value));
            if (schema.Required is { Count: > 0 })
                result["required"] = schema.Required;
        }

        return result;
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
                        p => BuildJsonSchemaProperty(p.Value)),
                    required = t.Parameters.Required
                }
            }
        }).ToList();

        var model = request.Options.Model ?? Config.DefaultModel;
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["tools"] = tools.Count > 0 ? tools : null,
            ["tool_choice"] = tools.Count > 0 ? "auto" : null,
            ["temperature"] = request.Options.Temperature,
            ["max_tokens"] = request.Options.MaxTokens,
            ["stream"] = stream,
            ["stream_options"] = stream ? new { include_usage = true } : null
        };

        // Reasoning models (o-series, gpt-5) use max_completion_tokens, reject a custom temperature, and take reasoning_effort.
        if (OpenAiReasoning.IsReasoningModel(model))
        {
            body.Remove("temperature");
            body.Remove("max_tokens");
            body["max_completion_tokens"] = request.Options.MaxTokens;
            var effort = OpenAiReasoning.Effort(model, request.Options.Reasoning);
            if (effort != null) body["reasoning_effort"] = effort;
        }
        return body;
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