using System;
using System.Collections.Generic;
using System.Linq;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// What the running flow has waiting for a human: the gate in front of a step, a tool approval inside a step, and human
/// steps. The answers are the ones the Flows inspector gives; nothing here decides what a flow does with them.
/// </summary>
public sealed class FlowReviewSource : IReviewSource
{
    /// <summary>Outcome names a human step uses for "go ahead", in the order they are tried.</summary>
    internal static readonly string[] ApproveOutcomes = { "approved", "approve", "accepted", "accept", "yes", "ok", "pass", "passed", "done" };

    /// <summary>Outcome names a human step uses for "send it back".</summary>
    internal static readonly string[] ChangeOutcomes = { "rejected", "reject", "changes_requested", "request_changes", "needs_changes", "declined", "no", "fail", "failed" };

    private readonly FlowEditorViewModel _flows;

    public FlowReviewSource(FlowEditorViewModel flows)
    {
        _flows = flows;
        _flows.PendingReviewChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Name => "Flows";

    public event EventHandler? Changed;

    public IReadOnlyList<ReviewItem> Pending() => _flows.PendingReviews().Select(Item).ToList();

    private ReviewItem Item(FlowReviewRequest r) => r.Kind switch
    {
        FlowReviewKind.Gate => GateItem(r),
        FlowReviewKind.ToolApproval => ToolItem(r),
        _ => DecisionItem(r)
    };

    private ReviewItem GateItem(FlowReviewRequest r) => new()
    {
        Id = "flow:" + r.NodeId,
        Kind = ReviewKind.Gate,
        Title = $"Start step: {r.NodeName}",
        Subtitle = $"Flow {r.Flow} · gate",
        Detail = r.Detail,
        Since = r.Since,
        ApproveLabel = "Approve",
        ChangesLabel = "Run with a note",
        DeclineLabel = "Stop the flow",
        ChangesHint = "The step runs with your comment added to its prompt.",
        Approve = () => Answer(_flows.ReviewApprove(r.NodeId)),
        RequestChanges = comment => Answer(_flows.ReviewApprove(r.NodeId, comment), "Approved with your note."),
        Decline = () => Answer(_flows.ReviewDeny(r.NodeId), "Flow stopped.")
    };

    private ReviewItem ToolItem(FlowReviewRequest r) => new()
    {
        Id = "flow:" + r.NodeId,
        Kind = ReviewKind.Approval,
        Title = r.NodeName + " · tool call",
        Subtitle = $"Flow {r.Flow} · approval",
        Detail = r.Detail,
        Since = r.Since,
        ApproveLabel = "Approve",
        ChangesLabel = "Deny with a comment",
        DeclineLabel = "Deny",
        ChangesHint = "The agent gets your comment as the result of the call and carries on.",
        Approve = () => Answer(_flows.ReviewApprove(r.NodeId)),
        RequestChanges = comment => Answer(_flows.ReviewDeny(r.NodeId, comment), "Denied with your comment."),
        Decline = () => Answer(_flows.ReviewDeny(r.NodeId), "Denied.")
    };

    private ReviewItem DecisionItem(FlowReviewRequest r)
    {
        // A step without outcomes just continues. With outcomes the queue only answers the ones it can tell apart from the
        // name; custom outcomes are answered in Flows, and the item says so instead of guessing.
        var approve = r.Outcomes.Count == 0 ? (found: true, outcome: (string?)null) : Match(r.Outcomes, ApproveOutcomes);
        var change = r.Outcomes.Count == 0 ? (found: false, outcome: (string?)null) : Match(r.Outcomes, ChangeOutcomes);

        return new ReviewItem
        {
            Id = "flow:" + r.NodeId,
            Kind = ReviewKind.Decision,
            Title = $"Decide: {r.NodeName}",
            Subtitle = $"Flow {r.Flow} · human step",
            Detail = r.Detail,
            Since = r.Since,
            ApproveLabel = r.Outcomes.Count == 0 ? "Continue" : approve.found ? approve.outcome! : "Approve",
            ChangesLabel = change.found ? change.outcome! : "Request changes",
            DeclineLabel = "Stop the flow",
            ChangesHint = $"The flow takes the \"{change.outcome}\" path and your comment goes to the next step.",
            Approve = () => approve.found
                ? Answer(_flows.ReviewChoose(r.NodeId, approve.outcome, null))
                : ReviewResult.Refused($"This step has its own outcomes ({string.Join(", ", r.Outcomes)}). Answer it in Flows."),
            RequestChanges = change.found
                ? comment => Answer(_flows.ReviewChoose(r.NodeId, change.outcome, comment), $"Sent back: {change.outcome}.")
                : null,
            Decline = () => Answer(_flows.ReviewStopHuman(r.NodeId), "Flow stopped.")
        };
    }

    private static (bool found, string? outcome) Match(IReadOnlyList<string> outcomes, string[] names)
    {
        foreach (var name in names)
        {
            var hit = outcomes.FirstOrDefault(o => string.Equals(o.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return (true, hit.Trim());
        }
        return (false, null);
    }

    private static ReviewResult Answer(bool answered, string? message = null) =>
        answered ? ReviewResult.Ok(message) : ReviewResult.Refused("It was answered already, or the run stopped.");
}
