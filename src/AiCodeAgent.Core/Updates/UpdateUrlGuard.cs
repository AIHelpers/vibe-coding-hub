namespace AiCodeAgent.Core.Updates;

/// <summary>The update banner opens addresses that came from a web answer, so only plain https links to GitHub are allowed.</summary>
public static class UpdateUrlGuard
{
    public static bool IsSafe(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }
}
