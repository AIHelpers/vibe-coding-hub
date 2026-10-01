using System.Collections.Generic;

namespace AiCodeAgent.App.EditHistory;

/// <summary>
/// A predicted edit span: either an insertion at a line or a replacement
/// of a line range with <see cref="NewText"/>.
/// </summary>
public sealed record NextEditSpan
{
    /// <summary>1-based start line of the edit.</summary>
    public int StartLine { get; init; }

    /// <summary>1-based end line (inclusive) for replacements; equals StartLine for inserts.</summary>
    public int EndLine { get; init; }

    /// <summary>The new text to insert/replace with.</summary>
    public string NewText { get; init; } = string.Empty;

    /// <summary>True for a pure insertion; false for a line-range replacement.</summary>
    public bool IsInsert { get; init; }

    /// <summary>Optional confidence score [0..1] from the model.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// A complete next-edit prediction: one or more spans plus metadata.
/// </summary>
public sealed record NextEditPrediction
{
    public string FilePath { get; init; } = string.Empty;

    public IReadOnlyList<NextEditSpan> Spans { get; init; } = new List<NextEditSpan>();

    /// <summary>Overall confidence of the prediction [0..1].</summary>
    public double Confidence { get; init; }

    /// <summary>Model latency in milliseconds, for telemetry.</summary>
    public long LatencyMs { get; init; }
}