using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AiCodeAgent.App.EditHistory;

namespace AiCodeAgent.App.Editor;

/// <summary>
/// AvaloniaEdit background renderer that draws "ghost text" for a predicted
/// next-edit span at the target line, giving a Copilot-style inline preview.
/// </summary>
public class GhostTextRenderer : IBackgroundRenderer
{
    private static readonly IBrush GhostBrush = new SolidColorBrush(Color.FromArgb(120, 120, 120, 160));
    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    private readonly List<NextEditSpan> _spans = new();

    public KnownLayer Layer => KnownLayer.Background;

    public void UpdatePrediction(IEnumerable<NextEditSpan>? spans)
    {
        _spans.Clear();
        if (spans != null)
            _spans.AddRange(spans);
    }

    public void Clear() => _spans.Clear();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_spans.Count == 0)
            return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            foreach (var span in _spans)
            {
                if (lineNumber >= span.StartLine && lineNumber <= span.EndLine + 1)
                {
                    DrawGhostForSpan(drawingContext, visualLine, span, lineNumber);
                    break;
                }
            }
        }
    }

    private static void DrawGhostForSpan(
        DrawingContext drawingContext,
        VisualLine visualLine,
        NextEditSpan span,
        int lineNumber)
    {
        var lines = span.NewText.Replace("\r\n", "\n").Split('\n');
        var lineIndex = lineNumber - span.StartLine;
        var textToDraw = span.IsInsert
            ? string.Join(" ⏎ ", lines)
            : (lineIndex >= 0 && lineIndex < lines.Length ? lines[lineIndex] : null);

        if (string.IsNullOrEmpty(textToDraw))
            return;

        var y = visualLine.VisualTop;
        // Draw at a fixed left offset within the text area; exact end-of-line
        // positioning would require measuring the visual column, which is not
        // exposed by AvaloniaEdit's public VisualLine API.
        var x = 24.0;

        var formatted = new FormattedText(
            textToDraw,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            DefaultTypeface,
            14,
            GhostBrush);
        drawingContext.DrawText(formatted, new Point(x, y));
    }
}