namespace AiCodeAgent.Core.Diffing;

/// <summary>Status of a diff hunk in the review workflow.</summary>
public enum HunkStatus
{
    Pending,
    Accepted,
    Rejected
}

/// <summary>Kind of a single diff line.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed
}

/// <summary>A single line in a diff hunk.</summary>
public record DiffLine(DiffLineKind Kind, string Text);

/// <summary>
/// A single hunk of a file diff, with per-hunk attribution for multi-agent sessions.
/// This is the load-bearing data model shared by the editor and hunk review UI.
/// </summary>
public record DiffHunk(
    string HunkId,
    string FilePath,
    int OriginalStartLine,
    int OriginalLineCount,
    int NewStartLine,
    int NewLineCount,
    IReadOnlyList<DiffLine> Lines,
    string AgentId,
    HunkStatus Status);