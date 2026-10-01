using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Indexing.Semantic;

/// <summary>
/// Wraps an <see cref="IEmbeddingProvider"/> and adds batching, caching and
/// normalization conveniences for the semantic indexing pipeline.
/// </summary>
public sealed class EmbeddingService
{
    private readonly IEmbeddingProvider _provider;
    private readonly ILogger<EmbeddingService>? _logger;
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly int _batchSize;

    public EmbeddingService(IEmbeddingProvider provider, int batchSize = 32, ILogger<EmbeddingService>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _batchSize = batchSize;
        _logger = logger;
    }

    public int Dimensions => _provider.Dimensions;
    public string ProviderName => _provider.Name;

    /// <summary>Embeds a single query string, using a cache for repeated queries.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[_provider.Dimensions];

        if (_cache.TryGetValue(text, out var cached))
            return cached;

        var vec = await _provider.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        Normalize(vec);
        _cache.TryAdd(text, vec);
        return vec;
    }

    /// <summary>
    /// Embeds a batch of chunk texts and assigns the resulting vectors back to
    /// each <see cref="CodeChunk.Embedding"/> field.
    /// </summary>
    public async Task EmbedChunksAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;

        for (var start = 0; start < chunks.Count; start += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(_batchSize, chunks.Count - start);
            var texts = new string[count];
            for (var i = 0; i < count; i++)
                texts[i] = chunks[start + i].Content;

            try
            {
                var vectors = await _provider.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < count; i++)
                {
                    var vec = vectors[i];
                    Normalize(vec);
                    chunks[start + i].Embedding = vec;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Embedding batch failed at offset {Offset}; falling back to per-chunk embedding", start);
                // Fallback: embed individually so a single transient failure doesn't kill the batch
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = chunks[start + i];
                    try
                    {
                        var vec = await _provider.EmbedAsync(chunk.Content, cancellationToken).ConfigureAwait(false);
                        Normalize(vec);
                        chunk.Embedding = vec;
                    }
                    catch (Exception ex2) when (ex2 is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex2, "Failed to embed chunk {File}:{Start}", chunk.FilePath, chunk.StartLine);
                        chunk.Embedding = new float[_provider.Dimensions];
                    }
                }
            }
        }
    }

    private static void Normalize(float[] v)
    {
        var sum = 0f;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = MathF.Sqrt(sum);
        if (norm < 1e-9f) return;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }
}