using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Git;

/// <summary>
/// <see cref="IWorkspaceIsolation"/> backed by <c>git worktree</c>: each agent gets its own checkout on branch
/// <c>agent/&lt;session&gt;/&lt;agent&gt;</c> (created from HEAD) under <c>&lt;repo-parent&gt;/.aiagent-worktrees/&lt;repo&gt;/</c>.
/// Work is committed there and merged back with <c>--no-ff</c>, one agent at a time. Uncommitted changes in the
/// main checkout are NOT visible to the agents (worktrees start from HEAD).
/// </summary>
public class GitWorktreeManager : IWorkspaceIsolation
{
    // One merge at a time per repository: concurrent merges into the same checkout would corrupt its index.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MergeLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<GitWorktreeManager> _logger;

    public GitWorktreeManager(ILogger<GitWorktreeManager> logger) => _logger = logger;

    public async Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(baseDirectory)) return null;

            var top = await GitAsync(baseDirectory, ct, "rev-parse", "--show-toplevel");
            if (top.ExitCode != 0) return null;
            var repoRoot = Path.GetFullPath(top.Stdout.Trim());

            // A repository without any commit has no HEAD to branch from.
            if ((await GitAsync(repoRoot, ct, "rev-parse", "--verify", "HEAD")).ExitCode != 0) return null;

            var repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var parent = Path.GetDirectoryName(repoRoot) ?? repoRoot;
            var slug = Slug(agentId);
            var shortSession = Slug(sessionId.Length > 8 ? sessionId[..8] : sessionId);
            var branch = $"agent/{shortSession}/{slug}";
            var dir = Path.Combine(parent, ".aiagent-worktrees", repoName, $"{shortSession}-{slug}");

            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            var add = await GitAsync(repoRoot, ct, "worktree", "add", "-b", branch, dir, "HEAD");
            if (add.ExitCode != 0)
            {
                _logger.LogWarning("git worktree add failed for {Agent}: {Err}", agentId, add.Stderr.Trim());
                return null;
            }

            var relative = Path.GetRelativePath(repoRoot, Path.GetFullPath(baseDirectory));
            var workDir = relative == "." || relative.StartsWith("..", StringComparison.Ordinal)
                ? dir
                : Path.Combine(dir, relative);
            Directory.CreateDirectory(workDir);
            return new WorkspaceLease(agentId, branch, dir, workDir, repoRoot);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create an isolated worktree for {Agent}", agentId);
            return null;
        }
    }

    public async Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default)
    {
        try
        {
            await GitAsync(lease.RootPath, CancellationToken.None, "add", "-A");
            var status = await GitAsync(lease.RootPath, CancellationToken.None, "status", "--porcelain");
            if (string.IsNullOrWhiteSpace(status.Stdout))
            {
                await RemoveAsync(lease);
                return new WorkspaceMergeResult(WorkspaceMergeOutcome.NoChanges, lease.Branch, "No changes.", Array.Empty<string>());
            }

            var message = $"agent {lease.AgentId}: {FirstLine(summary)}";
            var commit = await GitAsync(lease.RootPath, CancellationToken.None, "commit", "-m", message);
            if (commit.ExitCode != 0)
            {
                // Typical cause: no user.name/user.email configured. Retry with a neutral identity.
                commit = await GitAsync(lease.RootPath, CancellationToken.None,
                    "-c", "user.name=AiCodeAgent", "-c", "user.email=agent@localhost", "commit", "-m", message);
                if (commit.ExitCode != 0)
                    return new WorkspaceMergeResult(WorkspaceMergeOutcome.Failed, lease.Branch,
                        $"Commit failed: {commit.Stderr.Trim()}", Array.Empty<string>(), lease.RootPath);
            }

            if (!merge)
                return new WorkspaceMergeResult(WorkspaceMergeOutcome.Kept, lease.Branch,
                    $"Changes committed on branch {lease.Branch} (not merged).", Array.Empty<string>(), lease.RootPath);

            var gate = MergeLocks.GetOrAdd(lease.RepoRoot, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                var m = await GitAsync(lease.RepoRoot, CancellationToken.None,
                    "-c", "user.name=AiCodeAgent", "-c", "user.email=agent@localhost",
                    "merge", "--no-ff", "-m", $"Merge agent {lease.AgentId} ({lease.Branch})", lease.Branch);
                if (m.ExitCode == 0)
                {
                    await RemoveAsync(lease);
                    return new WorkspaceMergeResult(WorkspaceMergeOutcome.Merged, lease.Branch,
                        $"Merged {lease.Branch} into the current branch.", Array.Empty<string>());
                }

                var conflicts = (await GitAsync(lease.RepoRoot, CancellationToken.None, "diff", "--name-only", "--diff-filter=U"))
                    .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
                await GitAsync(lease.RepoRoot, CancellationToken.None, "merge", "--abort"); // no-op if git refused before merging
                var detail = conflicts.Length > 0
                    ? $"Merge conflicts in: {string.Join(", ", conflicts)}."
                    : $"Merge refused: {FirstLine(m.Stderr.Length > 0 ? m.Stderr : m.Stdout)}";
                return new WorkspaceMergeResult(WorkspaceMergeOutcome.Conflict, lease.Branch,
                    $"{detail} Branch {lease.Branch} and its worktree were kept for manual resolution.",
                    conflicts, lease.RootPath);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Completing worktree for {Agent} failed", lease.AgentId);
            return new WorkspaceMergeResult(WorkspaceMergeOutcome.Failed, lease.Branch, ex.Message, Array.Empty<string>(), lease.RootPath);
        }
    }

    private async Task RemoveAsync(WorkspaceLease lease)
    {
        await GitAsync(lease.RepoRoot, CancellationToken.None, "worktree", "remove", "--force", lease.RootPath);
        await GitAsync(lease.RepoRoot, CancellationToken.None, "branch", "-D", lease.Branch);
    }

    internal static string Slug(string value)
    {
        var s = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return s.Length == 0 ? "agent" : s.Length > 40 ? s[..40] : s;
    }

    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "changes";
        return line.Length > 72 ? line[..72] : line;
    }

    private readonly record struct GitOutput(int ExitCode, string Stdout, string Stderr);

    private async Task<GitOutput> GitAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            return new GitOutput(p.ExitCode, await o, await e);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GitOutput(-1, string.Empty, ex.Message);
        }
    }
}
