using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Tracks live context-window usage for a session and decides when
/// compaction should be triggered.
/// </summary>
public interface IContextUsageTracker
{
    /// <summary>Current estimated token usage for the session.</summary>
    int CurrentTokens(string sessionId);

    /// <summary>Configured soft limit (trigger threshold) for the session.</summary>
    int SoftLimit(string sessionId);

    /// <summary>Hard cap (model context window) for the session.</summary>
    int HardLimit(string sessionId);

    /// <summary>Refresh cached usage from the context manager.</summary>
    Task RefreshAsync(string sessionId);

    /// <summary>Returns true when usage crosses the soft limit.</summary>
    bool ShouldCompact(string sessionId);

    /// <summary>Ratio (0..1) of current tokens to hard limit.</summary>
    double UsageRatio(string sessionId);
}

/// <summary>
/// Prevents repeated compaction cycles ("thrashing") when a session
/// keeps refilling the context window immediately after compaction.
/// </summary>
public interface IThrashingGuard
{
    /// <summary>Record a compaction attempt and return whether to proceed.</summary>
    bool TryBeginCompaction(string sessionId);

    /// <summary>Record a successful compaction.</summary>
    void RecordCompaction(string sessionId);

    /// <summary>Record a failed/rejected compaction attempt.</summary>
    void RecordFailure(string sessionId);

    /// <summary>True when the guard has blocked further compaction for the session.</summary>
    bool IsThrashing(string sessionId);

    /// <summary>Reset the guard state for a session (e.g. on /compact manual invoke).</summary>
    void Reset(string sessionId);
}

/// <summary>
/// Compacts a session's context by summarizing older messages into a
/// single system message, preserving the most recent messages.
/// </summary>
public interface IContextCompactor
{
    /// <summary>
    /// Compact the session context. Returns a result describing what happened.
    /// </summary>
    Task<CompactionResult> CompactAsync(
        string sessionId,
        CompactionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Options controlling a compaction pass.</summary>
public record CompactionOptions
{
    /// <summary>Target maximum tokens after compaction.</summary>
    public int TargetTokens { get; init; } = 16_000;

    /// <summary>Number of most-recent messages to always preserve verbatim.</summary>
    public int KeepRecentMessages { get; init; } = 6;

    /// <summary>
    /// Optional focus query / topic to bias the summary toward (e.g. the
    /// current user goal). When null, a generic summary is produced.
    /// </summary>
    public string? Focus { get; init; }

    /// <summary>When true, force compaction even if the guard would block it.</summary>
    public bool Force { get; init; }
}

/// <summary>Result of a compaction pass.</summary>
public record CompactionResult
{
    public bool Compacted { get; init; }
    public int MessagesBefore { get; init; }
    public int MessagesAfter { get; init; }
    public int TokensBefore { get; init; }
    public int TokensAfter { get; init; }
    public int TokensSaved => TokensBefore - TokensAfter;
    public string? Summary { get; init; }
    public string? Focus { get; init; }
    public bool ThrashingBlocked { get; init; }
    public string? Message { get; init; }
}