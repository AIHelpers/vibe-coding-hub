using System.Collections.ObjectModel;

namespace AiCodeAgent.Core.Agent;

/// <summary>A chat message in a session's history (UI-friendly).</summary>
public sealed class SessionMessage
{
    public string Role { get; init; } = string.Empty; // User|Assistant|System
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Lifecycle status of an agent session.
/// </summary>
public enum SessionStatus
{
    /// <summary>Created but not started yet.</summary>
    Pending,
    /// <summary>Currently running (streaming/processing).</summary>
    Running,
    /// <summary>Paused by the user; can be resumed.</summary>
    Paused,
    /// <summary>Completed successfully.</summary>
    Completed,
    /// <summary>Cancelled by the user.</summary>
    Cancelled,
    /// <summary>Failed with an error.</summary>
    Error
}

/// <summary>
/// A file-scope declaration restricting which paths a session may touch.
/// </summary>
public sealed class FileScope
{
    private readonly List<string> _allowedPaths = new();

    /// <summary>
    /// When empty, the session is unrestricted (may touch any file).
    /// When non-empty, only paths under these roots are in-scope.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths => _allowedPaths;

    public void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (!_allowedPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            _allowedPaths.Add(normalized);
    }

    /// <summary>Returns true when <paramref name="filePath"/> is within this scope.</summary>
    public bool IsInScope(string filePath)
    {
        if (_allowedPaths.Count == 0) return true; // unrestricted
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var normalized = filePath.Replace('\\', '/');
        foreach (var root in _allowedPaths)
        {
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Encapsulates per-session state for the multi-agent dashboard (Feature 5):
/// identity, task, status, chat history, file-scope, and cancellation.
/// Thread-safe with respect to status changes; chat history is mutated on
/// the UI thread by the owning ViewModel.
/// </summary>
public sealed class AgentSession
{
    private readonly object _lock = new();
    private SessionStatus _status = SessionStatus.Pending;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _pauseLinkCts;

    /// <summary>Unique session identifier.</summary>
    public string Id { get; }

    /// <summary>The user-supplied task/prompt for this session.</summary>
    public string Task { get; init; }

    /// <summary>Short display title (defaults to a truncated task).</summary>
    public string Title => string.IsNullOrWhiteSpace(Task)
        ? Id[..8]
        : (Task.Length > 48 ? Task[..48] + "..." : Task);

    /// <summary>When the session was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>Last time the status or chat history changed.</summary>
    public DateTime UpdatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Per-session chat message history (mutated by the UI layer).</summary>
    public ObservableCollection<SessionMessage> ChatHistory { get; } = new();

    /// <summary>File-scope restricting which paths this session may edit.</summary>
    public FileScope FileScope { get; } = new();

    /// <summary>Current lifecycle status (thread-safe).</summary>
    public SessionStatus Status
    {
        get { lock (_lock) return _status; }
        private set
        {
            lock (_lock)
            {
                _status = value;
                UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Human-readable status for binding.</summary>
    public string StatusText => Status.ToString();

    public AgentSession(string id, string task)
    {
        Id = id;
        Task = task ?? string.Empty;
        _cts = new CancellationTokenSource();
    }

    /// <summary>The effective cancellation token for agent work in this session.</summary>
    public CancellationToken CancellationToken
    {
        get
        {
            lock (_lock)
            {
                if (_cts == null) return new CancellationToken(true);
                if (_pauseLinkCts != null)
                {
                    // Linked so pause-CTS cancel (pause) and main-CTS cancel (cancel) both apply
                    return CancellationTokenSource.CreateLinkedTokenSource(
                        _cts.Token, _pauseLinkCts.Token).Token;
                }
                return _cts.Token;
            }
        }
    }

    /// <summary>Mark the session as started (Running).</summary>
    public void MarkRunning() => Status = SessionStatus.Running;

    /// <summary>Mark the session as completed.</summary>
    public void MarkCompleted() => Status = SessionStatus.Completed;

    /// <summary>Mark the session as failed.</summary>
    public void MarkError() => Status = SessionStatus.Error;

    /// <summary>
    /// Pause the session: a linked cancellation token is cancelled, which
    /// interrupts in-flight agent work; the session can later be resumed.
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_status != SessionStatus.Running) return;
            _pauseLinkCts?.Dispose();
            _pauseLinkCts = new CancellationTokenSource();
            _pauseLinkCts.Cancel();
        }
        Status = SessionStatus.Paused;
    }

    /// <summary>Resume a paused session by creating a fresh pause-link token.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_status != SessionStatus.Paused) return;
            _pauseLinkCts?.Dispose();
            _pauseLinkCts = new CancellationTokenSource();
        }
        Status = SessionStatus.Running;
    }

    /// <summary>Cancel the session permanently.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _pauseLinkCts?.Cancel();
        }
        Status = SessionStatus.Cancelled;
    }

    /// <summary>Dispose resources held by this session.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
            _pauseLinkCts?.Dispose();
            _pauseLinkCts = null;
        }
    }
}