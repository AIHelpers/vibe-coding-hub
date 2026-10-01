using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Sessions;

/// <summary>What a rewind undoes.</summary>
public enum RewindMode
{
    /// <summary>Drop the conversation from the chosen message onward; files stay as they are.</summary>
    Conversation,
    /// <summary>Restore files to how they were before the chosen message; the conversation stays.</summary>
    Code,
    /// <summary>Both: go back to exactly the state before the chosen message.</summary>
    Both
}

/// <summary>One user message the session can be rewound to.</summary>
/// <param name="Number">1-based position among the user's messages (what the user types in /rewind N).</param>
/// <param name="MessageIndex">Index in the full message list.</param>
/// <param name="FilesChanged">Files the agent changed from this message onward.</param>
public sealed record RewindPoint(int Number, int MessageIndex, string Preview, DateTime Timestamp, int FilesChanged);

public sealed record RewindResult(
    bool Success,
    string Message,
    string? RestoredUserText = null,
    int MessagesRemoved = 0,
    IReadOnlyList<string>? RestoredFiles = null,
    IReadOnlyList<string>? SkippedFiles = null);

/// <summary>
/// "Rewind to here" and "fork from here" for a session. A rewind point is one of the user's messages: rewinding
/// removes that message and everything after it (so it can be re-typed or edited) and, optionally, puts every file
/// the agent touched since then back the way it was, using the existing edit checkpoints. Forking copies the
/// conversation up to (not including) the chosen message into a NEW session and leaves the original untouched.
/// Files are matched to messages by timestamp: a file is restored from the earliest checkpoint taken at or after
/// the chosen message. Note: the on-disk session log is append-only and is not rewritten by a rewind.
/// </summary>
public sealed class ConversationRewinder
{
    private readonly IContextManager _context;
    private readonly ICheckpointManager _checkpoints;
    private readonly IAgentEventBus? _eventBus;

    public ConversationRewinder(IContextManager context, ICheckpointManager checkpoints, IAgentEventBus? eventBus = null)
    {
        _context = context;
        _checkpoints = checkpoints;
        _eventBus = eventBus;
    }

    public async Task<IReadOnlyList<RewindPoint>> ListPointsAsync(string sessionId)
    {
        var messages = await _context.GetContextAsync(sessionId).ConfigureAwait(false) ?? new List<Message>();
        var checkpoints = await _checkpoints.GetCheckpointsForSessionAsync(sessionId).ConfigureAwait(false);

        var points = new List<RewindPoint>();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role != MessageRole.User) continue;
            var changed = checkpoints.Where(c => c.Timestamp >= m.Timestamp).Select(c => c.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            points.Add(new RewindPoint(points.Count + 1, i, Preview(m.Content), m.Timestamp, changed));
        }
        return points;
    }

    /// <summary>The session's current messages, for rebuilding a chat view after a rewind or fork.</summary>
    public async Task<IReadOnlyList<Message>> GetTranscriptAsync(string sessionId) =>
        await _context.GetContextAsync(sessionId).ConfigureAwait(false) ?? new List<Message>();

    public async Task<RewindResult> RewindAsync(string sessionId, int number, RewindMode mode)
    {
        var messages = await _context.GetContextAsync(sessionId).ConfigureAwait(false) ?? new List<Message>();
        var point = (await ListPointsAsync(sessionId).ConfigureAwait(false)).FirstOrDefault(p => p.Number == number);
        if (point == null)
            return new RewindResult(false, $"No user message number {number}. Use /rewind to list them.");

        var restored = new List<string>();
        var skipped = new List<string>();
        if (mode is RewindMode.Code or RewindMode.Both)
        {
            var all = await _checkpoints.GetCheckpointsForSessionAsync(sessionId).ConfigureAwait(false);
            foreach (var group in all.Where(c => c.Timestamp >= point.Timestamp)
                                     .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                var earliest = group.OrderBy(c => c.Timestamp).First();
                var ok = await _checkpoints.RestoreAsync(sessionId, earliest.CheckpointId, _eventBus).ConfigureAwait(false);
                (ok ? restored : skipped).Add(group.Key);
            }
        }

        var removed = 0;
        string? userText = null;
        if (mode is RewindMode.Conversation or RewindMode.Both)
        {
            userText = messages[point.MessageIndex].Content;
            var keep = messages.Take(point.MessageIndex).ToList();
            removed = messages.Count - keep.Count;
            await _context.ClearAsync(sessionId).ConfigureAwait(false);
            foreach (var m in keep)
                await _context.AddMessageAsync(sessionId, m).ConfigureAwait(false);
        }

        var parts = new List<string>();
        if (mode != RewindMode.Code) parts.Add($"removed {removed} message(s)");
        if (mode != RewindMode.Conversation) parts.Add($"restored {restored.Count} file(s)" + (skipped.Count > 0 ? $", {skipped.Count} skipped" : ""));
        return new RewindResult(true, $"Rewound to before message {number}: {string.Join("; ", parts)}.", userText, removed, restored, skipped);
    }

    /// <summary>Copies the conversation before message <paramref name="number"/> into a new session and returns its id.</summary>
    public async Task<(string? NewSessionId, string Message)> ForkAsync(string sessionId, int number)
    {
        var messages = await _context.GetContextAsync(sessionId).ConfigureAwait(false) ?? new List<Message>();
        var point = (await ListPointsAsync(sessionId).ConfigureAwait(false)).FirstOrDefault(p => p.Number == number);
        if (point == null)
            return (null, $"No user message number {number}. Use /rewind to list them.");

        var newId = Guid.NewGuid().ToString("N")[..24];
        foreach (var m in messages.Take(point.MessageIndex))
            await _context.AddMessageAsync(newId, m).ConfigureAwait(false);

        return (newId, $"Forked into a new session with the {point.MessageIndex} message(s) before message {number}. Files were not changed.");
    }

    private static string Preview(string content)
    {
        var line = (content ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "(empty)";
        return line.Length > 70 ? line[..70] + "…" : line;
    }
}
