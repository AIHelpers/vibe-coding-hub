using System;
using System.Collections.Generic;

namespace AiCodeAgent.Core.GitHosting;

/// <summary>A repository on a git host (GitHub first; GitLab and Azure DevOps can follow).</summary>
public sealed record HostRepo(string Host, string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
}

public sealed record HostIssueComment(string Author, string Body, DateTimeOffset CreatedAt);

public sealed record HostIssue(
    int Number,
    string Title,
    string Body,
    string Author,
    IReadOnlyList<string> Labels,
    string? Assignee,
    string Url,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HostIssueComment> Comments)
{
    public static IReadOnlyList<string> NoLabels { get; } = Array.Empty<string>();
}

/// <summary>Which open issues to list. <see cref="Assignee"/> may be a login, <c>me</c>, <c>none</c> or <c>*</c>.</summary>
public sealed record IssueFilter(string? Label = null, string? Assignee = null, int Limit = 30);

/// <summary>Where a run came from: set when a task is started from an issue, used later to build the PR (<c>Closes #n</c>).</summary>
public sealed record IssueLink(string Provider, string Host, string Owner, string Repo, int Number, string Url, string Title);

public enum GitHostError { NotSignedIn, Unauthorized, NotFound, RateLimited, Network, Other }

public sealed class GitHostException : Exception
{
    public GitHostException(GitHostError kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
    public GitHostError Kind { get; }
}
