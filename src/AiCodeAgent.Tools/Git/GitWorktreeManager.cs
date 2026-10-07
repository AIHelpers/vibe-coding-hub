using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Git;

/// <summary>
/// <see cref="IWorkspaceIsolation"/> backed by <c>git worktree</c>: each agent gets its own checkout on branch
/// <c>agent/&lt;session&gt;/&lt;agent&gt;</c> under <c>&lt;repo-parent&gt;/.aiagent-worktrees/&lt;repo&gt;/</c>.
/// <list type="bullet">
/// <item>Clean main checkout: the copy starts from HEAD; the agent's commit is merged back with <c>--no-ff</c>.</item>
/// <item>Uncommitted work in the main checkout (e.g. written by an earlier flow step): it is captured in a snapshot
/// commit (without touching the branch, index or files), the copy starts from it, and the agent's changes are applied
/// back to the working tree as uncommitted changes.</item>
/// </list>
/// One merge/apply at a time per repository.
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
            // The hash keeps names apart for sessions that share a prefix (e.g. "<flow>/<step>#1" and "#2").
            var shortSession = Slug(sessionId.Length > 8 ? sessionId[..8] : sessionId) + "-" + StableHash(sessionId);
            var branch = $"agent/{shortSession}/{slug}";
            var dir = Path.Combine(parent, ".aiagent-worktrees", repoName, $"{shortSession}-{slug}");

            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            var snapshot = await SnapshotAsync(repoRoot, ct);
            var add = await GitAsync(repoRoot, ct, "worktree", "add", "-b", branch, dir, snapshot ?? "HEAD");
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
            return new WorkspaceLease(agentId, branch, dir, workDir, repoRoot, snapshot);
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
                if (lease.BaseSnapshot != null)
                    return await ApplyToWorkingTreeAsync(lease);

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

    /// <summary>
    /// The copy started from a snapshot of uncommitted work: apply only what the agent changed since that snapshot
    /// to the main working tree (left uncommitted, like the work it started from).
    /// </summary>
    private async Task<WorkspaceMergeResult> ApplyToWorkingTreeAsync(WorkspaceLease lease)
    {
        var patch = Path.Combine(Path.GetTempPath(), $"aiagent-{Slug(lease.AgentId)}-{Guid.NewGuid():N}.patch");
        try
        {
            // git writes the patch itself, byte for byte (no re-encoding of file contents).
            var diff = await GitAsync(lease.RepoRoot, CancellationToken.None, "diff", "--binary", $"--output={patch}", lease.BaseSnapshot!, lease.Branch);
            if (diff.ExitCode != 0)
                return new WorkspaceMergeResult(WorkspaceMergeOutcome.Failed, lease.Branch,
                    $"Could not compute the agent's changes: {FirstLine(diff.Stderr)}", Array.Empty<string>(), lease.RootPath);
            if (!File.Exists(patch) || new FileInfo(patch).Length == 0)
            {
                await RemoveAsync(lease);
                return new WorkspaceMergeResult(WorkspaceMergeOutcome.NoChanges, lease.Branch, "No changes.", Array.Empty<string>());
            }
            var apply = await GitAsync(lease.RepoRoot, CancellationToken.None, "apply", "--whitespace=nowarn", patch);
            if (apply.ExitCode == 0)
            {
                await RemoveAsync(lease);
                return new WorkspaceMergeResult(WorkspaceMergeOutcome.Merged, lease.Branch,
                    $"Applied {lease.Branch} to the working tree (uncommitted).", Array.Empty<string>());
            }
            var files = Regex.Matches(apply.Stderr, @"(?:patch failed|does not exist in index|already exists in working directory): ([^:
]+)")
                .Select(m => m.Groups[1].Value.Trim()).Distinct().ToArray();
            var detail = files.Length > 0 ? $"Conflicting changes in: {string.Join(", ", files)}." : $"Could not apply: {FirstLine(apply.Stderr)}";
            return new WorkspaceMergeResult(WorkspaceMergeOutcome.Conflict, lease.Branch,
                $"{detail} Branch {lease.Branch} and its worktree were kept for manual resolution.", files, lease.RootPath);
        }
        finally
        {
            try { File.Delete(patch); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// When the working tree differs from HEAD (changes, new files; ignored files excluded), records it as a commit
    /// object without touching the branch, the index or any file, and returns its id. Null when the tree is clean.
    /// </summary>
    private async Task<string?> SnapshotAsync(string repoRoot, CancellationToken ct)
    {
        var tempIndex = Path.Combine(Path.GetTempPath(), $"aiagent-index-{Guid.NewGuid():N}");
        try
        {
            // Start from a copy of the real index (its file stats make "add" fast), else from HEAD.
            var indexPath = (await GitAsync(repoRoot, ct, "rev-parse", "--git-path", "index")).Stdout.Trim();
            var realIndex = Path.GetFullPath(Path.Combine(repoRoot, indexPath));
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
            if (File.Exists(realIndex)) File.Copy(realIndex, tempIndex, overwrite: true);
            else await GitAsync(repoRoot, ct, env, "read-tree", "HEAD");

            if ((await GitAsync(repoRoot, ct, env, "add", "-A")).ExitCode != 0) return null;
            var tree = (await GitAsync(repoRoot, ct, env, "write-tree")).Stdout.Trim();
            var headTree = (await GitAsync(repoRoot, ct, "rev-parse", "HEAD^{tree}")).Stdout.Trim();
            if (tree.Length == 0 || tree == headTree) return null;

            var commit = await GitAsync(repoRoot, ct, "-c", "user.name=AiCodeAgent", "-c", "user.email=agent@localhost",
                "commit-tree", tree, "-p", "HEAD", "-m", "aiagent: snapshot of uncommitted work");
            return commit.ExitCode == 0 && commit.Stdout.Trim().Length > 0 ? commit.Stdout.Trim() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not snapshot uncommitted work in {Repo}; agents start from HEAD", repoRoot);
            return null;
        }
        finally
        {
            try { File.Delete(tempIndex); } catch { /* best effort */ }
        }
    }

    /// <summary>Short, stable (process-independent) hash for names.</summary>
    internal static string StableHash(string value)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in value) h = (h ^ c) * 16777619;
            return (h & 0xFFFFFF).ToString("x6");
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

    private Task<GitOutput> GitAsync(string workingDirectory, CancellationToken ct, params string[] args) =>
        GitAsync(workingDirectory, ct, null, args);

    private async Task<GitOutput> GitAsync(string workingDirectory, CancellationToken ct, IReadOnlyDictionary<string, string>? env, params string[] args)
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
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
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
