using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Matches the entries of a tool allow-list or deny-list against tool names. An entry is an exact name (<c>read_file</c>), a glob
/// (<c>github__*</c>) or an MCP pattern: <c>mcp:github/*</c> is every tool of the server "github", <c>mcp:github/create_issue</c>
/// one tool, <c>mcp:github</c> and <c>mcp:*</c> a whole server and every MCP tool. MCP tools are named <c>server__tool</c>.
/// </summary>
public static class ToolPatterns
{
    public const string McpPrefix = "mcp:";
    public const string McpSeparator = "__";

    /// <summary>The pattern for one tool, or for all tools of a server when <paramref name="tool"/> is null or "*".</summary>
    public static string McpPattern(string server, string? tool = null) =>
        $"{McpPrefix}{server}/{(string.IsNullOrEmpty(tool) ? "*" : tool)}";

    /// <summary>The registry name of an MCP tool.</summary>
    public static string McpToolName(string server, string tool) => server + McpSeparator + tool;

    public static bool IsMcpPattern(string entry) => entry.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesAny(IEnumerable<string> entries, string toolName)
    {
        foreach (var entry in entries)
            if (IsMatch(entry, toolName)) return true;
        return false;
    }

    public static bool IsMatch(string entry, string toolName)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var name = Normalize(entry.Trim());
        if (name.IndexOfAny(new[] { '*', '?' }) < 0)
            return string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + Regex.Escape(name).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(toolName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Turns <c>mcp:github/*</c> into the glob <c>github__*</c>; other entries are returned as they are.</summary>
    private static string Normalize(string entry)
    {
        if (!IsMcpPattern(entry)) return entry;
        var rest = entry[McpPrefix.Length..];
        if (rest == "*" || rest.Length == 0) return "*" + McpSeparator + "*";
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest + McpSeparator + "*" : rest[..slash] + McpSeparator + (rest[(slash + 1)..] is { Length: > 0 } t ? t : "*");
    }
}
