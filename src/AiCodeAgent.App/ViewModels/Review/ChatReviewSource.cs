using System;
using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.Core.Evidence;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// What the chat has waiting for a human: the approvals the agent asked for, and the tasks that finished with an evidence
/// pack nobody has looked at yet.
/// </summary>
public sealed class ChatReviewSource : IReviewSource
{
    /// <summary>Finished tasks that nobody answered are dropped oldest first beyond this many.</summary>
    public const int MaxFinishedTasks = 50;

    private readonly ChatViewModel _chat;
    private readonly List<FinishedTask> _finished = new();

    private sealed record FinishedTask(EvidencePack Pack, DateTimeOffset Since);

    public ChatReviewSource(ChatViewModel chat)
    {
        _chat = chat;
        _chat.PendingReviewChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _chat.TaskReadyForReview += OnTaskReadyForReview;
    }

    public string Name => "Chat";

    public event EventHandler? Changed;

    private void OnTaskReadyForReview(EvidencePack pack)
    {
        _finished.RemoveAll(f => f.Pack.RunId == pack.RunId);
        _finished.Add(new FinishedTask(pack, DateTimeOffset.UtcNow));
        while (_finished.Count > MaxFinishedTasks) _finished.RemoveAt(0);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ReviewItem> Pending()
    {
        var items = new List<ReviewItem>();
        foreach (var a in _chat.PendingApprovals()) items.Add(ApprovalItem(a));
        foreach (var f in _finished) items.Add(TaskItem(f));
        return items;
    }

    private ReviewItem ApprovalItem(PendingApprovalInfo a)
    {
        var who = string.IsNullOrWhiteSpace(a.Agent) ? "Chat" : $"Chat · {a.Agent}";
        return new ReviewItem
        {
            Id = "approval:" + a.Key,
            Kind = ReviewKind.Approval,
            Title = ToolCallCardFormatting.GetFriendlyName(a.ToolName),
            Subtitle = $"{who} · {a.Risk.ToLowerInvariant()}",
            Detail = string.IsNullOrWhiteSpace(a.Args) ? string.Empty : "```\n" + a.Args.Trim() + "\n```",
            Preview = a.Preview,
            Since = a.Since,
            ApproveLabel = "Approve",
            ChangesLabel = "Deny with a comment",
            DeclineLabel = "Deny",
            ChangesHint = "The agent gets your comment as the result of the call and carries on.",
            Approve = () => Answer(a.Key, true, null),
            RequestChanges = comment => Answer(a.Key, false, comment),
            Decline = () => Answer(a.Key, false, null)
        };
    }

    private ReviewResult Answer(string key, bool approved, string? comment) =>
        _chat.ResolveApproval(key, approved, comment)
            ? ReviewResult.Ok(approved ? null : comment == null ? "Denied." : "Denied with your comment.")
            : ReviewResult.Refused("This approval was answered already.");

    private ReviewItem TaskItem(FinishedTask f)
    {
        var pack = f.Pack;
        var title = FirstLine(pack.Task, 90);
        return new ReviewItem
        {
            Id = "task:" + pack.RunId,
            Kind = ReviewKind.FinishedTask,
            Title = title.Length == 0 ? "Finished task" : title,
            Subtitle = $"Chat · {pack.Status} · ${pack.CostUsd:0.00}" + (pack.Gaps.Count > 0 ? $" · {pack.Gaps.Count} not proven" : string.Empty)
                + (pack.Review is { } review ? $" · reviewed: {review.Findings.Count} finding(s), {review.HighCount} high" : string.Empty),
            Detail = EvidencePackRenderer.ToMarkdown(pack),
            Since = f.Since,
            ApproveLabel = "Accept",
            ChangesLabel = "Request changes",
            DeclineLabel = "Dismiss",
            ChangesHint = "Your comment is sent to the agent as the next message of the chat.",
            Approve = () => Finish(pack.RunId, "Accepted."),
            RequestChanges = comment =>
            {
                if (!_chat.TrySendReviewComment(comment, out var message)) return ReviewResult.Refused(message);
                return Finish(pack.RunId, message);
            },
            Decline = () => Finish(pack.RunId, "Dismissed.")
        };
    }

    private ReviewResult Finish(string runId, string message)
    {
        var removed = _finished.RemoveAll(f => f.Pack.RunId == runId) > 0;
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed ? ReviewResult.Ok(message) : ReviewResult.Refused("This task was answered already.");
    }

    private static string FirstLine(string text, int max)
    {
        var line = (text ?? string.Empty).Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }
}
