using AiCodeAgent.Core.Context;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Edits a character file's <c>tools</c> entry: allow or stop allowing a tool or pattern such as <c>mcp:github/*</c>
/// (<c>tools: { add: [mcp:github/*] }</c>). Everything else in the file stays as it was.
/// </summary>
public static class CharacterToolEdit
{
    /// <summary>True when <paramref name="pattern"/> is in the character's <c>tools.add</c> (or in its tool list).</summary>
    public static bool IsAllowed(string content, string pattern)
    {
        var (add, _, whitelist) = Read(Frontmatter.Parse(content));
        return (whitelist ?? add).Contains(pattern, StringComparer.OrdinalIgnoreCase);
    }

    public static string SetAllowed(string content, string pattern, bool allowed)
    {
        var doc = Frontmatter.Parse(content);
        var (add, remove, whitelist) = Read(doc);

        if (whitelist != null)
        {
            whitelist.RemoveAll(t => string.Equals(t, pattern, StringComparison.OrdinalIgnoreCase));
            if (allowed) whitelist.Add(pattern);
            return Frontmatter.SetField(content, "tools", Frontmatter.FormatList(whitelist));
        }

        add.RemoveAll(t => string.Equals(t, pattern, StringComparison.OrdinalIgnoreCase));
        remove.RemoveAll(t => string.Equals(t, pattern, StringComparison.OrdinalIgnoreCase));
        if (allowed) add.Add(pattern);

        if (add.Count == 0 && remove.Count == 0) return Frontmatter.RemoveField(content, "tools");
        var parts = new List<string>();
        if (add.Count > 0) parts.Add("add: " + Frontmatter.FormatList(add));
        if (remove.Count > 0) parts.Add("remove: " + Frontmatter.FormatList(remove));
        return Frontmatter.SetField(content, "tools", "{ " + string.Join(", ", parts) + " }");
    }

    private static (List<string> Add, List<string> Remove, List<string>? Whitelist) Read(FrontmatterDocument doc)
    {
        if (!doc.Fields.TryGetValue("tools", out var value) || value == null) return (new(), new(), null);
        if (value is List<string> list) return (new(), new(), list.ToList());
        if (value is Dictionary<string, object?> map)
            return (ListOf(map, "add"), ListOf(map, "remove"), null);
        if (value is string s && !string.IsNullOrWhiteSpace(s))
            return (new(), new(), s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
        return (new(), new(), null);
    }

    private static List<string> ListOf(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) ? v switch
        {
            List<string> l => l.ToList(),
            string s when !string.IsNullOrWhiteSpace(s) => new List<string> { s },
            _ => new List<string>()
        } : new List<string>();
}
