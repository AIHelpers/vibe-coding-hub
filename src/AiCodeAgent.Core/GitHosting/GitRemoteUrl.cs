using System;
using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.GitHosting;

/// <summary>Reads <c>owner/repo</c> from the usual git remote spellings.</summary>
public static partial class GitRemoteUrl
{
    // https://host/owner/repo(.git)  ssh://git@host/owner/repo(.git)  git@host:owner/repo(.git)
    [GeneratedRegex(@"^(?:(?:https?|ssh|git)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?/|(?:[^@/\s]+@)?(?<host2>[^/:\s]+):)(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteRegex();

    public static HostRepo? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var m = RemoteRegex().Match(remoteUrl.Trim());
        if (!m.Success) return null;
        var host = m.Groups["host"].Success ? m.Groups["host"].Value : m.Groups["host2"].Value;
        return new HostRepo(host.ToLowerInvariant(), m.Groups["owner"].Value, m.Groups["repo"].Value);
    }
}
