using System.Text;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Context;

/// <summary>
/// Compacts a session's context by summarizing older messages into a
/// single system message, preserving the most recent messages.
/// </summary>
public class ContextCompactor : IContextCompactor
{
    private readonly IContextManager _contextManager;
    private readonly IAiProvider _provider;
    private readonly IThrashingGuard _thrashingGuard;
    private readonly ILogger<ContextCompactor> _logger;
    private const int ApproxCharsPerToken = 4;

    public ContextCompactor(
        IContextManager contextManager,
        IAiProvider provider,
        IThrashingGuard thrashingGuard,
        ILogger<ContextCompactor> logger)
    {
        _contextManager = contextManager;
        _provider = provider;
        _thrashingGuard = thrashingGuard;
        _logger = logger;
    }

    public async Task<CompactionResult> CompactAsync(
        string sessionId,
        CompactionOptions options,
        CancellationToken cancellationToken = default)
    {
        // Check thrashing guard unless forced
        if (!options.Force && !_thrashingGuard.TryBeginCompaction(sessionId))
        {
            return new CompactionResult
            {
                Compacted = false,
                ThrashingBlocked = true,
                Message = "Compaction blocked by thrashing guard: repeated refill cycles detected. " +
                          "Use /compact with force to override, or start a new session."
            };
        }

        var messages = await _contextManager.GetContextAsync(sessionId).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            return new CompactionResult { Compacted = false, Message = "No messages to compact." };
        }

        var tokensBefore = messages.Sum(m => m.TokenCount);
        var messagesBefore = messages.Count;

        // Determine the split point: keep the most recent N messages verbatim
        var keepCount = Math.Min(options.KeepRecentMessages, messages.Count);
        var toSummarize = messages.Take(messages.Count - keepCount).ToList();
        var toKeep = messages.Skip(messages.Count - keepCount).ToList();

        if (toSummarize.Count == 0)
        {
            // Nothing to summarize; just apply hard trimming to the kept messages
            await _contextManager.TrimContextAsync(sessionId, options.TargetTokens).ConfigureAwait(false);
            var trimmed = await _contextManager.GetContextAsync(sessionId).ConfigureAwait(false);
            var trimmedTokens = trimmed.Sum(m => m.TokenCount);
            return new CompactionResult
            {
                Compacted = true,
                MessagesBefore = messagesBefore,
                MessagesAfter = trimmed.Count,
                TokensBefore = tokensBefore,
                TokensAfter = trimmedTokens,
                Message = "Trimmed tool outputs only (nothing to summarize)."
            };
        }

        // Build a summary of older messages using the provider
        var summary = await SummarizeMessagesAsync(toSummarize, options.Focus, cancellationToken).ConfigureAwait(false);

        // Construct the compacted message list:
        // 1. Preserve the original system message(s)
        // 2. Insert a SystemMessage with the summary
        // 3. Append the kept recent messages
        var systemMessages = toSummarize.Where(m => m.Role == MessageRole.System).ToList();
        var nonSystemToSummarize = toSummarize.Where(m => m.Role != MessageRole.System).ToList();

        var summaryMessage = new Message
        {
            Role = MessageRole.System,
            Content = BuildSummaryContent(summary, options.Focus, nonSystemToSummarize.Count),
            TokenCount = EstimateTokens(summary)
        };

        // Clear and rebuild the session
        await _contextManager.ClearAsync(sessionId).ConfigureAwait(false);

        foreach (var sys in systemMessages)
        {
            await _contextManager.AddMessageAsync(sessionId, sys).ConfigureAwait(false);
        }

        await _contextManager.AddMessageAsync(sessionId, summaryMessage).ConfigureAwait(false);

        foreach (var msg in toKeep)
        {
            await _contextManager.AddMessageAsync(sessionId, msg).ConfigureAwait(false);
        }

        // Apply hard trim if still over target
        await _contextManager.TrimContextAsync(sessionId, options.TargetTokens).ConfigureAwait(false);

        var finalMessages = await _contextManager.GetContextAsync(sessionId).ConfigureAwait(false);
        var tokensAfter = finalMessages.Sum(m => m.TokenCount);

        _thrashingGuard.RecordCompaction(sessionId);

        _logger.LogInformation(
            "Compacted session {SessionId}: {Before}→{After} messages, {TokensBefore}→{TokensAfter} tokens",
            sessionId, messagesBefore, finalMessages.Count, tokensBefore, tokensAfter);

        return new CompactionResult
        {
            Compacted = true,
            MessagesBefore = messagesBefore,
            MessagesAfter = finalMessages.Count,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            Summary = summary,
            Focus = options.Focus,
            Message = "Context compacted successfully."
        };
    }

    private async Task<string> SummarizeMessagesAsync(
        List<Message> messages,
        string? focus,
        CancellationToken cancellationToken)
    {
        var conversationText = new StringBuilder();
        foreach (var msg in messages.Where(m => m.Role != MessageRole.System))
        {
            var roleLabel = msg.Role switch
            {
                MessageRole.User => "User",
                MessageRole.Assistant => "Assistant",
                MessageRole.Tool => $"Tool({msg.Name})",
                _ => msg.Role.ToString()
            };
            // Truncate very long tool outputs in the summary input
            var content = msg.Content;
            const int maxContentLength = 2000;
            if (content.Length > maxContentLength)
            {
                content = content[..maxContentLength] + "\n...[truncated for summary]";
            }
            conversationText.AppendLine($"{roleLabel}: {content}");
            conversationText.AppendLine();
        }

        var focusInstruction = string.IsNullOrWhiteSpace(focus)
            ? string.Empty
            : $"\n\nPay special attention to and preserve details about: {focus}";

        var summaryPrompt = $"""
            You are a conversation summarizer. Summarize the following conversation history concisely,
            preserving key decisions, code snippets, file paths, and important context.{focusInstruction}

            Conversation to summarize:
            {conversationText}

            Provide a concise summary that captures:
            - The user's main request(s) and goals
            - Key decisions made
            - Important file paths and code snippets referenced
            - Current state of work
            - Any pending tasks or open questions

            Summary:
            """;

        var request = new CompletionRequest
        {
            SystemPrompt = "You are a helpful conversation summarizer.",
            Messages = new List<Message>
            {
                new() { Role = MessageRole.User, Content = summaryPrompt }
            },
            Options = new CompletionOptions
            {
                Stream = false,
                MaxTokens = 2048
            }
        };

        try
        {
            var response = await _provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            return response.Content ?? "Summary unavailable.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider summarization failed; falling back to local summary");
            _thrashingGuard.RecordFailure(messages.GetHashCode().ToString());
            // Fallback: produce a simple local summary
            return LocalSummary(messages, focus);
        }
    }

    private static string LocalSummary(List<Message> messages, string? focus)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Earlier conversation (auto-summarized locally):");
        foreach (var msg in messages.Where(m => m.Role == MessageRole.User).Take(3))
        {
            sb.AppendLine($"- User asked: {Truncate(msg.Content, 200)}");
        }
        if (!string.IsNullOrWhiteSpace(focus))
            sb.AppendLine($"- Focus topic: {focus}");
        sb.AppendLine($"- {messages.Count} messages were summarized.");
        return sb.ToString();
    }

    private static string BuildSummaryContent(string summary, string? focus, int messageCount)
    {
        var focusLine = string.IsNullOrWhiteSpace(focus) ? string.Empty : $" (focus: {focus})";
        return $"[Earlier conversation summarized{focusLine} — {messageCount} messages condensed]\n\n{summary}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static int EstimateTokens(string? content) =>
        (content?.Length ?? 0) / ApproxCharsPerToken + 1;
}