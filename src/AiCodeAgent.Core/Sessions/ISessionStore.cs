namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// Append-only JSONL-backed session log store.
/// </summary>
public interface ISessionStore
{
    /// <summary>Append a single entry to the session's JSONL file.</summary>
    Task AppendAsync(string sessionId, SessionEntry entry, CancellationToken ct = default);

    /// <summary>Stream all entries for a session (oldest first). Empty if missing.</summary>
    IAsyncEnumerable<SessionEntry> ReadAsync(string sessionId, CancellationToken ct = default);

    /// <summary>List sessions for a worktree (newest first).</summary>
    Task<IReadOnlyList<SessionMetadata>> ListAsync(string worktree, CancellationToken ct = default);

    /// <summary>Delete a session file.</summary>
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>Lightweight metadata about a stored session.</summary>
public sealed class SessionMetadata
{
    public string SessionId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public long SizeBytes { get; init; }
    public int EntryCount { get; init; }
}