using System.Collections.Concurrent;
using AiCodeAgent.Context.Persistence;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Context;

/// <summary>
/// An <see cref="IContextManager"/> that survives restarts: it keeps the working copy in an inner (in-memory) manager,
/// which does the token counting and trimming, and writes every change through to a <see cref="ChatSessionStore"/>.
/// A session is loaded from disk the first time it is touched. Also the <see cref="ISessionCatalog"/> behind the
/// resume banner and the session list. Writes are best-effort: a failing disk is logged and never stops a chat.
/// </summary>
public sealed class PersistentContextManager : IContextManager, ISessionCatalog
{
    private readonly IContextManager _inner;
    private readonly ChatSessionStore _store;
    private readonly Func<string> _workingDirectory;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, string> _directories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _loaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <param name="workingDirectory">The current project folder; new sessions are stored under it.</param>
    public PersistentContextManager(IContextManager inner, ChatSessionStore store, Func<string> workingDirectory, ILogger? logger = null)
    {
        _inner = inner;
        _store = store;
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    // ----- IContextManager -----

    public async Task<List<Message>> GetContextAsync(string sessionId)
    {
        await EnsureLoadedAsync(sessionId).ConfigureAwait(false);
        return await _inner.GetContextAsync(sessionId).ConfigureAwait(false);
    }

    public async Task AddMessageAsync(string sessionId, Message message)
    {
        await EnsureLoadedAsync(sessionId).ConfigureAwait(false);
        await _inner.AddMessageAsync(sessionId, message).ConfigureAwait(false);
        if (!ChatSessionStore.IsPersistableId(sessionId)) return;

        try { await _store.AppendMessageAsync(DirectoryOf(sessionId), sessionId, message).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Could not store a message of session {SessionId}", sessionId); }
    }

    public async Task<int> GetTokenCountAsync(string sessionId)
    {
        await EnsureLoadedAsync(sessionId).ConfigureAwait(false);
        return await _inner.GetTokenCountAsync(sessionId).ConfigureAwait(false);
    }

    public async Task TrimContextAsync(string sessionId, int maxTokens)
    {
        await EnsureLoadedAsync(sessionId).ConfigureAwait(false);
        var before = await _inner.GetContextAsync(sessionId).ConfigureAwait(false);
        await _inner.TrimContextAsync(sessionId, maxTokens).ConfigureAwait(false);
        if (!ChatSessionStore.IsPersistableId(sessionId)) return;

        var after = await _inner.GetContextAsync(sessionId).ConfigureAwait(false);
        if (after.Count == before.Count && after.Sum(m => m.TokenCount) == before.Sum(m => m.TokenCount)) return;

        try { await _store.RewriteAsync(DirectoryOf(sessionId), sessionId, after).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Could not store the trimmed session {SessionId}", sessionId); }
    }

    public async Task ClearAsync(string sessionId)
    {
        await _inner.ClearAsync(sessionId).ConfigureAwait(false);
        // Nothing left to load for this id: what is written from here on starts after the reset marker.
        _loaded[sessionId] = true;
        if (!ChatSessionStore.IsPersistableId(sessionId)) return;

        try { await _store.AppendResetAsync(DirectoryOf(sessionId), sessionId).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Could not store the reset of session {SessionId}", sessionId); }
    }

    public async Task<IReadOnlyList<string>> GetSessionsAsync()
    {
        var ids = new HashSet<string>(await _inner.GetSessionsAsync().ConfigureAwait(false), StringComparer.Ordinal);
        foreach (var info in await ListAsync().ConfigureAwait(false))
            ids.Add(info.SessionId);
        return ids.ToList();
    }

    // ----- ISessionCatalog -----

    public async Task<IReadOnlyList<ChatSessionInfo>> ListAsync(CancellationToken ct = default)
    {
        var all = await _store.ListAsync(_workingDirectory(), ct).ConfigureAwait(false);
        return all.Where(i => i.MessageCount > 0).OrderByDescending(i => i.UpdatedAt).ToList();
    }

    public async Task<ChatSessionInfo?> GetLatestAsync(CancellationToken ct = default) =>
        (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault();

    public Task RenameAsync(string sessionId, string title, CancellationToken ct = default) =>
        ChatSessionStore.IsPersistableId(sessionId)
            ? _store.SetTitleAsync(DirectoryOf(sessionId), sessionId, title.Trim(), ct)
            : Task.CompletedTask;

    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        await _inner.ClearAsync(sessionId).ConfigureAwait(false);
        _loaded.TryRemove(sessionId, out _);
        if (ChatSessionStore.IsPersistableId(sessionId))
            _store.Delete(DirectoryOf(sessionId), sessionId);
        _directories.TryRemove(sessionId, out _);
    }

    public Task RecordCostAsync(string sessionId, decimal costUsd, CancellationToken ct = default) =>
        ChatSessionStore.IsPersistableId(sessionId)
            ? _store.SetCostAsync(DirectoryOf(sessionId), sessionId, costUsd, ct)
            : Task.CompletedTask;

    // ----- internals -----

    /// <summary>The working directory a session lives under: where it was first seen, so a later folder change does not move it.</summary>
    private string DirectoryOf(string sessionId) => _directories.GetOrAdd(sessionId, _ => _workingDirectory());

    private async Task EnsureLoadedAsync(string sessionId)
    {
        if (_loaded.ContainsKey(sessionId)) return;
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loaded.ContainsKey(sessionId)) return;

            if (ChatSessionStore.IsPersistableId(sessionId))
            {
                try
                {
                    var stored = await _store.LoadAsync(DirectoryOf(sessionId), sessionId).ConfigureAwait(false);
                    if (stored != null)
                    {
                        foreach (var message in stored.Messages)
                            await _inner.AddMessageAsync(sessionId, message).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Could not load session {SessionId}", sessionId); }
            }

            _loaded[sessionId] = true;
        }
        finally { _loadGate.Release(); }
    }
}
