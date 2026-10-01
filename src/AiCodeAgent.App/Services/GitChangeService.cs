using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiCodeAgent.App.Services;

/// <summary>What git says happened to a file relative to HEAD.</summary>
public enum GitChangeKind
{
    None = 0,
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted
}

public static class GitChangeKindExtensions
{
    /// <summary>One-letter badge shown next to a file in the Explorer.</summary>
    public static string Badge(this GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "U",
        GitChangeKind.Conflicted => "!",
        _ => string.Empty
    };

    public static string Color(this GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => "#E5A100",
        GitChangeKind.Added => "#4CAF50",
        GitChangeKind.Untracked => "#4CAF50",
        GitChangeKind.Deleted => "#E05555",
        GitChangeKind.Renamed => "#4A9EFF",
        GitChangeKind.Conflicted => "#E05555",
        _ => "#9AA0A6"
    };

    public static string Describe(this GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => "Modified",
        GitChangeKind.Added => "Added",
        GitChangeKind.Deleted => "Deleted",
        GitChangeKind.Renamed => "Renamed",
        GitChangeKind.Untracked => "New (untracked)",
        GitChangeKind.Conflicted => "Merge conflict",
        _ => string.Empty
    };
}

/// <summary>One changed file in a git working tree.</summary>
public sealed record GitFileChange(
    string RepoRoot,
    string RelativePath,
    string FullPath,
    GitChangeKind Kind,
    string? OriginalRelativePath,
    bool IsStaged,
    GitChangeKind IndexKind = GitChangeKind.None,
    GitChangeKind WorkTreeKind = GitChangeKind.None)
{
    /// <summary>True when part of this change is staged for the next commit.</summary>
    public bool HasStagedPart => IndexKind is not (GitChangeKind.None or GitChangeKind.Untracked or GitChangeKind.Conflicted);

    /// <summary>True when part of this change is not staged (edited after staging, new, or conflicted).</summary>
    public bool HasUnstagedPart => WorkTreeKind != GitChangeKind.None;

    public string Badge => Kind.Badge();
    public string Color => Kind.Color();
    public string Description => Kind.Describe();
}

/// <summary>Outcome of a git write operation: success flag plus git's output (or the new short commit hash for a commit).</summary>
public sealed record GitOpResult(bool Success, string Output);

/// <summary>Result of reading a repository's status.</summary>
public sealed record GitRepoStatus(string RepoRoot, string Branch, IReadOnlyList<GitFileChange> Changes);

/// <summary>
/// Reads git state for the Explorer: which files changed, and what they looked like at HEAD
/// (the baseline for the diff viewer). Read-only — it never stages, commits or touches the index
/// (<c>--no-optional-locks</c>), so polling can't fight with the user's own git commands.
/// </summary>
public class GitChangeService
{
    public const int MaxChanges = 2000;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Root of the git repository containing <paramref name="directory"/>, or null if it isn't inside one.</summary>
    public virtual async Task<string?> FindRepoRootAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var (code, stdout, _) = await RunGitAsync(directory, new[] { "rev-parse", "--show-toplevel" }, cancellationToken).ConfigureAwait(false);
        var root = stdout.Trim();
        return code == 0 && root.Length > 0 ? Path.GetFullPath(root) : null;
    }

    /// <summary>Changes under <paramref name="directory"/> (a repo or a folder inside one); null when it isn't a git repository.</summary>
    public virtual async Task<GitRepoStatus?> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
    {
        var root = await FindRepoRootAsync(directory, cancellationToken).ConfigureAwait(false);
        if (root == null)
            return null;

        // "-- ." limits the report to this folder; paths in porcelain output stay relative to the repo root.
        var (code, stdout, _) = await RunGitAsync(directory,
            new[] { "status", "--porcelain=v1", "-b", "-z", "--untracked-files=all", "--", "." },
            cancellationToken).ConfigureAwait(false);
        return code == 0 ? ParsePorcelain(root, stdout) : null;
    }

    /// <summary>File content at HEAD, or null when the file doesn't exist there (new file) or is unreadable.</summary>
    public virtual async Task<string?> GetHeadContentAsync(string repoRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        var gitPath = relativePath.Replace('\\', '/');
        var (code, stdout, _) = await RunGitAsync(repoRoot, new[] { "show", $"HEAD:{gitPath}" }, cancellationToken).ConfigureAwait(false);
        return code == 0 ? stdout : null;
    }

    /// <summary>File content as currently staged in the index (stage 0), or null when it isn't staged/tracked.</summary>
    public virtual async Task<string?> GetIndexContentAsync(string repoRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        var gitPath = relativePath.Replace('\\', '/');
        var (code, stdout, _) = await RunGitAsync(repoRoot, new[] { "show", $":{gitPath}" }, cancellationToken).ConfigureAwait(false);
        return code == 0 ? stdout : null;
    }

    // ---- write operations (only ever run in response to an explicit user action) ----

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(2); // commit hooks can be slow

    /// <summary>Stage the given files (new, modified or deleted).</summary>
    public virtual Task<GitOpResult> StageAsync(string repoRoot, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
        => WriteAsync(repoRoot, new[] { "add", "--" }.Concat(relativePaths.Select(ToGitPath)), literalPaths: true, cancellationToken);

    /// <summary>Stage every change in the repository, including new files.</summary>
    public virtual Task<GitOpResult> StageAllAsync(string repoRoot, CancellationToken cancellationToken = default)
        => WriteAsync(repoRoot, new[] { "add", "-A" }, literalPaths: false, cancellationToken);

    /// <summary>Remove the given files from the staging area, keeping their working-tree content.</summary>
    public virtual async Task<GitOpResult> UnstageAsync(string repoRoot, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
    {
        var paths = relativePaths.Select(ToGitPath).ToList();
        if (await HasHeadAsync(repoRoot, cancellationToken).ConfigureAwait(false))
            return await WriteAsync(repoRoot, new[] { "restore", "--staged", "--" }.Concat(paths), literalPaths: true, cancellationToken).ConfigureAwait(false);

        // A repository with no commits yet has no HEAD to restore from.
        return await WriteAsync(repoRoot, new[] { "rm", "--cached", "-r", "-q", "--ignore-unmatch", "--" }.Concat(paths), literalPaths: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unstage everything.</summary>
    public virtual async Task<GitOpResult> UnstageAllAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        if (await HasHeadAsync(repoRoot, cancellationToken).ConfigureAwait(false))
            return await WriteAsync(repoRoot, new[] { "reset", "-q" }, literalPaths: false, cancellationToken).ConfigureAwait(false);

        return await WriteAsync(repoRoot, new[] { "rm", "--cached", "-r", "-q", "--ignore-unmatch", "--", "." }, literalPaths: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throw away working-tree changes to the given files: tracked files are restored from the index,
    /// untracked files are deleted. This cannot be undone.
    /// </summary>
    public virtual Task<GitOpResult> DiscardAsync(string repoRoot, IEnumerable<string> relativePaths, bool untracked, CancellationToken cancellationToken = default)
    {
        var paths = relativePaths.Select(ToGitPath);
        var args = untracked
            ? new[] { "clean", "-f", "-q", "--" }.Concat(paths)
            : new[] { "restore", "--" }.Concat(paths);
        return WriteAsync(repoRoot, args, literalPaths: true, cancellationToken);
    }

    /// <summary>Commit what is staged. On success <see cref="GitOpResult.Output"/> is the new short commit hash.</summary>
    public virtual async Task<GitOpResult> CommitAsync(string repoRoot, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitOpResult(false, "Enter a commit message.");

        var commit = await WriteAsync(repoRoot, new[] { "commit", "-m", message }, literalPaths: false, cancellationToken).ConfigureAwait(false);
        if (!commit.Success)
            return commit;

        var (code, hash, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--short", "HEAD" }, cancellationToken).ConfigureAwait(false);
        return new GitOpResult(true, code == 0 ? hash.Trim() : string.Empty);
    }

    public virtual async Task<bool> HasHeadAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--verify", "-q", "HEAD" }, cancellationToken).ConfigureAwait(false);
        return code == 0;
    }

    private static string ToGitPath(string path) => path.Replace('\\', '/');

    private static async Task<GitOpResult> WriteAsync(string repoRoot, IEnumerable<string> args, bool literalPaths, CancellationToken cancellationToken)
    {
        var (code, stdout, stderr) = await RunGitAsync(repoRoot, args, cancellationToken, WriteTimeout, literalPaths).ConfigureAwait(false);
        if (code == 0)
            return new GitOpResult(true, stdout.Trim());

        // git reports most problems on stderr, but e.g. "nothing to commit" arrives on stdout.
        var text = string.Join(Environment.NewLine, new[] { stderr.Trim(), stdout.Trim() }.Where(t => t.Length > 0));
        return new GitOpResult(false, text.Length > 0 ? text : "git failed");
    }

    /// <summary>Parses <c>git status --porcelain=v1 -b -z</c> output. Exposed for testing.</summary>
    public static GitRepoStatus ParsePorcelain(string repoRoot, string output)
    {
        var branch = "HEAD";
        var changes = new List<GitFileChange>();
        var entries = output.Split('\0');

        for (var i = 0; i < entries.Length && changes.Count < MaxChanges; i++)
        {
            var entry = entries[i];
            if (entry.Length == 0)
                continue;

            if (entry.StartsWith("## ", StringComparison.Ordinal))
            {
                branch = ParseBranch(entry[3..]);
                continue;
            }

            if (entry.Length < 4)
                continue;

            char x = entry[0], y = entry[1];
            var path = entry[3..];

            // Renames/copies are followed by a second NUL-separated entry holding the original path.
            string? original = null;
            if (x is 'R' or 'C' || y is 'R' or 'C')
                original = ++i < entries.Length ? entries[i] : null;

            var kind = Classify(x, y);
            // Conflicts live entirely in the "needs attention" list; everything else keeps its two columns.
            var indexKind = kind == GitChangeKind.Conflicted ? GitChangeKind.None : KindOf(x);
            var workTreeKind = kind == GitChangeKind.Conflicted ? GitChangeKind.Conflicted : KindOf(y);
            if (x == '?') { indexKind = GitChangeKind.None; workTreeKind = GitChangeKind.Untracked; }

            changes.Add(new GitFileChange(
                repoRoot,
                path,
                Path.GetFullPath(Path.Combine(repoRoot, path)),
                kind,
                original,
                IsStaged: x is not (' ' or '?'),
                IndexKind: indexKind,
                WorkTreeKind: workTreeKind));
        }

        return new GitRepoStatus(repoRoot, branch, changes);
    }

    private static GitChangeKind KindOf(char c) => c switch
    {
        'M' or 'T' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' or 'C' => GitChangeKind.Renamed,
        'U' => GitChangeKind.Conflicted,
        '?' => GitChangeKind.Untracked,
        _ => GitChangeKind.None
    };

    private static GitChangeKind Classify(char x, char y)
    {
        if (x == '?' && y == '?') return GitChangeKind.Untracked;
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return GitChangeKind.Conflicted;
        if (x == 'D' || y == 'D') return GitChangeKind.Deleted;
        if (x is 'R' or 'C' || y is 'R' or 'C') return GitChangeKind.Renamed;
        if (x == 'A' || y == 'A') return GitChangeKind.Added;
        return GitChangeKind.Modified;
    }

    private static string ParseBranch(string header)
    {
        const string noCommits = "No commits yet on ";
        if (header.StartsWith(noCommits, StringComparison.Ordinal))
            return header[noCommits.Length..];

        var end = header.IndexOf("...", StringComparison.Ordinal);
        if (end < 0) end = header.IndexOf(" [", StringComparison.Ordinal);
        return (end >= 0 ? header[..end] : header).Trim();
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory, IEnumerable<string> args, CancellationToken cancellationToken,
        TimeSpan? timeoutAfter = null, bool literalPaths = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter ?? GitTimeout);

        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // Never take the index lock: status must not interfere with the user's own git commands.
            psi.ArgumentList.Add("--no-optional-locks");
            // File names are paths, not patterns: names containing [ ] * ? must match literally.
            if (literalPaths) psi.ArgumentList.Add("--literal-pathspecs");
            // Keep quoting off so paths with spaces/unicode come through untouched.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotepath=false");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, string.Empty, "git could not be started");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                if (cancellationToken.IsCancellationRequested) throw;
                return (-1, string.Empty, "git timed out");
            }

            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // git not installed, directory vanished, etc. — treat as "not a repository".
            return (-1, string.Empty, ex.Message);
        }
    }
}
