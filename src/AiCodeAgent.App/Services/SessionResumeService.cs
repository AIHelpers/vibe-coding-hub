using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.Services;

/// <summary>What <see cref="SessionResumeService.ResumeAsync"/> hands back for the chat to show.</summary>
/// <param name="Transcript">The messages the agent will see on the next turn, in order. The timeline is rebuilt from them.</param>
/// <param name="Note">Set when the history was over the token limit and had to be reduced.</param>
public sealed record ResumedSession(string SessionId, IReadOnlyList<Message> Transcript, string? Note);

/// <summary>
/// Finds the session to continue and restores it: loads the stored messages into the agent's context and, when
/// they do not fit the token limit, compacts (or, without a compactor, trims) the oldest ones, so the next
/// message is sent with the previous context and the visible timeline matches what the agent knows.
/// </summary>
public sealed class SessionResumeService
{
    private readonly ISessionCatalog _catalog;
    private readonly IContextManager _context;
    private readonly IContextCompactor? _compactor;
    private readonly ILogger<SessionResumeService>? _logger;

    public SessionResumeService(
        ISessionCatalog catalog,
        IContextManager context,
        IContextCompactor? compactor = null,
        ILogger<SessionResumeService>? logger = null)
    {
        _catalog = catalog;
        _context = context;
        _compactor = compactor;
        _logger = logger;
    }

    /// <summary>The newest session of this working directory that has messages, or null.</summary>
    public Task<ChatSessionInfo?> FindLastAsync() => _catalog.GetLatestAsync();

    public Task<IReadOnlyList<ChatSessionInfo>> ListAsync() => _catalog.ListAsync();

    public async Task RecordCostAsync(string sessionId, decimal costUsd)
    {
        try { await _catalog.RecordCostAsync(sessionId, costUsd).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Could not store the cost of session {SessionId}", sessionId); }
    }

    public async Task<ResumedSession> ResumeAsync(string sessionId, int maxTokens)
    {
        var messages = await _context.GetContextAsync(sessionId).ConfigureAwait(false);
        string? note = null;

        if (maxTokens > 0 && await _context.GetTokenCountAsync(sessionId).ConfigureAwait(false) > maxTokens)
        {
            if (_compactor != null)
            {
                try
                {
                    var result = await _compactor.CompactAsync(sessionId, new CompactionOptions
                    {
                        TargetTokens = Math.Max(1, maxTokens / 2),
                        KeepRecentMessages = 6
                    }).ConfigureAwait(false);
                    if (result.Compacted)
                        note = $"The history was over the token limit, so older messages were summarized ({result.MessagesBefore} → {result.MessagesAfter} messages).";
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Compaction on resume failed for session {SessionId}", sessionId);
                }
            }

            if (await _context.GetTokenCountAsync(sessionId).ConfigureAwait(false) > maxTokens)
            {
                await _context.TrimContextAsync(sessionId, maxTokens).ConfigureAwait(false);
                note ??= "The history was over the token limit, so the oldest messages were dropped.";
            }

            messages = await _context.GetContextAsync(sessionId).ConfigureAwait(false);
        }

        return new ResumedSession(sessionId, messages, note);
    }
}
