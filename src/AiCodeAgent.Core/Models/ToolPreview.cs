namespace AiCodeAgent.Core.Models;

/// <summary>
/// What a tool call would do, computed without side effects so a human can review it before approving:
/// a file change (old vs new content) or the command(s) it would run. <see cref="Error"/> is set when the call
/// would fail as it stands (e.g. <c>old_string</c> not found); approving it would then just return that error.
/// </summary>
public sealed record ToolPreview
{
    public FileChangePreview? FileChange { get; init; }
    public CommandPreview? Command { get; init; }
    public string? Error { get; init; }

    public static ToolPreview Failed(string error) => new() { Error = error };
}

/// <summary>A file the call would create or change. <see cref="OldContent"/> is null for a new file.</summary>
public sealed record FileChangePreview(string Path, string? OldContent, string NewContent)
{
    public bool IsNewFile => OldContent is null;
}

/// <summary>The command(s) a shell tool would run, where, and whether the Docker sandbox would isolate them.</summary>
public sealed record CommandPreview(
    IReadOnlyList<string> Commands,
    string WorkingDirectory,
    bool SandboxEnabled,
    string? SandboxDescription = null);

/// <summary>
/// Filled by the UI when the user denies a call with a note ("use the existing helper instead"). The orchestrator
/// returns it to the model as the tool result, so the agent can continue in the right direction.
/// </summary>
public sealed class ApprovalFeedback
{
    public string? Text { get; set; }

    /// <summary>
    /// Flow gates only: when the answer is "no", skip this step and carry on with the rest of the flow instead of
    /// stopping the whole flow.
    /// </summary>
    public bool Skip { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
