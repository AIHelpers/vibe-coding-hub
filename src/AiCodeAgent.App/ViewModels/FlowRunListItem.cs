using System;
using System.Globalization;
using AiCodeAgent.Core.Flows;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One entry of the History dropdown: a saved run of the open flow.</summary>
public sealed class FlowRunListItem
{
    public FlowRunListItem(FlowRunSummary run) => Run = run;

    public FlowRunSummary Run { get; }
    public string RunId => Run.RunId;

    public string Glyph => Run.Status switch
    {
        FlowRunStatus.Succeeded => "✓",
        FlowRunStatus.Failed => "✕",
        FlowRunStatus.BudgetExceeded => "$",
        _ => "–"
    };

    public string When => Run.StartedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    public string Status => FlowUsageFormat.StatusName(Run.Status);
    public string Cost => Run.Tokens == 0 ? "–" : FlowUsageFormat.Cost(Run.CostUsd);
    public string Duration => Run.Duration.TotalSeconds < 60
        ? Run.Duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
        : $"{(int)Run.Duration.TotalMinutes}m {Run.Duration.Seconds}s";

    /// <summary>"↺ from review" for a re-run, else empty.</summary>
    public string ReplayNote => Run.ReplayFromNodeId == null ? string.Empty : $"↺ from {Run.ReplayFromNodeId}";

    public string TaskText
    {
        get
        {
            var t = Run.Task.ReplaceLineEndings(" ").Trim();
            return t.Length > 60 ? t[..60] + "…" : t;
        }
    }

    /// <summary>Time, status, cost, duration and task on one line.</summary>
    public string Label =>
        $"{When} · {Glyph} {Status} · {Cost} · {Duration}" + (ReplayNote.Length > 0 ? $" · {ReplayNote}" : "") + (TaskText.Length > 0 ? $" · {TaskText}" : "");

    public override string ToString() => Label;
}
