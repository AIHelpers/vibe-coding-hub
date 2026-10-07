using System.Text;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Flows;

/// <summary>Output of a finished node, handed to the nodes connected after it.</summary>
public sealed record FlowNodeOutput(string NodeId, string? CharacterId, string Text, string? Outcome, int Iteration);

/// <summary>
/// Builds a flow node's prompt (task + inputs + loop feedback + outcome instruction) and reads
/// the <c>OUTCOME:</c> line from its answer.
/// </summary>
public static partial class FlowPrompt
{
    /// <summary>Longest input handed to a node, in characters (~6k tokens). Longer outputs are cut with a note.</summary>
    public const int MaxInputChars = 24_000;

    [GeneratedRegex(@"\{input:([A-Za-z0-9_\-]+)\}")]
    private static partial Regex InputPlaceholderRegex();

    [GeneratedRegex(@"^\s*\**\s*OUTCOME\s*\**\s*:\s*\**\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OutcomeRegex();

    /// <summary>Node ids used as <c>{input:id}</c> in a template.</summary>
    public static IEnumerable<string> InputPlaceholders(string? template) =>
        string.IsNullOrEmpty(template)
            ? Enumerable.Empty<string>()
            : InputPlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The full prompt for a node run. Inputs used inline through <c>{input:id}</c> are not repeated
    /// in the &lt;inputs&gt; block.
    /// </summary>
    public static string Build(
        string? template,
        string task,
        IReadOnlyList<FlowNodeOutput> inputs,
        IReadOnlyList<FlowNodeOutput>? feedback = null,
        IReadOnlyList<string>? outcomes = null)
    {
        var inline = new HashSet<string>(InputPlaceholders(template), StringComparer.OrdinalIgnoreCase);
        var body = string.IsNullOrWhiteSpace(template) ? "Task: {task}" : template;
        body = body.Replace("{task}", task);
        body = InputPlaceholderRegex().Replace(body, m =>
        {
            var input = inputs.FirstOrDefault(i => string.Equals(i.NodeId, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            return input == null ? $"(no output from {m.Groups[1].Value})" : Cap(input.Text);
        });

        var sb = new StringBuilder(body.TrimEnd());
        var blockInputs = inputs.Where(i => !inline.Contains(i.NodeId)).ToList();
        if (blockInputs.Count > 0)
        {
            sb.Append("\n\nResults from the steps before you:\n<inputs>\n");
            foreach (var input in blockInputs)
                AppendTagged(sb, "input", input);
            sb.Append("</inputs>");
        }

        if (feedback is { Count: > 0 })
        {
            sb.Append("\n\nYour previous work was sent back for another round. Address this feedback:\n");
            foreach (var f in feedback)
                AppendTagged(sb, "feedback", f);
        }

        if (outcomes is { Count: > 0 })
        {
            sb.Append("\n\nWhen you are done, end your answer with exactly one line in this form:\nOUTCOME: <")
              .Append(string.Join(" | ", outcomes))
              .Append(">\nChoose one of: ").Append(string.Join(", ", outcomes)).Append('.');
        }
        return sb.ToString();
    }

    private static void AppendTagged(StringBuilder sb, string tag, FlowNodeOutput o)
    {
        sb.Append('<').Append(tag).Append(" from=\"").Append(o.NodeId).Append('"');
        if (!string.IsNullOrEmpty(o.CharacterId)) sb.Append(" character=\"").Append(o.CharacterId).Append('"');
        if (o.Iteration > 1) sb.Append(" iteration=\"").Append(o.Iteration).Append('"');
        if (!string.IsNullOrEmpty(o.Outcome)) sb.Append(" outcome=\"").Append(o.Outcome).Append('"');
        sb.Append(">\n").Append(Cap(o.Text).Trim()).Append("\n</").Append(tag).Append(">\n");
    }

    /// <summary>Cut text to <see cref="MaxInputChars"/>, keeping the end (where conclusions usually are) and the start.</summary>
    public static string Cap(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(no output)";
        if (text.Length <= MaxInputChars) return text;
        var head = text[..(MaxInputChars / 3)];
        var tail = text[^(MaxInputChars * 2 / 3)..];
        return head + $"\n\n[… {text.Length - MaxInputChars:N0} characters cut …]\n\n" + tail;
    }

    /// <summary>
    /// The outcome named on the last <c>OUTCOME:</c> line, matched case-insensitively against
    /// <paramref name="allowed"/> (returned in its declared spelling). Null when missing or not allowed.
    /// </summary>
    public static string? ParseOutcome(string? answer, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrEmpty(answer) || allowed.Count == 0) return null;
        var matches = OutcomeRegex().Matches(answer);
        if (matches.Count == 0) return null;
        var value = matches[^1].Groups[1].Value;
        return allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Short follow-up asking for the missing outcome line.</summary>
    public static string OutcomeReminder(IReadOnlyList<string> outcomes) =>
        $"You did not end with a valid outcome line. Reply with only one line: OUTCOME: <{string.Join(" | ", outcomes)}>";
}
