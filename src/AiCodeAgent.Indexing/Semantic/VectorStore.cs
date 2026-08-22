using System.Collections.Concurrent;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Indexing.Semantic;

/// <summary>
/// In-memory vector store keyed by chunk id. Supports upsert, removal and
/// brute-force cosine similarity search. Suitable for moderately sized
/// codebases (tens of thousands of chunks); persistence is handled by
/// <see cref="SemanticIndex"/> via SQLite serialization.
/// </summary>
public sealed class VectorStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public void Upsert(CodeChunk chunk)
    {
        if (chunk.Embedding is null) return;
        _entries[chunk.Id] = new Entry(chunk, Normalize(chunk.Embedding));
    }

    public void UpsertRange(IEnumerable<CodeChunk> chunks)
    {
        foreach (var c in chunks) Upsert(c);
    }

    public void Remove(string chunkId) => _entries.TryRemove(chunkId, out _);

    public void RemoveFile(string filePath)
    {
        var toRemove = _entries.Values.Where(e => e.Chunk.FilePath == filePath).Select(e => e.Chunk.Id).ToList();
        foreach (var id in toRemove) _entries.TryRemove(id, out _);
    }

    public void Clear() => _entries.Clear();

    public bool Contains(string chunkId) => _entries.ContainsKey(chunkId);

    public IEnumerable<CodeChunk> AllChunks => _entries.Values.Select(e => e.Chunk);

    /// <summary>Brute-force cosine similarity search.</summary>
    public IReadOnlyList<SemanticSearchResult> Search(float[] queryVector, int topK)
    {
        var q = Normalize(queryVector);
        var results = new List<(Entry Entry, double Score)>(_entries.Count);

        foreach (var entry in _entries.Values)
        {
            var score = Dot(entry.Vector, q);
            results.Add((entry, score));
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results.Take(topK).Select(r => new SemanticSearchResult(r.Entry.Chunk, r.Score)).ToList();
    }

    private static float[] Normalize(float[] v)
    {
        var sum = 0f;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = MathF.Sqrt(sum);
        if (norm < 1e-9f) return v.ToArray();
        var r = new float[v.Length];
        for (var i = 0; i < v.Length; i++) r[i] = v[i] / norm;
        return r;
    }

    private static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var sum = 0f;
        for (var i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    private sealed record Entry(CodeChunk Chunk, float[] Vector);
}