using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Anthropic;

public class AnthropicProvider : BaseHttpProvider
{
    private const string ApiVersion = "2023-06-01";
    private static readonly string[] Models =
    [
        "claude-opus-4-5", "claude-sonnet-4-5",
        "claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022",
        "claude-3-opus-20240229"
    ];

    public AnthropicProvider(ProviderConfiguration config, ILogger<AnthropicProvider> logger)
        : base(config, logger)
    {
        HttpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        HttpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey ?? string.Empty);
        HttpClient.DefaultRequestHeaders.Remove("Authorization");
    }

    public override string Name => "Anthropic";
    public override string[] SupportedModels => Models;

    public override async Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<AnthropicModelsResponse>(
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
        var response = await HttpClient.PostAsJsonAsync("/v1/messages", payload, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Anthropic API error: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(
            cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Empty response");

        return MapResponse(result);
    }

    public override async IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(request, stream: true);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(payload)
        };

        var response = await HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var toolCallsBuffer = new Dictionary<int, AnthropicToolUseBuffer>();
        var currentToolIndex = -1;

        // Anthropic streams usage in two places: message_start carries
        // input_tokens (and an initial output_tokens of 0), and message_delta
        // carries the final output_tokens plus the stop_reason. We accumulate
        // both and attach them to the terminal finished chunk so the
        // orchestrator (which breaks on IsFinished) actually records usage.
        var inputTokens = 0;
        var outputTokens = 0;
        var stopReason = "stop";

        await foreach (var line in ReadSseStreamAsync(response, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            AnthropicStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AnthropicStreamEvent>(line, JsonOptions.Default);
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Failed to parse Anthropic streaming event: {Line}", line);
                continue;
            }

            if (evt == null) continue;

            switch (evt.Type)
            {
                case "message_start":
                    inputTokens = evt.Message?.Usage?.InputTokens ?? 0;
                    outputTokens = evt.Message?.Usage?.OutputTokens ?? 0;
                    break;

                case "content_block_start":
                    if (evt.ContentBlock?.Type == "tool_use")
                    {
                        currentToolIndex = evt.Index;
                        toolCallsBuffer[currentToolIndex] = new AnthropicToolUseBuffer
                        {
                            Id = evt.ContentBlock.Id ?? string.Empty,
                            Name = evt.ContentBlock.Name ?? string.Empty
                        };
                    }
                    break;

                case "content_block_delta":
                    if (evt.Delta?.Type == "text_delta")
                    {
                        yield return new StreamChunk { Delta = evt.Delta.Text ?? string.Empty };
                    }
                    else if (evt.Delta?.Type == "input_json_delta" && currentToolIndex >= 0)
                    {
                        toolCallsBuffer[currentToolIndex].InputJson += evt.Delta.PartialJson ?? string.Empty;
                    }
                    break;

                case "message_delta":
                    // message_delta carries the final stop_reason and the
                    // cumulative output_tokens count.
                    if (evt.Delta?.StopReason != null)
                        stopReason = evt.Delta.StopReason;
                    if (evt.Delta?.Usage != null)
                        outputTokens = evt.Delta.Usage.OutputTokens;
                    break;

                case "message_stop":
                    var usage = new TokenUsage
                    {
                        PromptTokens = inputTokens,
                        CompletionTokens = outputTokens
                    };

                    if (toolCallsBuffer.Count > 0)
                    {
                        var toolCalls = toolCallsBuffer.Values.Select(buf => new ToolCall
                        {
                            Id = buf.Id,
                            Name = buf.Name,
                            Arguments = ParseArguments(buf.InputJson)
                        }).ToList();

                        yield return new StreamChunk
                        {
                            IsFinished = true,
                            ToolCalls = toolCalls,
                            FinishReason = stopReason == "stop" ? "tool_use" : stopReason,
                            Usage = usage
                        };
                    }
                    else
                    {
                        yield return new StreamChunk
                        {
                            IsFinished = true,
                            FinishReason = stopReason,
                            Usage = usage
                        };
                    }
                    break;
            }
        }
    }

    private object BuildPayload(CompletionRequest request, bool stream)
    {
        var messages = request.Messages
            .Where(m => m.Role != MessageRole.System)
            .Select(BuildMessage)
            .ToList();

        var tools = request.Tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            input_schema = new
            {
                type = t.Parameters.Type,
                properties = t.Parameters.Properties.ToDictionary(
                    p => p.Key,
                    p => (object)new { type = p.Value.Type, description = p.Value.Description }),
                required = t.Parameters.Required
            }
        }).ToList();

        return new
        {
            model = request.Options.Model ?? Config.DefaultModel,
            system = request.SystemPrompt,
            messages,
            tools = tools.Count > 0 ? tools : null,
            max_tokens = request.Options.MaxTokens,
            temperature = request.Options.Temperature,
            stream
        };
    }

    private static object BuildMessage(Message msg) => msg.Role switch
    {
        MessageRole.User when msg.Images is { Count: > 0 } =>
            new { role = "user", content = BuildUserContentBlocks(msg) },
        MessageRole.User => new { role = "user", content = msg.Content },
        MessageRole.Assistant when msg.ToolCalls?.Count > 0 => new
        {
            role = "assistant",
            content = msg.ToolCalls.Select(tc => (object)new
            {
                type = "tool_use",
                id = tc.Id,
                name = tc.Name,
                input = tc.Arguments
            }).ToArray()
        },
        MessageRole.Tool => new
        {
            role = "user",
            content = new[]
            {
                new
                {
                    type = "tool_result",
                    tool_use_id = msg.ToolCallId,
                    content = msg.Content
                }
            }
        },
        _ => new { role = "assistant", content = msg.Content }
    };

    /// <summary>
    /// Builds Anthropic's mixed content-block array for a user message that
    /// carries image attachments: one image block per attachment, followed
    /// by a trailing text block (Anthropic wants images before the text that
    /// refers to them).
    /// </summary>
    private static List<object> BuildUserContentBlocks(Message msg)
    {
        var blocks = new List<object>();
        foreach (var image in msg.Images!)
        {
            blocks.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = image.MediaType,
                    data = image.Base64Data
                }
            });
        }
        if (!string.IsNullOrEmpty(msg.Content))
            blocks.Add(new { type = "text", text = msg.Content });
        return blocks;
    }

    private static CompletionResponse MapResponse(AnthropicResponse result)
    {
        var textContent = string.Join("", result.Content
            .Where(c => c.Type == "text")
            .Select(c => c.Text ?? string.Empty));

        var toolCalls = result.Content
            .Where(c => c.Type == "tool_use")
            .Select(c => new ToolCall
            {
                Id = c.Id ?? string.Empty,
                Name = c.Name ?? string.Empty,
                Arguments = c.Input ?? new()
            })
            .ToList();

        return new CompletionResponse
        {
            Content = textContent,
            ToolCalls = toolCalls,
            Usage = new TokenUsage
            {
                PromptTokens = result.Usage?.InputTokens ?? 0,
                CompletionTokens = result.Usage?.OutputTokens ?? 0
            },
            Model = result.Model ?? string.Empty,
            StopReason = result.StopReason ?? string.Empty
        };
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            return string.IsNullOrEmpty(json)
                ? new()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
        }
        catch { return new(); }
    }

    private class AnthropicToolUseBuffer
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string InputJson { get; set; } = string.Empty;
    }
}
