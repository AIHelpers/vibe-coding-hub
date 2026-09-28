using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Context;

public class InMemoryContextManager : IContextManager
{
    private readonly ConcurrentDictionary<string, List<Message>> _sessions = new();
    private readonly ILogger<InMemoryContextManager> _logger;
    private const int ApproxCharsPerToken = 4;

    public InMemoryContextManager(ILogger<InMemoryContextManager> logger) => _logger = logger;

    public Task<List<Message>> GetContextAsync(string sessionId)
    {
        var messages = _sessions.GetOrAdd(sessionId, _ => new List<Message>());
        // Copy under the same lock AddMessageAsync/TrimContextAsync use —
        // without it, a concurrent agent adding/removing messages while this
        // runs List<T>.ToList() could throw "Collection was modified" or
        // hand back a torn snapshot.
        lock (messages)
        {
            return Task.FromResult(messages.ToList());
        }
    }

    public Task AddMessageAsync(string sessionId, Message message)
    {
        var messages = _sessions.GetOrAdd(sessionId, _ => new List<Message>());
        lock (messages)
        {
            messages.Add(message with { TokenCount = EstimateTokens(message.Content) });
        }
        return Task.CompletedTask;
    }

    public Task<int> GetTokenCountAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var messages))
            return Task.FromResult(0);

        lock (messages)
        {
            return Task.FromResult(messages.Sum(m => m.TokenCount));
        }
    }

    public async Task TrimContextAsync(string sessionId, int maxTokens)
    {
        if (!_sessions.TryGetValue(sessionId, out var messages))
            return;

        lock (messages)
        {
            var totalTokens = messages.Sum(m => m.TokenCount);
            if (totalTokens <= maxTokens) return;

            // Identify the system message (first one with System role) to preserve it
            var systemMessageIndex = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == MessageRole.System)
                {
                    systemMessageIndex = i;
                    break;
                }
            }

            // Build removable groups in oldest-first order (index 1 upward).
            // An assistant message that made tool calls is grouped together
            // with its immediately-following tool_result messages so we never
            // remove one half of a tool_use/tool_result pair — providers
            // (Anthropic/OpenAI) reject a request with a dangling tool_result
            // or a tool_use with no matching result.
            var groups = new List<List<int>>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (i == systemMessageIndex) continue;

                var msg = messages[i];
                if (msg.Role == MessageRole.Assistant && msg.ToolCalls is { Count: > 0 })
                {
                    var callIds = new HashSet<string>(msg.ToolCalls.Select(tc => tc.Id));
                    var group = new List<int> { i };
                    var j = i + 1;
                    while (j < messages.Count
                           && messages[j].Role == MessageRole.Tool
                           && messages[j].ToolCallId is { } toolCallId
                           && callIds.Contains(toolCallId))
                    {
                        group.Add(j);
                        j++;
                    }
                    groups.Add(group);
                    i = j - 1; // skip past the group we just consumed
                }
                else
                {
                    groups.Add(new List<int> { i });
                }
            }

            // Remove oldest groups first until under budget.
            var currentTokens = totalTokens;
            var toRemove = new HashSet<int>();
            foreach (var group in groups)
            {
                if (currentTokens <= maxTokens) break;
                foreach (var idx in group)
                    currentTokens -= messages[idx].TokenCount;
                toRemove.UnionWith(group);
            }

            if (toRemove.Count > 0)
            {
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    if (toRemove.Contains(i))
                        messages.RemoveAt(i);
                }
            }

            // If still over budget, truncate the longest individual message content
            if (currentTokens > maxTokens)
            {
                for (int i = 0; i < messages.Count && currentTokens > maxTokens; i++)
                {
                    var msg = messages[i];
                    if (msg.Role == MessageRole.System) continue; // Never truncate system messages

                    // Truncate message content to roughly half its token count
                    var originalTokens = msg.TokenCount;
                    var targetLength = msg.Content.Length / 2;
                    var truncatedContent = msg.Content[..Math.Min(targetLength, msg.Content.Length)] + "\n...[truncated]";
                    var newTokenCount = EstimateTokens(truncatedContent);
                    messages[i] = msg with { Content = truncatedContent, TokenCount = newTokenCount };
                    currentTokens = currentTokens - originalTokens + newTokenCount;
                }
            }

            if (currentTokens > maxTokens)
                _logger.LogDebug("Trimmed messages to fit token limit (current: {CurrentTokens}/{MaxTokens})",
                    currentTokens, maxTokens);
        }
    }

    public Task ClearAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetSessionsAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_sessions.Keys.ToList());
    }

    private static int EstimateTokens(string? content) =>
        (content?.Length ?? 0) / ApproxCharsPerToken + 1;
}