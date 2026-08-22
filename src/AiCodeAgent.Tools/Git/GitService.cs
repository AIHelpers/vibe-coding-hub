using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Git;

/// <summary>
/// High-level git workflow service (Feature 6: In-Chat Branch / PR Workflow).
/// Wraps the git CLI and returns structured results suitable for rendering
/// as rich cards in the chat UI. Remote operations (push, PR) are explicit
/// and require the caller to confirm before invoking.
/// </summary>
public class GitService
{
    private readonly string _workingDirectory;
    private readonly ILogger<GitService> _logger;

    public GitService(string workingDirectory, ILogger<GitService> logger)
    {
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Get the current repository status.</summary>
    public async Task<StatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, stdout, stderr) = await RunGitAsync("status --porcelain=v1 -b", cancellationToken);
        if (exitCode != 0)
            return new StatusResult
            {
                Success = false,
                Summary = "Failed to read git status",
                Output = stderr
            };

        var branch = "HEAD";
        var changed = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("##"))
            {
                var match = Regex.Match(trimmed, @"^##\s+(\S+)");
                if (match.Success)
                    branch = match.Groups[1].Value;
                continue;
            }
            if (trimmed.Length >= 3)
                changed.Add(trimmed.Substring(3).Trim());
        }

        return new StatusResult
        {
            Success = true,
            Summary = changed.Count == 0
                ? $"On branch {branch}. Working tree clean."
                : $"On branch {branch}. {changed.Count} changed file(s).",
            Output = stdout,
            Branch = branch,
            ChangedFiles = changed
        };
    }

    /// <summary>Create and checkout a new branch from the current HEAD.</summary>
    public async Task<BranchResult> CreateBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new BranchResult { Success = false, Summary = "Branch name is required" };

        var (exitCode, stdout, stderr) = await RunGitAsync($"checkout -b {Quote(branchName)}", cancellationToken);
        if (exitCode != 0)
            return new BranchResult
            {
                Success = false,
                Summary = $"Failed to create branch '{branchName}'",
                Output = stderr
            };

        return new BranchResult
        {
            Success = true,
            Summary = $"Created and switched to branch '{branchName}'",
            Output = stdout,
            BranchName = branchName
        };
    }

    /// <summary>Stage all changes and commit them with the provided message.</summary>
    public async Task<CommitResult> CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new CommitResult { Success = false, Summary = "Commit message is required" };

        // Stage all changes.
        var (addCode, _, addErr) = await RunGitAsync("add -A", cancellationToken);
        if (addCode != 0)
            return new CommitResult
            {
                Success = false,
                Summary = "Failed to stage changes",
                Output = addErr
            };

        // Commit.
        var (commitCode, commitOut, commitErr) = await RunGitAsync($"commit -m {Quote(message)}", cancellationToken);
        if (commitCode != 0)
        {
            // `git commit` exits non-zero when there is nothing to commit.
            return new CommitResult
            {
                Success = false,
                Summary = string.IsNullOrWhiteSpace(commitErr) ? "Nothing to commit" : commitErr.Trim(),
                Output = commitOut + commitErr
            };
        }

        // Resolve the new commit hash.
        var (hashCode, hashOut, _) = await RunGitAsync("rev-parse HEAD", cancellationToken);
        var hash = hashCode == 0 ? hashOut.Trim() : string.Empty;

        // Collect committed files.
        var (nameCode, nameOut, _) = await RunGitAsync("diff --name-only HEAD~1 HEAD", cancellationToken);
        var files = nameCode == 0
            ? nameOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
            : new List<string>();

        return new CommitResult
        {
            Success = true,
            Summary = $"Committed {files.Count} file(s): {Shorten(hash, 7)}",
            Output = commitOut,
            CommitHash = hash,
            Files = files
        };
    }

    /// <summary>Push the current branch to origin and open a pull request.</summary>
    /// <param name="title">PR title.</param>
    /// <param name="body">PR body (auto-generated diff summary).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>This performs remote operations (push + PR create) and should
    /// only be called after explicit user confirmation.</remarks>
    public async Task<PullRequestResult> PushAndCreatePullRequestAsync(
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new PullRequestResult { Success = false, Summary = "PR title is required" };

        // Resolve current branch.
        var (branchCode, branchOut, branchErr) = await RunGitAsync("rev-parse --abbrev-ref HEAD", cancellationToken);
        if (branchCode != 0)
            return new PullRequestResult
            {
                Success = false,
                Summary = "Failed to determine current branch",
                Output = branchErr
            };
        var branch = branchOut.Trim();

        // Push the branch to origin.
        var (pushCode, _, pushErr) = await RunGitAsync($"push -u origin {Quote(branch)}", cancellationToken);
        if (pushCode != 0)
            return new PullRequestResult
            {
                Success = false,
                Summary = $"Failed to push branch '{branch}'",
                Output = pushErr,
                BranchName = branch
            };

        // Try the GitHub CLI first.
        var (ghCode, ghOut, ghErr) = await RunProcessAsync(
            "gh",
            $"pr create --title {Quote(title)} --body {Quote(body)}",
            cancellationToken);
        if (ghCode == 0)
        {
            var url = ExtractUrl(ghOut);
            return new PullRequestResult
            {
                Success = true,
                Summary = $"Pushed '{branch}' and opened pull request",
                Output = ghOut,
                BranchName = branch,
                PullRequestUrl = url,
                PullRequestBody = body
            };
        }

        // Fallback: report the push success but note PR creation failed.
        return new PullRequestResult
        {
            Success = true,
            Summary = $"Pushed branch '{branch}'. PR creation failed: {(string.IsNullOrWhiteSpace(ghErr) ? "gh CLI unavailable" : ghErr.Trim())}. Use GitHub to open the PR manually.",
            Output = pushErr + Environment.NewLine + ghErr,
            BranchName = branch,
            PullRequestUrl = null,
            PullRequestBody = body
        };
    }

    /// <summary>Return the full diff of staged + unstaged changes for summarization.</summary>
    public async Task<string> GetDiffAsync(CancellationToken cancellationToken = default)
    {
        var (code, stdout, _) = await RunGitAsync("diff HEAD", cancellationToken);
        return code == 0 ? stdout : string.Empty;
    }

    // ---- helpers ---------------------------------------------------------

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string Shorten(string hash, int len) =>
        string.IsNullOrEmpty(hash) ? string.Empty : hash.Length <= len ? hash : hash.Substring(0, len);

    private static string? ExtractUrl(string output)
    {
        var match = Regex.Match(output, @"https?://\S+");
        return match.Success ? match.Value : null;
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(string args, CancellationToken cancellationToken)
        => await RunProcessAsync("git", args, cancellationToken);

    private async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read asynchronously to avoid deadlocks, then wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            _logger.LogDebug("git {Args} -> {Code}", arguments, process.ExitCode);
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run {File} {Args}", fileName, arguments);
            return (-1, string.Empty, ex.Message);
        }
    }
}