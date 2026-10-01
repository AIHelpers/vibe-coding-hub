using System.Collections.Generic;
using AiCodeAgent.Core.Diffing;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Presentation of one <see cref="SideBySideRow"/> in the split diff: which kind of row it is, the line
/// numbers, and the GitHub-style colours (red on the old side for removed lines, green on the new side for added ones).
/// </summary>
public sealed class DiffRowViewModel
{
    // Translucent so they read on both the light and the dark theme.
    private const string RemovedLine = "#33E05555";
    private const string AddedLine = "#334CAF50";
    private const string EmptyCell = "#14808080";
    private const string Transparent = "Transparent";

    public DiffRowViewModel(SideBySideRow row)
    {
        Row = row;
    }

    public SideBySideRow Row { get; }

    public bool IsLine => Row.Kind == SideBySideRowKind.Line;
    public bool IsHunkHeader => Row.Kind == SideBySideRowKind.HunkHeader;
    public bool IsGap => Row.Kind == SideBySideRowKind.Gap;

    // ----- old (left) side -----
    public string OldNumber => Row.OldNumber?.ToString() ?? string.Empty;
    public string OldText => Row.OldText;
    public IReadOnlyList<DiffSegment>? OldSegments => Row.OldSegments;
    public string OldMarker => Row.OldKind == SideBySideCellKind.Removed ? "−" : string.Empty;
    public string OldBackground => BackgroundFor(Row.OldKind);
    public string OldMarkerColor => "#E05555";

    // ----- new (right) side -----
    public string NewNumber => Row.NewNumber?.ToString() ?? string.Empty;
    public string NewText => Row.NewText;
    public IReadOnlyList<DiffSegment>? NewSegments => Row.NewSegments;
    public string NewMarker => Row.NewKind == SideBySideCellKind.Added ? "+" : string.Empty;
    public string NewBackground => BackgroundFor(Row.NewKind);
    public string NewMarkerColor => "#4CAF50";

    // ----- hunk header -----
    public string HeaderText => Row.Header;
    public DiffHunk? Hunk => Row.Hunk;

    // ----- collapsed unchanged lines -----
    public int GapOldStart => Row.GapOldStart;
    public int GapNewStart => Row.GapNewStart;
    public int GapCount => Row.GapCount;
    public string GapText => Row.GapCount == 1 ? "⋯  Show 1 unchanged line" : $"⋯  Show {Row.GapCount} unchanged lines";

    private static string BackgroundFor(SideBySideCellKind kind) => kind switch
    {
        SideBySideCellKind.Removed => RemovedLine,
        SideBySideCellKind.Added => AddedLine,
        SideBySideCellKind.Empty => EmptyCell,
        _ => Transparent
    };
}
