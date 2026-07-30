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
        : base(config, logger) { }

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
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Empty response");

        return MapResponse(result);
    }

    public override async IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = request.Options.Model ?? Config.DefaultModel,
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

        var response = await HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaChatResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions.Default);
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Failed to parse Ollama streaming chunk: {Line}", line);
                continue;
            }

            if (chunk == null) continue;

            if (chunk.Message?.ToolCalls?.Count > 0)
            {
                var toolCalls = chunk.Message.ToolCalls.Select(tc => new ToolCall
                {
                    // Ollama doesn't provide tool call IDs, so generate unique ones
                    Id = GenerateToolCallId(),
                    Name = tc.Function.Name,
                    Arguments = tc.Function.Arguments
                }).ToList();

                yield return new StreamChunk
                {
                    IsFinished = chunk.Done,
                    ToolCalls = toolCalls,
                    FinishReason = "tool_calls"
                };
            }
            else
            {
                yield return new StreamChunk
                {
                    Delta = chunk.Message?.Content ?? string.Empty,
                    IsFinished = chunk.Done
                };
            }
        }
    }

    private static List<object> BuildMessages(CompletionRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            messages.Add(new { role = msg.Role.ToString().ToLower(), content = msg.Content });
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