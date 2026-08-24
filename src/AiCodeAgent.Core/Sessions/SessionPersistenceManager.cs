using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// Wraps <see cref="ISessionStore"/> to track the active session id and
/// support resume (same id, reload history) and fork (new id, copy history).
/// </summary>
public sealed class SessionPersistenceManager
{
    private readonly ISessionStore _store;
    private readonly ILogger<SessionPersistenceManager>? _logger;

    public string ActiveSessionId { get; private set; }

    public SessionPersistenceManager(ISessionStore store, ILogger<SessionPersistenceManager>? logger = null)
    {
        _store = store;
        _logger = logger;
        ActiveSessionId = NewId();
    }

    /// <summary>Start a brand-new session.</summary>
    public string CreateNew()
    {
        ActiveSessionId = NewId();
        return ActiveSessionId;
    }

    /// <summary>Resume an existing session by id, loading its entries.</summary>
    public async Task<(string SessionId, IReadOnlyList<SessionEntry> History)> ResumeAsync(string sessionId, CancellationToken ct = default)
    {
        var history = new List<SessionEntry>();
        await foreach (var entry in _store.ReadAsync(sessionId, ct).ConfigureAwait(false))
            history.Add(entry);

        ActiveSessionId = sessionId;
        _logger?.LogInformation("Resumed session {SessionId} with {Count} entries", sessionId, history.Count);
        return (sessionId, history);
    }

    /// <summary>
    /// Fork an existing session: copy its history into a new session id.
    /// The original session file is left unchanged.
    /// </summary>
    public async Task<string> ForkAsync(string sourceSessionId, CancellationToken ct = default)
    {
        var newId = NewId();
        await foreach (var entry in _store.ReadAsync(sourceSessionId, ct).ConfigureAwait(false))
            await _store.AppendAsync(newId, entry, ct).ConfigureAwait(false);

        ActiveSessionId = newId;
        _logger?.LogInformation("Forked session {Source} -> {NewId}", sourceSessionId, newId);
        return newId;
    }

    /// <summary>Append a single entry to the active session.</summary>
    public Task AppendAsync(SessionEntry entry, CancellationToken ct = default)
        => _store.AppendAsync(ActiveSessionId, entry, ct);

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}