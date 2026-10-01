using System;
using System.Collections.Generic;
using AiCodeAgent.Core.Diffing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AiCodeAgent.App.Controls;

/// <summary>
/// One line of a diff: plain text, or — for a modified line — text where the words that changed carry a
/// stronger background (GitHub's word-level highlight). The runs are built here in code rather than bound,
/// because bindings on individual <see cref="Run"/> elements are not reliable.
/// </summary>
public class DiffTextBlock : SelectableTextBlock
{
    // Look like a SelectableTextBlock to the theme (font, foreground, selection colours) rather than needing its own style.
    // Selectable, so code can be selected and copied straight out of the diff.
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public static readonly StyledProperty<string?> LineTextProperty =
        AvaloniaProperty.Register<DiffTextBlock, string?>(nameof(LineText));

    public static readonly StyledProperty<IReadOnlyList<DiffSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<DiffTextBlock, IReadOnlyList<DiffSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<DiffTextBlock, IBrush?>(nameof(HighlightBrush));

    /// <summary>The whole line.</summary>
    public string? LineText
    {
        get => GetValue(LineTextProperty);
        set => SetValue(LineTextProperty, value);
    }

    /// <summary>Optional word-level pieces of <see cref="LineText"/>; null means "no highlighting".</summary>
    public IReadOnlyList<DiffSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Background for the changed words.</summary>
    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    static DiffTextBlock()
    {
        LineTextProperty.Changed.AddClassHandler<DiffTextBlock>((c, _) => c.Rebuild());
        SegmentsProperty.Changed.AddClassHandler<DiffTextBlock>((c, _) => c.Rebuild());
        HighlightBrushProperty.Changed.AddClassHandler<DiffTextBlock>((c, _) => c.Rebuild());
    }

    private void Rebuild()
    {
        var segments = Segments;
        if (segments == null || segments.Count == 0)
        {
            Inlines?.Clear();
            Text = LineText ?? string.Empty;
            return;
        }

        Text = null;
        var inlines = Inlines ?? (Inlines = new InlineCollection());
        inlines.Clear();
        foreach (var segment in segments)
        {
            inlines.Add(new Run(segment.Text)
            {
                Background = segment.Changed ? HighlightBrush : null
            });
        }
    }
}
