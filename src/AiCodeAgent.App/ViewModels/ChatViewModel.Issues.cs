using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiCodeAgent.Core.GitHosting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>E6-S4(a): <c>/issue</c> lists open issues of the repository and starts a task from one.</summary>
public partial class ChatViewModel
{
    private readonly IGitHostProvider? _gitHost;

    /// <summary>The issue the current task was started from; the PR slice uses it for the branch name and <c>Closes #n</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedIssue))]
    [NotifyPropertyChangedFor(nameof(LinkedIssueText))]
    private IssueLink? _linkedIssue;

    public bool HasLinkedIssue => LinkedIssue != null;
    public string LinkedIssueText => LinkedIssue == null ? string.Empty : $"Issue #{LinkedIssue.Number}: {LinkedIssue.Title}";

    /// <summary>Parsed form of <c>/issue [number] [label:x] [assignee:y]</c>.</summary>
    internal sealed record IssueCommand(int? Number, string? Label, string? Assignee, bool Clear);

    internal static IssueCommand ParseIssueCommand(string arguments)
    {
        int? number = null;
        string? label = null, assignee = null;
        var clear = false;
        foreach (var raw in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.StartsWith("label:", StringComparison.OrdinalIgnoreCase)) label = token["label:".Length..];
            else if (token.StartsWith("assignee:", StringComparison.OrdinalIgnoreCase)) assignee = token["assignee:".Length..];
            else if (token.Equals("clear", StringComparison.OrdinalIgnoreCase) || token.Equals("none", StringComparison.OrdinalIgnoreCase)) clear = true;
            else if (int.TryParse(token.TrimStart('#'), out var n) && n > 0) number = n;
        }
        return new IssueCommand(number, label, assignee, clear);
    }

    /// <summary>Handles <c>/issue</c> and <c>/issues</c>. Returns true when handled.</summary>
    internal async Task<bool> TryHandleIssueCommandAsync(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        var command = parts[0].ToLowerInvariant();
        if (command != "/issue" && command != "/issues") return false;

        Timeline.Add(new ChatMessage { Role = "User", Content = text, Timestamp = DateTime.Now });
        var args = ParseIssueCommand(parts.Length > 1 ? parts[1] : string.Empty);

        if (args.Clear)
        {
            LinkedIssue = null;
            Note("The task is no longer linked to an issue.");
            return true;
        }
        if (_gitHost == null || _gitService == null)
        {
            Note("Issues are not available here: open a folder with a git repository on GitHub.");
            return true;
        }

        IsProcessing = true;
        try
        {
            var remote = await _gitService.GetRemoteUrlAsync().ConfigureAwait(true);
            var repo = remote == null ? null : _gitHost.ParseRemote(remote);
            if (repo == null)
            {
                Note(remote == null
                    ? "This folder has no `origin` remote, so there is no repository to read issues from."
                    : $"The `origin` remote ({remote}) is not a {_gitHost.DisplayName} repository.");
                return true;
            }

            if (args.Number is { } number)
            {
                StatusText = $"Reading issue #{number}…";
                var issue = await _gitHost.GetIssueAsync(repo, number).ConfigureAwait(true);
                LinkedIssue = IssueTaskBuilder.LinkOf(_gitHost.Id, repo, issue);
                InputText = IssueTaskBuilder.Build(issue);
                Note($"Task filled from {repo.FullName}#{issue.Number} ({issue.Comments.Count} comment(s)). Edit it if you like, then send. Use `/issue clear` to unlink.");
            }
            else
            {
                StatusText = "Reading open issues…";
                var issues = await _gitHost.ListIssuesAsync(repo, new IssueFilter(args.Label, args.Assignee)).ConfigureAwait(true);
                Note(FormatIssueList(repo, issues, args));
            }
        }
        catch (GitHostException ex)
        {
            Note("⚠️ " + ex.Message);
        }
        finally
        {
            IsProcessing = false;
            StatusText = string.Empty;
        }
        return true;
    }

    internal static string FormatIssueList(HostRepo repo, IReadOnlyList<HostIssue> issues, IssueCommand filter)
    {
        var where = new List<string>();
        if (filter.Label != null) where.Add($"label {filter.Label}");
        if (filter.Assignee != null) where.Add($"assigned to {filter.Assignee}");
        var suffix = where.Count > 0 ? " (" + string.Join(", ", where) + ")" : string.Empty;
        if (issues.Count == 0) return $"No open issues in {repo.FullName}{suffix}.";

        var sb = new StringBuilder();
        sb.AppendLine($"Open issues in **{repo.FullName}**{suffix}. Pick one with `/issue <number>`:");
        foreach (var i in issues)
        {
            sb.Append("- `#").Append(i.Number).Append("` ").Append(i.Title);
            if (i.Labels.Count > 0) sb.Append("  [").Append(string.Join(", ", i.Labels)).Append(']');
            if (i.Assignee != null) sb.Append("  → @").Append(i.Assignee);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
