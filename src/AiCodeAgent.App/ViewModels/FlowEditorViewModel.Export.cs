using System;
using System.Linq;
using AiCodeAgent.Core.Flows;

namespace AiCodeAgent.App.ViewModels;

/// <summary>The area of the canvas that an exported image covers, in canvas coordinates.</summary>
public readonly record struct FlowExportBounds(double X, double Y, double Width, double Height);

/// <summary>E5-S8: Export ▾ — the Markdown report of a run, the Mermaid diagram and the size of the PNG.</summary>
public partial class FlowEditorViewModel
{
    /// <summary>Space around the outermost card or arrow in an exported image.</summary>
    public const double ExportPadding = 48;

    /// <summary>
    /// The run to report on: the one open in replay, otherwise the newest saved run of this flow.
    /// Null while a run is in progress or when the flow has no saved run.
    /// </summary>
    public FlowRunRecord? ExportRun()
    {
        if (_runStore == null) return null;
        if (IsReplay) return _replayRecord;
        if (IsRunning || string.IsNullOrWhiteSpace(FlowName)) return null;
        var newest = History.FirstOrDefault();
        return newest == null ? null : _runStore.Load(FlowName.Trim(), newest.RunId);
    }

    /// <summary>The Markdown report of <see cref="ExportRun"/>; null (with a hint in the status line) when there is no run yet.</summary>
    public string? BuildMarkdownReport()
    {
        var run = ExportRun();
        if (run == null || _runStore == null)
        {
            StatusText = IsRunning
                ? "Wait for the run to finish, then export its report."
                : "No run to report on yet. Run the flow or open a run from History.";
            return null;
        }
        var report = FlowReportBuilder.Markdown(run, step => _runStore.LoadOutput(run.FlowName, run.Id, step));
        StatusText = $"Copied the report of run {run.Id}.";
        return report;
    }

    /// <summary>
    /// E7-S2: the <c>audit.jsonl</c> of <see cref="ExportRun"/>; null (with a hint in the status line) when there is no run
    /// yet or the run is older than audit trails.
    /// </summary>
    public string? AuditFileForExport()
    {
        var run = ExportRun();
        if (run == null || _runStore == null)
        {
            StatusText = IsRunning
                ? "Wait for the run to finish, then save its audit trail."
                : "No run yet. Run the flow or open a run from History.";
            return null;
        }
        var file = System.IO.Path.Combine(_runStore.RunDirectory(run.FlowName, run.Id), AiCodeAgent.Core.Audit.AuditChain.FileName);
        if (!System.IO.File.Exists(file))
        {
            StatusText = $"Run {run.Id} has no audit trail (it ran before audit trails existed).";
            return null;
        }
        return file;
    }

    /// <summary>Mermaid for the flow on the canvas; in replay it also shows what happened in that run.</summary>
    public string BuildMermaid()
    {
        if (IsReplay && _replayRecord is { } run) return FlowReportBuilder.Mermaid(run);
        return FlowGraph.Build(ToDefinition(), null).ToMermaid();
    }

    /// <summary>
    /// The smallest box that holds every card and every arrow (loop arcs included) plus <see cref="ExportPadding"/>,
    /// so a saved PNG never clips a node. An empty flow gets a small blank box.
    /// </summary>
    public FlowExportBounds ExportBounds()
    {
        if (Nodes.Count == 0) return new FlowExportBounds(0, 0, 320, 200);
        var minX = Nodes.Min(n => n.X);
        var minY = Nodes.Min(n => n.Y);
        var maxX = Nodes.Max(n => n.X + FlowNodeViewModel.Width);
        var maxY = Nodes.Max(n => n.Y + FlowNodeViewModel.Height);
        foreach (var e in Edges)
        {
            var ext = e.Extent();
            minX = Math.Min(minX, ext.MinX);
            minY = Math.Min(minY, ext.MinY);
            maxX = Math.Max(maxX, ext.MaxX);
            maxY = Math.Max(maxY, ext.MaxY);
        }
        // Arrow labels sit at the middle of a curve and are wider than a point; leave room above the highest arc.
        minY -= 24;
        var x = Math.Max(0, minX - ExportPadding);
        var y = Math.Max(0, minY - ExportPadding);
        return new FlowExportBounds(x, y, maxX + ExportPadding - x, maxY + ExportPadding - y);
    }
}
