using System.Collections.Generic;

namespace AiCodeAgent.Tools.Git;

/// <summary>
/// Structured result of a git operation, suitable for rendering as a rich
/// card in the chat UI (Feature 6: In-Chat Branch / PR Workflow).
/// </summary>
public abstract record GitResult
{
    /// <summary>Whether the underlying git command succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable summary shown in the chat card.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Raw command output (stdout + stderr) for diagnostics.</summary>
    public string Output { get; init; } = string.Empty;
}

/// <summary>Result of creating a branch.</summary>
public record BranchResult : GitResult
{
    /// <summary>The branch that was created (or checked out).</summary>
    public string BranchName { get; init; } = string.Empty;
}

/// <summary>Result of staging and committing changes.</summary>
public record CommitResult : GitResult
{
    /// <summary>The new commit hash.</summary>
    public string CommitHash { get; init; } = string.Empty;

    /// <summary>Files included in the commit.</summary>
    public IReadOnlyList<string> Files { get; init; } = new List<string>();
}

/// <summary>Result of pushing a branch and opening a pull request.</summary>
public record PullRequestResult : GitResult
{
    /// <summary>The branch that was pushed.</summary>
    public string BranchName { get; init; } = string.Empty;

    /// <summary>The pull request URL (clickable in chat).</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>The auto-generated PR body (diff summary).</summary>
    public string? PullRequestBody { get; init; }
}

/// <summary>Current repository status.</summary>
public record StatusResult : GitResult
{
    /// <summary>The current branch name.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>Files that are modified/staged/untracked.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = new List<string>();
}