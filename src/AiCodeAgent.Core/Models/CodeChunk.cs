namespace AiCodeAgent.Core.Models;

/// <summary>
/// A chunk of source code (or text) that is embedded and indexed for
/// semantic retrieval.
/// </summary>
public sealed record CodeChunk
{
    /// <summary>Stable id (hash of file+start line).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Absolute path of the source file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>1-based start line.</summary>
    public int StartLine { get; init; }

    /// <summary>1-based end line (inclusive).</summary>
    public int EndLine { get; init; }

    /// <summary>Language id (e.g. "csharp", "python").</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Chunk text content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Optional symbol/scope name for context (e.g. class/method).</summary>
    public string? Symbol { get; init; }

    /// <summary>Last write time of the source file when chunked (UTC ticks).</summary>
    public long FileWriteTimeUtcTicks { get; init; }

    /// <summary>Embedding vector (populated after embedding).</summary>
    public float[]? Embedding { get; set; }
}

/// <summary>A scored retrieval result.</summary>
public sealed record SemanticSearchResult(CodeChunk Chunk, double Score);