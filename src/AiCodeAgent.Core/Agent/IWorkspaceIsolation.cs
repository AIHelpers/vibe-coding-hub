namespace AiCodeAgent.Core.Agent;

/// <summary>A private working copy handed to one agent (typically a git worktree on its own branch).</summary>
/// <param name="AgentId">Agent the lease belongs to.</param>
/// <param name="Branch">Branch the agent's work lands on.</param>
/// <param name="RootPath">Root of the private working copy.</param>
/// <param name="WorkingDirectory">Directory the agent should use (RootPath, or the matching subfolder when the base dir was not the repo root).</param>
/// <param name="RepoRoot">Root of the original repository the work merges back into.</param>
public sealed record WorkspaceLease(string AgentId, string Branch, string RootPath, string WorkingDirectory, string RepoRoot);

public enum WorkspaceMergeOutcome
{
    /// <summary>The agent changed nothing; the private copy was discarded.</summary>
    NoChanges,
    /// <summary>The agent's commit was merged into the base branch and the private copy removed.</summary>
    Merged,
    /// <summary>Merging conflicted (or was refused); the branch and private copy are kept for manual resolution.</summary>
    Conflict,
    /// <summary>Changes were committed but intentionally not merged (e.g. the run was cancelled); branch kept.</summary>
    Kept,
    /// <summary>Something else went wrong; branch and copy are kept.</summary>
    Failed
}

public sealed record WorkspaceMergeResult(
    WorkspaceMergeOutcome Outcome,
    string Branch,
    string Message,
    IReadOnlyList<string> ConflictFiles,
    string? RetainedPath = null)
{
    public bool Succeeded => Outcome is WorkspaceMergeOutcome.NoChanges or WorkspaceMergeOutcome.Merged;
}

/// <summary>
/// Gives each parallel agent its own copy of the workspace so they cannot overwrite each other's files,
/// and folds the results back afterwards. Implemented on top of <c>git worktree</c> in the Tools project.
/// </summary>
public interface IWorkspaceIsolation
{
    /// <summary>
    /// Creates a private copy for <paramref name="agentId"/>, or returns null when isolation is not possible
    /// (not a git repository, no commits yet, git missing) — callers then simply run in the shared directory.
    /// </summary>
    Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default);

    /// <summary>Commits the agent's work and, when <paramref name="merge"/> is true, merges it into the base branch.</summary>
    Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default);
}
