using System.Collections.ObjectModel;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Thread-safe registry of <see cref="AgentSession"/> instances for the
/// multi-agent session dashboard (Feature 5). Supports create/get/remove,
/// an active-session cursor, and lifecycle helpers (pause/resume/cancel).
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSessionId;
    private bool _disposed;

    /// <summary>Observable list of sessions for UI binding.</summary>
    public ObservableCollection<AgentSession> Sessions { get; } = new();

    /// <summary>The currently focused session, or null.</summary>
    public AgentSession? ActiveSession
    {
        get
        {
            lock (_lock)
            {
                if (_activeSessionId == null) return null;
                return _sessions.TryGetValue(_activeSessionId, out var s) ? s : null;
            }
        }
    }

    /// <summary>Raised when the active session changes (string id or null).</summary>
    public event Action<string?>? ActiveSessionChanged;

    /// <summary>Create and register a new session with the given task.</summary>
    public AgentSession CreateSession(string task)
    {
        var id = GenerateId();
        var session = new AgentSession(id, task);
        lock (_lock)
        {
            _sessions[id] = session;
        }
        Sessions.Add(session);
        return session;
    }

    /// <summary>Get a session by id, or null.</summary>
    public AgentSession? GetSession(string id)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(id, out var s) ? s : null;
        }
    }

    /// <summary>Set the active session by id. Pass null to clear.</summary>
    public void SetActive(string? id)
    {
        lock (_lock)
        {
            if (id != null && !_sessions.ContainsKey(id))
                throw new KeyNotFoundException($"Session '{id}' not found.");
            _activeSessionId = id;
        }
        ActiveSessionChanged?.Invoke(id);
    }

    /// <summary>Remove and dispose a session by id.</summary>
    public bool RemoveSession(string id)
    {
        AgentSession? session;
        lock (_lock)
        {
            if (!_sessions.Remove(id, out session))
                return false;
            if (_activeSessionId == id)
                _activeSessionId = null;
        }
        Sessions.Remove(session);
        session.Dispose();
        if (_activeSessionId == null)
            ActiveSessionChanged?.Invoke(null);
        return true;
    }

    /// <summary>Pause the session (no-op if not running).</summary>
    public void Pause(string id)
    {
        GetSession(id)?.Pause();
    }

    /// <summary>Resume the session (no-op if not paused).</summary>
    public void Resume(string id)
    {
        GetSession(id)?.Resume();
    }

    /// <summary>Cancel the session permanently.</summary>
    public void Cancel(string id)
    {
        GetSession(id)?.Cancel();
    }

    /// <summary>Cancel all active sessions.</summary>
    public void CancelAll()
    {
        List<AgentSession> snapshot;
        lock (_lock)
        {
            snapshot = _sessions.Values.ToList();
        }
        foreach (var s in snapshot)
        {
            if (s.Status == SessionStatus.Running || s.Status == SessionStatus.Paused)
                s.Cancel();
        }
    }

    private static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<AgentSession> snapshot;
        lock (_lock)
        {
            snapshot = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var s in snapshot)
            s.Dispose();
    }
}