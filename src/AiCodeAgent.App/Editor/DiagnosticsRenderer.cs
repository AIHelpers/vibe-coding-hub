using System;
using System.Collections.Generic;
using AiCodeAgent.Core.Models;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace AiCodeAgent.App.Editor;

/// <summary>
/// Implements AvaloniaEdit's IBackgroundRenderer to draw squiggly underlines
/// for LSP diagnostics (errors and warnings) directly in the text view.
/// </summary>
public class DiagnosticsRenderer : IBackgroundRenderer
{
    private enum SeverityRank { Info, Warning, Error }

    private static readonly IPen ErrorPen = CreatePen(255, 77, 79);      // red
    private static readonly IPen WarningPen = CreatePen(255, 193, 7);    // amber
    private static readonly IPen InfoPen = CreatePen(77, 150, 255);      // blue

    // (1-based document line, rank)
    private readonly Dictionary<int, SeverityRank> _lines = new();

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Update the renderer with diagnostics for the current file.</summary>
    public void UpdateDiagnostics(IEnumerable<LspDiagnosticItem> diagnostics)
    {
        _lines.Clear();

        if (diagnostics == null)
            return;

        foreach (var diag in diagnostics)
        {
            var rank = diag.Severity.ToLowerInvariant() switch
            {
                "error" => SeverityRank.Error,
                "warning" => SeverityRank.Warning,
                _ => SeverityRank.Info
            };

            var lineNumber = diag.StartLine + 1;
            if (!_lines.TryGetValue(lineNumber, out var existing) || rank > existing)
                _lines[lineNumber] = rank;
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_lines.Count == 0)
            return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            if (!_lines.TryGetValue(lineNumber, out var rank))
                continue;

            var pen = rank switch
            {
                SeverityRank.Warning => WarningPen,
                SeverityRank.Info => InfoPen,
                _ => ErrorPen
            };

            var y = visualLine.VisualTop + visualLine.Height - 1.5;
            var width = textView.Bounds.Width;

            var geometry = MakeSquiggle(0, y, width);
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }

    private static IPen CreatePen(byte r, byte g, byte b) =>
        new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), 1.2);

    private static StreamGeometry MakeSquiggle(double startX, double y, double width)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var step = 4.0;
        var phase = 0.0;
        var x = startX;
        var first = true;

        while (x <= startX + width)
        {
            var point = new Point(x, y + Math.Sin(phase) * 1.5);
            if (first)
            {
                ctx.BeginFigure(point, false);
                first = false;
            }
            else
            {
                ctx.LineTo(point);
            }
            x += step;
            phase += Math.PI / 2;
        }

        ctx.EndFigure(false);
        return geometry;
    }
}