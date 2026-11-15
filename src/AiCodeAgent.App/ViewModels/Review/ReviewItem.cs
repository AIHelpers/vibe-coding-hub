using System;

namespace AiCodeAgent.App.ViewModels;

/// <summary>What is waiting for a human (E10-S3).</summary>
public enum ReviewKind
{
    /// <summary>A tool call that needs approval before it runs (chat, or inside a flow step).</summary>
    Approval,
    /// <summary>The confirmation gate in front of a flow step.</summary>
    Gate,
    /// <summary>A human step of a flow: it waits for a decision.</summary>
    Decision,
    /// <summary>A finished task with its evidence pack: nobody is blocked, but nobody has looked at it yet.</summary>
    FinishedTask
}

/// <summary>What a review action reports. <see cref="Done"/> is false when nothing changed; <see cref="Message"/> then says why.</summary>
public readonly record struct ReviewResult(bool Done, string? Message = null)
{
    public static ReviewResult Ok(string? message = null) => new(true, message);
    public static ReviewResult Refused(string message) => new(false, message);
}

/// <summary>
/// One thing waiting for a human, as a source reports it right now. An item is a snapshot: the actions find their target by
/// key when they are called (never through a captured object that may be gone), so a snapshot that is kept while its
/// <see cref="Signature"/> stays the same is still valid.
/// </summary>
public sealed class ReviewItem
{
    public required string Id { get; init; }
    public required ReviewKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>Where it comes from and who asks, e.g. "Chat · Dana (implementer) · write".</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Markdown for the detail pane: the arguments, the step's question, or the evidence pack.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>For approvals of a file edit or a command: the same preview the approval dialog shows.</summary>
    public ApprovalPreviewViewModel? Preview { get; init; }

    public DateTimeOffset Since { get; init; } = DateTimeOffset.UtcNow;

    public required Func<ReviewResult> Approve { get; init; }

    /// <summary>Sends a comment back to the agent; null when this kind of item has no way to do that.</summary>
    public Func<string, ReviewResult>? RequestChanges { get; init; }

    /// <summary>Says no (deny, stop the flow) or, for a finished task, dismisses it; null when there is no such action.</summary>
    public Func<ReviewResult>? Decline { get; init; }

    public string ApproveLabel { get; init; } = "Approve";
    public string ChangesLabel { get; init; } = "Request changes";
    public string DeclineLabel { get; init; } = "Decline";

    /// <summary>Shown above the comment box: what will happen to the comment.</summary>
    public string ChangesHint { get; init; } = "Your comment goes back to the agent.";

    /// <summary>An agent (or a whole flow) is stopped until someone answers.</summary>
    public bool BlocksAgent => Kind != ReviewKind.FinishedTask;

    public bool CanRequestChanges => RequestChanges != null;
    public bool CanDecline => Decline != null;

    public string KindLabel => Kind switch
    {
        ReviewKind.Approval => "Approval",
        ReviewKind.Gate => "Gate",
        ReviewKind.Decision => "Decision",
        _ => "Review"
    };

    /// <summary>Two snapshots with the same signature look identical, so the list keeps the old one (and the selection with it).</summary>
    public string Signature =>
        string.Join('\u001f', Kind, Title, Subtitle, Detail, Preview?.GetHashCode() ?? 0, ApproveLabel, ChangesLabel, DeclineLabel, ChangesHint, CanRequestChanges, CanDecline);

    /// <summary>"just now", "5 min", "2 h", "3 d".</summary>
    public string AgeText(DateTimeOffset now)
    {
        var age = now - Since;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }
}
