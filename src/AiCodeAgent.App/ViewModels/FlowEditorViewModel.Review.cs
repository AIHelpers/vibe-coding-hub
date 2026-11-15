using System;
using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>What a flow node is waiting for.</summary>
internal enum FlowReviewKind
{
    /// <summary>The confirmation gate in front of a step.</summary>
    Gate,
    /// <summary>A tool approval inside a running step.</summary>
    ToolApproval,
    /// <summary>A human step that waits for a choice.</summary>
    Decision
}

/// <summary>A flow node that waits for the user, as the Review Queue sees it.</summary>
internal sealed record FlowReviewRequest(
    string NodeId, string NodeName, FlowReviewKind Kind, string Flow, string Detail, IReadOnlyList<string> Outcomes, DateTimeOffset Since);

/// <summary>The Review Queue's view of the flow run (E10-S3): what waits for an answer, and the answers, by node id.</summary>
public partial class FlowEditorViewModel
{
    private const string DecisionMarker = "**Waiting for your decision**";
    private const int MaxDetailChars = 3000;

    private readonly Dictionary<string, ToolCall> _approvalCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _waitingSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A gate, a tool approval or a human step started or stopped waiting.</summary>
    internal event EventHandler? PendingReviewChanged;

    private void RaisePendingReviewChanged() => PendingReviewChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Everything in the running flow that waits for the user, in node order.</summary>
    internal IReadOnlyList<FlowReviewRequest> PendingReviews()
    {
        var list = new List<FlowReviewRequest>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Nodes)
        {
            FlowReviewKind kind;
            string detail;
            if (node.IsAwaitingChoice && _humanChoices.ContainsKey(node.Id))
            {
                kind = FlowReviewKind.Decision;
                detail = DecisionText(node);
            }
            else if (node.IsAwaitingApproval && node.IsAwaitingGate && _approvals.ContainsKey(node.Id))
            {
                kind = FlowReviewKind.Gate;
                detail = GateText(node);
            }
            else if (node.IsAwaitingApproval && _approvals.ContainsKey(node.Id) && _approvalCalls.TryGetValue(node.Id, out var call))
            {
                kind = FlowReviewKind.ToolApproval;
                detail = ToolApprovalText(node, call);
            }
            else
            {
                continue;
            }

            live.Add(node.Id);
            if (!_waitingSince.TryGetValue(node.Id, out var since)) _waitingSince[node.Id] = since = DateTimeOffset.UtcNow;
            list.Add(new FlowReviewRequest(node.Id, node.Name, kind, FlowName, detail, kind == FlowReviewKind.Decision ? node.Outcomes : Array.Empty<string>(), since));
        }

        foreach (var gone in _waitingSince.Keys.Where(k => !live.Contains(k)).ToList()) _waitingSince.Remove(gone);
        return list;
    }

    private static string GateText(FlowNodeViewModel node)
    {
        var text = $"The flow is paused in front of **{node.Name}** and waits for your go-ahead.";
        if (!string.IsNullOrWhiteSpace(node.CharacterLabel)) text += $"\n\nCharacter: {node.CharacterLabel}";
        if (!string.IsNullOrWhiteSpace(node.PromptTemplate)) text += "\n\nPrompt of the step:\n\n```\n" + Cut(node.PromptTemplate.Trim(), 1500) + "\n```";
        return text;
    }

    private static string ToolApprovalText(FlowNodeViewModel node, ToolCall call)
    {
        var args = string.Join("\n", call.Arguments.Select(kv => $"{kv.Key}: {Cut((kv.Value?.ToString() ?? string.Empty).ReplaceLineEndings(" "), 400)}"));
        return $"**{node.Name}** asks to run `{call.Name}`." + (args.Length == 0 ? string.Empty : "\n\n```\n" + args + "\n```");
    }

    private static string DecisionText(FlowNodeViewModel node)
    {
        var output = node.OutputText ?? string.Empty;
        var at = output.LastIndexOf(DecisionMarker, StringComparison.Ordinal);
        var text = (at >= 0 ? output[(at + DecisionMarker.Length)..] : output).Trim();
        if (text.Length == 0) text = $"**{node.Name}** waits for your decision.";
        if (node.Outcomes.Count > 0) text += $"\n\nOutcomes: {string.Join(", ", node.Outcomes)}";
        return Cut(text, MaxDetailChars);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Approves the gate or the tool call of a node; a note is added to the step's prompt (gates only). False when it is not waiting any more.</summary>
    internal bool ReviewApprove(string nodeId, string? note = null)
    {
        if (FindNode(nodeId) is not { } node || !_approvals.ContainsKey(node.Id)) return false;
        if (!string.IsNullOrWhiteSpace(note) && node.IsAwaitingGate && _gateFeedback.TryGetValue(node.Id, out var feedback))
            feedback.Text = note.Trim();
        AnswerGate(node, true);
        return true;
    }

    /// <summary>Says no to the gate (stops the flow) or to the tool call (the agent carries on, with the note as the result).</summary>
    internal bool ReviewDeny(string nodeId, string? note = null)
    {
        if (FindNode(nodeId) is not { } node || !_approvals.ContainsKey(node.Id)) return false;
        if (!string.IsNullOrWhiteSpace(note) && !node.IsAwaitingGate && _gateFeedback.TryGetValue(node.Id, out var feedback))
            feedback.Text = note.Trim();
        AnswerGate(node, false);
        return true;
    }

    /// <summary>Answers a human step with an outcome (null = continue) and an optional note.</summary>
    internal bool ReviewChoose(string nodeId, string? outcome, string? note)
    {
        if (FindNode(nodeId) is not { } node || !_humanChoices.ContainsKey(node.Id)) return false;
        AnswerHuman(node, new FlowHumanChoice(outcome, string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        return true;
    }

    /// <summary>Stops the whole flow at a human step.</summary>
    internal bool ReviewStopHuman(string nodeId)
    {
        if (FindNode(nodeId) is not { } node || !_humanChoices.ContainsKey(node.Id)) return false;
        AnswerHuman(node, new FlowHumanChoice(null, null, Stop: true));
        return true;
    }
}
