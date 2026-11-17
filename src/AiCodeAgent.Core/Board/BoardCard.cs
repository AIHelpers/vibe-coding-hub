using System.Text;
using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Board;

/// <summary>The four columns of the Mission Control board (E11-S1).</summary>
public enum BoardColumn
{
    /// <summary>Waiting to be started.</summary>
    Backlog,
    /// <summary>An agent is working on it in its own worktree.</summary>
    Running,
    /// <summary>The agent stopped (finished, failed or was stopped); a human looks at the result.</summary>
    NeedsReview,
    /// <summary>A human accepted the result.</summary>
    Done
}

/// <summary>How the last run of a card ended. Shown as a badge on cards in <see cref="BoardColumn.NeedsReview"/>.</summary>
public enum CardOutcome
{
    /// <summary>No run has ended yet.</summary>
    None,
    /// <summary>The agent finished and its changes are committed on the card's branch.</summary>
    Completed,
    /// <summary>The agent finished without changing a file (an answer, an analysis); the worktree was removed.</summary>
    NoChanges,
    /// <summary>The agent or the worktree failed; whatever was done is kept on the branch.</summary>
    Failed,
    /// <summary>The user stopped the card; the work so far is committed on the branch.</summary>
    Stopped,
    /// <summary>The app closed while the card ran; unsaved work is still in the worktree folder.</summary>
    Interrupted
}

/// <summary>
/// One task on the board: one card = one task = one git worktree on its own branch. Immutable; the board replaces the
/// card with a changed copy, so a view never sees a half-updated card.
/// </summary>
public sealed record BoardCard
{
    public string Id { get; init; } = NewId();
    public string Title { get; init; } = string.Empty;
    /// <summary>What the agent is asked to do.</summary>
    public string Prompt { get; init; } = string.Empty;
    /// <summary>Who does the work (E11-S2). A retry uses the same agent; change it while the card is waiting in the Backlog or after a run.</summary>
    public AgentTarget Agent { get; init; } = AgentTarget.Default;
    public BoardColumn Column { get; init; } = BoardColumn.Backlog;
    public CardOutcome Outcome { get; init; } = CardOutcome.None;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>When the latest run started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>When the latest run ended; null while running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>How many times the card was started (a retry starts a new run on a new branch).</summary>
    public int Runs { get; init; }
    /// <summary>The branch the work of the latest run is on; null before the first run and when a run changed nothing.</summary>
    public string? Branch { get; init; }
    /// <summary>The folder of the card's worktree while it exists.</summary>
    public string? WorktreePath { get; init; }
    /// <summary>The last thing the agent did, for example "edit_file src/Program.cs".</summary>
    public string? LastAction { get; init; }
    /// <summary>The agent's final answer, or why the run failed.</summary>
    public string? Summary { get; init; }
    /// <summary>Total spend over all runs of the card.</summary>
    public decimal CostUsd { get; init; }
    /// <summary>Total tokens over all runs of the card.</summary>
    public long Tokens { get; init; }
    /// <summary>Tool calls of the latest run.</summary>
    public int ToolCalls { get; init; }

    /// <summary>The agent is paused and waits for you (E11-S3). Only while the card runs; never saved.</summary>
    [JsonIgnore]
    public bool IsPaused { get; init; }
    /// <summary>The running agent can be paused, messaged and redirected (E11-S3). Flows and external agents can only be stopped. Never saved.</summary>
    [JsonIgnore]
    public bool CanSteer { get; init; }

    public bool IsRunning => Column == BoardColumn.Running;

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>A card title from the first line of a prompt: whitespace collapsed, cut at 70 characters.</summary>
    public static string TitleFrom(string? prompt)
    {
        var line = (prompt ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        var sb = new StringBuilder(line.Length);
        var space = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        var title = sb.ToString();
        return title.Length > 70 ? title[..69].TrimEnd() + "…" : title;
    }
}
