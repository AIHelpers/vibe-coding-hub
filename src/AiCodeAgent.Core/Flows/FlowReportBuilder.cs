using System.Globalization;
using System.Text;

namespace AiCodeAgent.Core.Flows;

/// <summary>
/// Turns a recorded flow run into a Markdown report that renders on GitHub: the task, the flow as a Mermaid diagram
/// coloured by what happened, a table of every step run, each step's final answer (collapsible), totals and changed files.
/// The output is deterministic (UTC times, invariant numbers, "\n" line ends), so it can be compared with golden files.
/// </summary>
public static class FlowReportBuilder
{
    /// <summary>One step's answer is cut after this many characters so the report stays pasteable.</summary>
    public const int MaxStepOutput = 20_000;

    /// <summary>The flow of <paramref name="run"/> as a Mermaid flowchart with the run's statuses.</summary>
    public static string Mermaid(FlowRunRecord run, Func<string, string?>? characterLabel = null) =>
        FlowGraph.Build(run.Definition, null).ToMermaid(characterLabel, run);

    /// <param name="outputOf">Reads a step run's saved answer (for example <c>s =&gt; store.LoadOutput(flow, id, s)</c>).</param>
    public static string Markdown(FlowRunRecord run, Func<FlowStepRecord, string?> outputOf, Func<string, string?>? characterLabel = null)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line($"# Flow run: {run.FlowName}");
        Line();
        Line($"{StatusIcon(run.Status)} **{FlowUsageFormat.StatusName(run.Status)}** · started {run.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC · " +
             $"{Seconds(run.Duration)} · {FlowUsageFormat.Tokens(run.Tokens)} tokens · {FlowUsageFormat.Cost(run.CostUsd)}" +
             (run.BudgetUsd is > 0 ? $" of {FlowUsageFormat.Cost(run.BudgetUsd.Value)}" : ""));
        if (run.Status != FlowRunStatus.Succeeded && !string.IsNullOrWhiteSpace(run.Message))
        {
            Line();
            Line($"> {OneLine(run.Message)}");
        }
        if (run.ReplayOfRunId != null)
        {
            Line();
            Line($"_Re-run of `{run.ReplayOfRunId}` from `{run.ReplayFromNodeId}`; earlier steps were reused._");
        }
        Line();

        Line("## Task");
        Line();
        Line("````text");
        Line(run.Task.Trim());
        Line("````");
        Line();

        Line("## Flow");
        Line();
        Line("```mermaid");
        sb.Append(Mermaid(run, characterLabel).TrimEnd('\n')).Append('\n');
        Line("```");
        Line();

        var order = run.Steps.Select(s => s.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Line("## Steps");
        Line();
        if (run.Steps.Count == 0) Line("_No step ran._");
        else
        {
            Line("| Step | Round | Character | Model | State | Outcome | Tokens | Cost | Duration |");
            Line("|---|---|---|---|---|---|---|---|---|");
            foreach (var s in run.Steps)
            {
                var model = string.IsNullOrEmpty(s.Model) ? s.Usage?.Model : s.Model;
                var provider = string.IsNullOrEmpty(s.Provider) ? s.Usage?.Provider : s.Provider;
                var modelText = string.IsNullOrEmpty(model) ? "" : string.IsNullOrEmpty(provider) ? model : $"{provider}/{model}";
                var state = s.Reused ? "reused" : s.State.ToString().ToLowerInvariant();
                var tokens = s.Usage == null ? "" : FlowUsageFormat.Tokens(s.Usage.Tokens);
                var cost = s.Usage == null ? "" : s.Usage.Unpriced ? "unpriced" : FlowUsageFormat.Cost(s.Usage.CostUsd);
                var duration = s.Duration > TimeSpan.Zero ? Seconds(s.Duration) : "";
                Line($"| `{s.NodeId}` | {s.Round} | {Cell(s.CharacterId)} | {Cell(modelText)} | {state} | {Cell(s.Outcome)} | {tokens} | {cost} | {duration} |");
            }
        }
        Line();

        var answered = order
            .Select(id => run.Steps.Where(s => s.NodeId.Equals(id, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Round).Last())
            .Select(s => (Step: s, Output: outputOf(s)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Output) || x.Step.State == FlowNodeState.Failed)
            .ToList();
        if (answered.Count > 0)
        {
            Line("## Results");
            Line();
            foreach (var (step, output) in answered)
            {
                var title = $"<b>{step.NodeId}</b>" + (step.Outcome != null ? $" — {step.Outcome}" : "") +
                            (step.State == FlowNodeState.Failed ? " (failed)" : "");
                Line($"<details><summary>{title}</summary>");
                Line();
                var text = string.IsNullOrWhiteSpace(output) ? $"_{step.Detail ?? "No output."}_" : output.Trim().Replace("\r\n", "\n");
                if (text.Length > MaxStepOutput) text = text[..MaxStepOutput] + "\n\n_…answer shortened._";
                Line(text);
                Line();
                Line("</details>");
                Line();
            }
        }

        var ran = run.Steps.Where(s => !s.Reused).ToList();
        var usage = ran.Where(s => s.Usage != null).Select(s => s.Usage!).ToList();
        Line("## Totals");
        Line();
        Line($"- Steps run: {ran.Count}" + (run.Steps.Count > ran.Count ? $" (+{run.Steps.Count - ran.Count} reused)" : ""));
        Line($"- Tokens: {FlowUsageFormat.Tokens(usage.Sum(u => u.TokensIn))} in / {FlowUsageFormat.Tokens(usage.Sum(u => u.TokensOut))} out");
        Line($"- Cost: {FlowUsageFormat.Cost(run.CostUsd)}" + (usage.Any(u => u.Unpriced) ? " (some models unpriced)" : ""));
        Line($"- Wall-clock time: {Seconds(run.Duration)}");
        Line();

        var changed = run.Steps.SelectMany(s => s.Diffs.Select(d => (d.FilePath, s.NodeId)))
            .GroupBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        Line("## Changed files");
        Line();
        if (changed.Count == 0) Line("_No files were changed._");
        else
            foreach (var g in changed)
                Line($"- `{g.Key}` — {string.Join(", ", g.Select(x => x.NodeId).Distinct(StringComparer.OrdinalIgnoreCase))}");
        Line();
        Line($"<sub>Run `{run.Id}`</sub>");
        return sb.ToString();
    }

    private static string StatusIcon(FlowRunStatus status) => status switch
    {
        FlowRunStatus.Succeeded => "✅",
        FlowRunStatus.Failed => "❌",
        FlowRunStatus.BudgetExceeded => "⚠️",
        _ => "⏹"
    };

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";

    private static string OneLine(string text) => text.Replace("\r", "").Replace('\n', ' ').Trim();

    private static string Cell(string? text) =>
        string.IsNullOrEmpty(text) ? "" : OneLine(text).Replace("|", "\\|");
}
