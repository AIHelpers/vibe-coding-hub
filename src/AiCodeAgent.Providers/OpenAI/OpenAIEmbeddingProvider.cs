using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.OpenAI;

/// <summary>
/// OpenAI embeddings API implementation of <see cref="IEmbeddingProvider"/>.
/// Calls /v1/embeddings and returns L2-normalized vectors. Falls back to
/// <see cref="NullEmbeddingProvider"/> semantics on empty input.
/// </summary>
public sealed class OpenAiEmbeddingProvider : BaseHttpProvider, IEmbeddingProvider
{
    private const string DefaultEmbeddingModel = "text-embedding-3-small";
    private const int DefaultEmbeddingDimensions = 1536;

    private static readonly string[] SupportedEmbeddingModels =
        ["text-embedding-3-small", "text-embedding-3-large", "text-embedding-ada-002"];

    private readonly string _embeddingModel;
    private readonly int _dimensions;

    public OpenAiEmbeddingProvider(ProviderConfiguration config, ILogger<OpenAiEmbeddingProvider> logger)
        : base(config, logger)
    {
        _embeddingModel = config.Headers.TryGetValue("embedding-model", out var em) && !string.IsNullOrWhiteSpace(em)
            ? em
            : DefaultEmbeddingModel;

        _dimensions = _embeddingModel switch
        {
            "text-embedding-3-large" => 3072,
            "text-embedding-ada-002" => 1536,
            _ => DefaultEmbeddingDimensions
        };
    }

    public override string Name => "OpenAI-Embeddings";
    public override string[] SupportedModels => SupportedEmbeddingModels;

    int IEmbeddingProvider.Dimensions => _dimensions;

    public override Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenAiEmbeddingProvider does not support chat completions.");

    public override IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenAiEmbeddingProvider does not support streaming completions.");

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[_dimensions];

        var payload = new EmbeddingRequest
        {
            Model = _embeddingModel,
            Input = text
        };

        var response = await HttpClient.PostAsJsonAsync("/v1/embeddings", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Empty embedding response");

        return ExtractVector(result);
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];

        var payload = new EmbeddingBatchRequest
        {
            Model = _embeddingModel,
            Input = texts.ToArray()
        };

        var response = await HttpClient.PostAsJsonAsync("/v1/embeddings", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Empty embedding response");

        if (result.Data is null || result.Data.Count == 0)
            throw new InvalidOperationException("Embedding response contained no data");

        var vectors = new float[result.Data.Count][];
        foreach (var item in result.Data)
        {
            if (item.Index >= 0 && item.Index < vectors.Length)
                vectors[item.Index] = item.Embedding ?? [];
        }

        return vectors;
    }

    private float[] ExtractVector(EmbeddingResponse result)
    {
        if (result.Data is not { Count: > 0 })
            throw new InvalidOperationException("Embedding response contained no data");
        return result.Data[0].Embedding ?? [];
    }

    // ---- Request / response DTOs ----

    private sealed class EmbeddingRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("input")] public string Input { get; set; } = string.Empty;
    }

    private sealed class EmbeddingBatchRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("input")] public string[] Input { get; set; } = [];
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")] public List<EmbeddingItem>? Data { get; set; }
    }

    private sealed class EmbeddingItem
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}