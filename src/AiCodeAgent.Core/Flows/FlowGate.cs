using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Flows;

/// <summary>
/// What a reviewer decided at a step: the answer to <see cref="FlowHumanRequestEvent"/> (a human step).
/// </summary>
/// <param name="Outcome">The outcome picked from the step's <c>outcomes</c>; null for a step without outcomes ("Continue").</param>
/// <param name="Note">Free text from the reviewer; handed on in the step's output.</param>
/// <param name="Stop">Stop the whole flow instead of choosing.</param>
public sealed record FlowHumanChoice(string? Outcome, string? Note = null, bool Stop = false);

/// <summary>
/// A human step is waiting for a choice (<c>"kind": "human"</c>). The runner emits it wrapped in an
/// <see cref="AgentTaggedEvent"/> tagged with the node id and waits for <see cref="Choice"/>.
/// </summary>
public sealed record FlowHumanRequestEvent(
    string NodeId,
    string Prompt,
    IReadOnlyList<string> Outcomes,
    TaskCompletionSource<FlowHumanChoice> Choice) : AgentEvent;

/// <summary>Builds the reviewer note block appended to a gated step's prompt.</summary>
public static class FlowReviewerNote
{
    public const string Tag = "reviewer_note";

    /// <summary>Null when there is no note.</summary>
    public static string? Block(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var clean = note.Replace($"</{Tag}>", string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Replace($"<{Tag}>", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return clean.Length == 0 ? null : $"<{Tag}>\n{clean}\n</{Tag}>";
    }

    /// <summary>The prompt with the note appended after it.</summary>
    public static string Append(string prompt, string? note) =>
        Block(note) is { } block ? prompt.TrimEnd() + "\n\n" + block : prompt;
}
