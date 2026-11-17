using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Board;

namespace AiCodeAgent.App.Services;

public enum CardFileKind { Added, Modified, Deleted, Renamed }

/// <summary>One file a card's agent changed, relative to where the card's branch left the project.</summary>
public sealed record CardFileChange(string Path, CardFileKind Kind, string? OldPath = null)
{
    public string Badge => Kind switch { CardFileKind.Added => "A", CardFileKind.Deleted => "D", CardFileKind.Renamed => "R", _ => "M" };
}

/// <summary>The files a card changed, or why they cannot be listed.</summary>
public sealed record CardChangeSet(IReadOnlyList<CardFileChange> Files, string? Problem, string BaseRevision = "")
{
    public static CardChangeSet Failed(string problem) => new(Array.Empty<CardFileChange>(), problem);
}

/// <summary>Both sides of one changed file, ready for the diff viewer.</summary>
public sealed record CardFileDiff(string Baseline, string Current, string BaselineLabel, string CurrentLabel);

/// <summary>
/// Shows what a board card changed (E11-S3): the files, and the two versions of each. While the card's worktree exists the
/// current side is read from it, so a running card shows its work as it goes, including files it has not committed yet; after
/// the worktree is gone the card's branch is read instead. The baseline is the commit the branch left the project at.
/// </summary>
public class BoardChangeReader
{
    public const int MaxFiles = 500;
    private const int MaxChars = 1_000_000;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    public virtual async Task<CardChangeSet> ListAsync(string projectDirectory, BoardCard card, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(card.Branch)) return CardChangeSet.Failed("This card has no branch yet: it changed nothing or has not run.");
        var live = HasWorktree(card);
        var baseRevision = await BaseOfAsync(projectDirectory, card.Branch, cancellationToken).ConfigureAwait(false);
        if (baseRevision == null) return CardChangeSet.Failed($"Branch '{card.Branch}' was not found in this repository. It may have been deleted.");

        var (code, output, error) = live
            ? await GitAsync(card.WorktreePath!, new[] { "diff", "--name-status", "-M", baseRevision }, cancellationToken).ConfigureAwait(false)
            : await GitAsync(projectDirectory, new[] { "diff", "--name-status", "-M", baseRevision, card.Branch }, cancellationToken).ConfigureAwait(false);
        if (code != 0) return CardChangeSet.Failed("git could not list the changes: " + error.Trim());

        var files = ParseNameStatus(output).ToList();
        if (live)
        {
            // New files the agent has not committed are not in `git diff` yet.
            var (_, untracked, _) = await GitAsync(card.WorktreePath!, new[] { "ls-files", "--others", "--exclude-standard" }, cancellationToken).ConfigureAwait(false);
            files.AddRange(untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => files.All(f => f.Path != p)).Select(p => new CardFileChange(p, CardFileKind.Added)));
        }
        return new CardChangeSet(files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList(), null, baseRevision);
    }

    public virtual async Task<CardFileDiff> ReadAsync(string projectDirectory, BoardCard card, CardChangeSet set, CardFileChange file, CancellationToken cancellationToken = default)
    {
        var live = HasWorktree(card);
        var baseline = file.Kind == CardFileKind.Added
            ? string.Empty
            : await ShowAsync(projectDirectory, $"{set.BaseRevision}:{file.OldPath ?? file.Path}", cancellationToken).ConfigureAwait(false);
        string current;
        if (file.Kind == CardFileKind.Deleted) current = string.Empty;
        else if (live) current = ReadFile(Path.Combine(card.WorktreePath!, file.Path));
        else current = await ShowAsync(projectDirectory, $"{card.Branch}:{file.Path}", cancellationToken).ConfigureAwait(false);
        return new CardFileDiff(baseline, current, "Before the card", live ? "In the worktree" : $"Branch {card.Branch}");
    }

    internal static IEnumerable<CardFileChange> ParseNameStatus(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            switch (parts[0][0])
            {
                case 'A': yield return new CardFileChange(parts[1], CardFileKind.Added); break;
                case 'D': yield return new CardFileChange(parts[1], CardFileKind.Deleted); break;
                case 'R' or 'C' when parts.Length >= 3: yield return new CardFileChange(parts[2], CardFileKind.Renamed, parts[1]); break;
                default: yield return new CardFileChange(parts[1], CardFileKind.Modified); break;
            }
        }
    }

    private static bool HasWorktree(BoardCard card) => !string.IsNullOrWhiteSpace(card.WorktreePath) && Directory.Exists(card.WorktreePath);

    private static async Task<string?> BaseOfAsync(string projectDirectory, string branch, CancellationToken token)
    {
        var (code, output, _) = await GitAsync(projectDirectory, new[] { "merge-base", "HEAD", branch }, token).ConfigureAwait(false);
        var revision = output.Trim();
        return code == 0 && revision.Length > 0 ? revision : null;
    }

    private static async Task<string> ShowAsync(string directory, string spec, CancellationToken token)
    {
        var (code, output, _) = await GitAsync(directory, new[] { "show", spec }, token).ConfigureAwait(false);
        return code == 0 ? Clean(output) : string.Empty;
    }

    private static string ReadFile(string path)
    {
        try { return File.Exists(path) ? Clean(File.ReadAllText(path)) : string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static string Clean(string text)
    {
        if (text.Contains('\0')) return "(binary file)";
        return text.Length > MaxChars ? text[..MaxChars] + "\n… (cut: file is larger than 1 MB)" : text;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> GitAsync(string directory, IEnumerable<string> args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(GitTimeout);
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("--no-optional-locks");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotepath=false");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(psi);
            if (process == null) return (-1, string.Empty, "git could not be started");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                if (token.IsCancellationRequested) throw;
                return (-1, string.Empty, "git timed out");
            }
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
