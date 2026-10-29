using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiCodeAgent.Core.GitHosting;

/// <summary>
/// A git host that can supply issues (and, in later slices, pull requests and review comments).
/// GitHub is the first implementation; GitLab and Azure DevOps implement the same interface.
/// </summary>
public interface IGitHostProvider
{
    /// <summary>Short id: <c>github</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Recognises a git remote URL (https or ssh) of this host.</summary>
    HostRepo? ParseRemote(string remoteUrl);

    /// <summary>Open issues (pull requests excluded), newest activity first.</summary>
    Task<IReadOnlyList<HostIssue>> ListIssuesAsync(HostRepo repo, IssueFilter filter, CancellationToken cancellationToken = default);

    /// <summary>One issue with its comments.</summary>
    Task<HostIssue> GetIssueAsync(HostRepo repo, int number, CancellationToken cancellationToken = default);
}
