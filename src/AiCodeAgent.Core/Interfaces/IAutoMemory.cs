using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Automatically captures and persists user preferences, conventions, and
/// corrections across sessions (Feature 05 — Auto Memory).
/// </summary>
public interface IAutoMemory
{
    /// <summary>Record a learning to the in-memory buffer and persist it.</summary>
    Task CaptureAsync(string sessionId, Learning learning, CancellationToken cancellationToken = default);

    /// <summary>Load the bounded memory content (first 200 lines or 25KB).</summary>
    Task<string> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persist the current in-memory learnings to disk.</summary>
    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Return the path to the memory file.</summary>
    string GetFilePath();
}

/// <summary>A single captured learning entry.</summary>
public record Learning
{
    /// <summary>Human-readable summary of the preference/convention.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>When the learning was captured (UTC).</summary>
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Session that produced the learning.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Optional category (preference, convention, correction).</summary>
    public string? Category { get; init; }

    /// <summary>Render the learning as a markdown list item.</summary>
    public string ToMarkdownItem() => $"- [{CapturedAt:yyyy-MM-dd}] {Text}";
}