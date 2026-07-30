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
        return Task.FromResult(messages.ToList());
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

            // Iterate from oldest non-system messages, removing until under budget
            var currentTokens = totalTokens;

            // Traverse backwards to safely remove messages by index
            for (int i = messages.Count - 1; i >= 1 && currentTokens > maxTokens; i--)
            {
                if (i == systemMessageIndex)
                    continue;

                currentTokens -= messages[i].TokenCount;
                messages.RemoveAt(i);
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

    private static int EstimateTokens(string? content) =>
        (content?.Length ?? 0) / ApproxCharsPerToken + 1;
}