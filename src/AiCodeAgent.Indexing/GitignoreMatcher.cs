namespace AiCodeAgent.Indexing;

/// <summary>
/// Lightweight .gitignore matcher used by the indexer to exclude ignored
/// paths during the full scan. Supports the common patterns: comments,
/// negation, trailing-slash directory rules, and simple globs.
/// </summary>
public sealed class GitignoreMatcher
{
    private sealed record Rule(string Pattern, bool IsNegated, bool IsDirectoryOnly, bool IsAnchored, string SourceDir);

    private readonly List<Rule> _rules = new();

    /// <summary>Load rules from a .gitignore file located at <paramref name="path"/>.</summary>
    public void LoadFromFile(string path)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var lines = File.ReadAllLines(path);
        ParseLines(lines, sourceDir);
    }

    /// <summary>Load rules from raw gitignore content with a source directory.</summary>
    public void Load(string content, string sourceDir)
    {
        ParseLines(content.Split('\n'), sourceDir);
    }

    /// <summary>Whether a relative path is ignored (respecting negation rules).</summary>
    public bool IsIgnored(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        if (string.IsNullOrEmpty(normalized))
            return false;

        var isDir = relativePath.EndsWith('/') || relativePath.EndsWith('\\');
        var ignored = false;

        // Rules are evaluated in order; later rules override earlier ones.
        foreach (var rule in _rules)
        {
            if (rule.IsDirectoryOnly && !isDir)
                continue;

            if (MatchesRule(rule, normalized))
            {
                ignored = !rule.IsNegated;
            }
        }

        return ignored;
    }

    /// <summary>Whether any rules are loaded.</summary>
    public bool HasRules => _rules.Count > 0;

    /// <summary>Whether a directory should be pruned entirely from traversal.</summary>
    public bool IsDirectoryIgnored(string relativeDirPath)
    {
        var normalized = NormalizeRelative(relativeDirPath);
        if (string.IsNullOrEmpty(normalized))
            return false;

        // A directory is ignored if any rule matches it (or a parent) as a directory.
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.IsNegated)
                continue;

            if (rule.IsDirectoryOnly && MatchesRule(rule, normalized))
            {
                ignored = true;
            }
        }
        return ignored;
    }

    private bool MatchesRule(Rule rule, string normalizedPath)
    {
        var pattern = rule.Pattern;

        // Patterns with a trailing slash match directories only (handle above).
        // Collapse leading slashes in the pattern.
        if (pattern.StartsWith('/'))
            pattern = pattern[1..];

        // Handle anchored patterns: "doc/frotz" matches only at repo root.
        if (rule.IsAnchored || pattern.Contains('/'))
        {
            return GlobMatch(pattern, normalizedPath);
        }

        // Unanchored patterns match at any level. Match against the full path
        // and against any trailing segment.
        foreach (var segment in GetPathSegments(normalizedPath))
        {
            if (GlobMatch(pattern, segment))
                return true;
        }
        return GlobMatch(pattern, normalizedPath);
    }

    private static bool GlobMatch(string pattern, string path)
    {
        // Simple glob translation: '*' matches anything except '/', '**' matches anything.
        var regex = GlobToRegex(pattern);
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex);
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        // "**" matches across directory separators.
                        sb.Append(".*");
                        i++;
                        // Collapse "/**/" to just ".*" for simplicity.
                        if (i + 1 < pattern.Length && (pattern[i + 1] == '/' || pattern[i + 1] == '\\'))
                            i++;
                    }
                    else
                    {
                        sb.Append("[^/\\\\]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/\\\\]");
                    break;
                case '.':
                case '(':
                case ')':
                case '+':
                case '|':
                case '^':
                case '$':
                case '@':
                case '%':
                    sb.Append('\\').Append(c);
                    break;
                case '/':
                case '\\':
                    sb.Append(@"[/\\]");
                    break;
                case '[':
                    // Pass character classes through (best-effort).
                    var close = pattern.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        sb.Append(pattern[i..(close + 1)]);
                        i = close;
                    }
                    else
                    {
                        sb.Append(@"\[");
                    }
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    private void ParseLines(IEnumerable<string> lines, string sourceDir)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var isNegated = line.StartsWith('!');
            if (isNegated)
                line = line[1..].Trim();

            var isDirectoryOnly = line.EndsWith('/');
            if (isDirectoryOnly)
                line = line.TrimEnd('/');

            var isAnchored = line.StartsWith('/');
            if (isAnchored)
                line = line[1..];

            if (line.Length == 0)
                continue;

            // Escape handling is intentionally minimal.
            _rules.Add(new Rule(line, isNegated, isDirectoryOnly, isAnchored, sourceDir));
        }
    }

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static IEnumerable<string> GetPathSegments(string normalizedPath)
    {
        var parts = normalizedPath.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            yield return string.Join('/', parts[i..]);
        }
    }
}