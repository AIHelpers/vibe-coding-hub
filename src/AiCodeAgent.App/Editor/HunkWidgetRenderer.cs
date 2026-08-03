using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.App.ViewModels;

namespace AiCodeAgent.App.Editor;

/// <summary>
/// Renders floating inline Accept/Reject buttons for pending hunks
/// in the editor's text view.
/// </summary>
public class HunkWidgetRenderer : IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private readonly EditorPaneViewModel? _viewModel;
    private readonly List<DiffHunk> _pendingHunks = new();

    public KnownLayer Layer => KnownLayer.Background;

    public HunkWidgetRenderer(TextEditor editor, EditorPaneViewModel? viewModel)
    {
        _editor = editor;
        _viewModel = viewModel;
    }

    /// <summary>Update the pending hunks for the current file.</summary>
    public void UpdateHunks(IEnumerable<DiffHunk> hunks)
    {
        _pendingHunks.Clear();
        if (hunks != null)
        {
            _pendingHunks.AddRange(hunks.Where(h => h.Status == HunkStatus.Pending));
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_pendingHunks.Count == 0 || _viewModel == null)
            return;

        foreach (var hunk in _pendingHunks)
        {
            // Find the visual line for the hunk's start position
            if (hunk.NewStartLine <= 0)
                continue;

            var visualLine = textView.VisualLines
                .FirstOrDefault(vl =>
                    vl.FirstDocumentLine.LineNumber == hunk.NewStartLine ||
                    (vl.FirstDocumentLine.LineNumber <= hunk.NewStartLine &&
                     vl.LastDocumentLine.LineNumber >= hunk.NewStartLine));

            if (visualLine == null)
                continue;

            // Draw a colored marker bar in the margin
            var markerBrush = new SolidColorBrush(Color.FromArgb(200, 76, 175, 80));
            var markerRect = new Rect(
                2,
                visualLine.VisualTop + 2,
                4,
                Math.Max(visualLine.Height - 4, 8));

            drawingContext.FillRectangle(markerBrush, markerRect);
        }
    }
}