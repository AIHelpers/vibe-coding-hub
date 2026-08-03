using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AiCodeAgent.Core.Diffing;

namespace AiCodeAgent.App.Editor;

/// <summary>
/// Implements AvaloniaEdit's IBackgroundRenderer to draw colored backgrounds
/// for added/removed lines directly in the editor's text view.
/// </summary>
public class DiffOverlayRenderer : IBackgroundRenderer
{
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromArgb(60, 76, 175, 80));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromArgb(60, 244, 67, 54));
    private static readonly IBrush AcceptedBrush = new SolidColorBrush(Color.FromArgb(30, 76, 175, 80));
    private static readonly IBrush RejectedBrush = new SolidColorBrush(Color.FromArgb(30, 158, 158, 158));

    private readonly Dictionary<int, DiffLineKind> _lineKinds = new();

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Update the overlay with the hunks for the current file.</summary>
    public void UpdateHunks(IEnumerable<DiffHunk> hunks)
    {
        _lineKinds.Clear();

        if (hunks == null)
            return;

        foreach (var hunk in hunks)
        {
            var lineOffset = hunk.NewStartLine > 0 ? hunk.NewStartLine - 1 : 0;
            var currentLine = lineOffset;

            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Added:
                        _lineKinds[currentLine] = hunk.Status switch
                        {
                            HunkStatus.Accepted => DiffLineKind.Added,
                            _ => DiffLineKind.Added
                        };
                        currentLine++;
                        break;
                    case DiffLineKind.Removed:
                        _lineKinds[currentLine] = DiffLineKind.Removed;
                        // Removed lines don't advance in the new file
                        break;
                    case DiffLineKind.Context:
                        currentLine++;
                        break;
                }
            }
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_lineKinds.Count == 0)
            return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            if (!_lineKinds.TryGetValue(lineNumber - 1, out var kind))
                continue;

            var brush = kind switch
            {
                DiffLineKind.Added => AddedBrush,
                DiffLineKind.Removed => RemovedBrush,
                _ => AddedBrush
            };

            var rect = new Rect(
                0,
                visualLine.VisualTop,
                textView.Bounds.Width,
                visualLine.Height);

            drawingContext.FillRectangle(brush, rect);
        }
    }
}