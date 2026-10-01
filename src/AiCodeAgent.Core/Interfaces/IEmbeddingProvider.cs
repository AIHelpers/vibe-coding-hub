namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Generates embeddings for text inputs. Implementations may call a
/// provider embedding endpoint or run a local model (e.g. ONNX).
/// </summary>
public interface IEmbeddingProvider
{
    string Name { get; }

    /// <summary>Dimensionality of the vectors produced by this provider.</summary>
    int Dimensions { get; }

    /// <summary>Generate an embedding for a single text input.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Generate embeddings for a batch of text inputs.</summary>
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>Null-object embedding provider used when no real provider is configured.
/// Produces deterministic hash-based pseudo-embeddings so retrieval still functions
/// (poorly) for development/demo scenarios.</summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public const int DefaultDimensions = 64;

    public string Name => "null";
    public int Dimensions => DefaultDimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(HashEmbed(text));

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => Task.FromResult(texts.Select(HashEmbed).ToArray());

    private static float[] HashEmbed(string text)
    {
        var vec = new float[DefaultDimensions];
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
            var slot = (int)(hash % (uint)DefaultDimensions);
            vec[slot] += (hash % 2u) == 0 ? -1f : 1f;
        }
        Normalize(vec);
        return vec;
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