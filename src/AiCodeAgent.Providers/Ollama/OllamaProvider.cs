using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Ollama;

public class OllamaProvider : BaseHttpProvider
{
    public OllamaProvider(ProviderConfiguration config, ILogger<OllamaProvider> logger)
        : base(config, logger)
    {
        // Ollama's local server doesn't use Bearer token authentication.
        // Sending an unexpected Authorization header causes a 403 Forbidden response.
        HttpClient.DefaultRequestHeaders.Remove("Authorization");
    }

    public override string Name => "Ollama";
    public override string[] SupportedModels => _cachedModels;

    private string[] _cachedModels = ["llama3.1", "codellama", "mistral", "deepseek-coder"];

    public override async Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<OllamaModelsResponse>(
                "/api/tags", cancellationToken);
            _cachedModels = response?.Models.Select(m => m.Name).ToArray() ?? _cachedModels;
            return _cachedModels;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Failed to fetch available models from Ollama");
            return _cachedModels;
        }
    }

    public override async Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = request.Options.Model ?? Config.DefaultModel,
            messages = BuildMessages(request),
            tools = BuildTools(request.Tools),
            stream = false,
            options = new
            {
                temperature = request.Options.Temperature,
                num_predict = request.Options.MaxTokens
            }
        };

        var response = await HttpClient.PostAsJsonAsync("/api/chat", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Ollama API error ({(int)response.StatusCode}): {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Empty response");

        return MapResponse(result);
    }

    public override async IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = request.Options.Model ?? Config.DefaultModel;
        Logger.LogInformation("Starting Ollama stream to model {Model} at {BaseUrl}", model, Config.BaseUrl);

        var payload = new
        {
            model = model,
            messages = BuildMessages(request),
            tools = BuildTools(request.Tools),
            stream = true,
            options = new
            {
                temperature = request.Options.Temperature,
                num_predict = request.Options.MaxTokens
            }
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(payload)
        };

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl} for streaming. Is Ollama running?", Config.BaseUrl);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Logger.LogError("Ollama streaming API returned {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Ollama API error ({(int)response.StatusCode}): {errorBody}");
        }

        Logger.LogInformation("Ollama stream connected, reading response stream...");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var chunkCount = 0;
        var hasYieldedContent = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                Logger.LogWarning("Ollama stream read interrupted: {Message}", ex.Message);
                throw;
            }

            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            chunkCount++;
            OllamaChatResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions.Default);
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Failed to parse Ollama streaming chunk #{Count}: {Line}", chunkCount, line);
                continue;
            }

            if (chunk == null) continue;

            // Ollama emits tool_calls in a chunk where done=false and then keeps
            // the stream open. The orchestrator only captures tool calls when
            // IsFinished is true, so emit them immediately as a finished chunk
            // to let the orchestrator break out and execute the tools.
            if (chunk.Message?.ToolCalls?.Count > 0)
            {
                Logger.LogInformation("Ollama stream received {Count} tool calls (done={Done}) — emitting as finished", chunk.Message.ToolCalls.Count, chunk.Done);
                var toolCalls = chunk.Message.ToolCalls.Select(tc => new ToolCall
                {
                    // Ollama doesn't provide tool call IDs, so generate unique ones
                    Id = GenerateToolCallId(),
                    Name = tc.Function.Name,
                    Arguments = tc.Function.Arguments
                }).ToList();

                yield return new StreamChunk
                {
                    IsFinished = true,
                    ToolCalls = toolCalls,
                    FinishReason = "tool_calls"
                };
                yield break;
            }

            var delta = chunk.Message?.Content ?? string.Empty;
            if (!string.IsNullOrEmpty(delta))
                hasYieldedContent = true;

            if (chunk.Done)
            {
                Logger.LogInformation("Ollama stream completed after {Count} chunks (done_reason={Reason}, hasContent={HasContent})",
                    chunkCount, chunk.DoneReason, hasYieldedContent);
                yield return new StreamChunk
                {
                    Delta = delta,
                    IsFinished = true,
                    FinishReason = chunk.DoneReason
                };
                yield break;
            }

            yield return new StreamChunk
            {
                Delta = delta,
                IsFinished = false
            };
        }

        Logger.LogInformation("Ollama stream ended after {Count} total chunks", chunkCount);
    }

    private static List<object> BuildMessages(CompletionRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
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
                                // Ollama expects arguments as a JSON object, not a string
                                arguments = tc.Arguments
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
                default:
                    messages.Add(new { role = msg.Role.ToString().ToLower(), content = msg.Content });
                    break;
            }
        }
        return messages;
    }

    private static object? BuildTools(List<ToolDefinition> tools)
    {
        if (tools.Count == 0) return null;
        return tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters
            }
        }).ToList();
    }

    private static CompletionResponse MapResponse(OllamaChatResponse result)
    {
        var toolCalls = result.Message?.ToolCalls?.Select(tc => new ToolCall
        {
            // Ollama doesn't provide tool call IDs, so generate unique ones
            Id = GenerateToolCallId(),
            Name = tc.Function.Name,
            Arguments = tc.Function.Arguments
        }).ToList() ?? new List<ToolCall>();

        return new CompletionResponse
        {
            Content = result.Message?.Content ?? string.Empty,
            ToolCalls = toolCalls,
            Usage = new TokenUsage
            {
                PromptTokens = result.PromptEvalCount ?? 0,
                CompletionTokens = result.EvalCount ?? 0
            },
            StopReason = result.DoneReason ?? string.Empty
        };
    }

    private static string GenerateToolCallId()
    {
        Span<byte> randomBytes = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
        return "ollama_" + Convert.ToHexString(randomBytes).ToLower();
    }
}