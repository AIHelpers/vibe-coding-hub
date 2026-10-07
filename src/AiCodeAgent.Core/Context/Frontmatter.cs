using System.Globalization;
using System.Text;

namespace AiCodeAgent.Core.Context;

/// <summary>
/// A parsed Markdown document with an optional YAML-style frontmatter block
/// delimited by <c>---</c> lines. Used by skills (<c>SKILL.md</c>) and
/// characters (<c>&lt;id&gt;.md</c>).
/// </summary>
public sealed class FrontmatterDocument
{
    /// <summary>Parsed fields in file order. Values are <see cref="string"/>, <see cref="List{T}"/> of string, or a nested dictionary.</summary>
    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw frontmatter text (without the <c>---</c> delimiters), or null when the file has none.</summary>
    public string? RawFrontmatter { get; init; }

    /// <summary>Everything after the closing delimiter (or the whole file when there is no frontmatter).</summary>
    public string Body { get; init; } = string.Empty;

    public bool HasFrontmatter => RawFrontmatter != null;

    /// <summary>Scalar value of <paramref name="key"/>, or null when missing or not a scalar.</summary>
    public string? GetString(string key) =>
        Fields.TryGetValue(key, out var v) && v is string s ? s : null;

    /// <summary>True/false for <c>true|false|yes|no|on|off</c>; <paramref name="defaultValue"/> otherwise.</summary>
    public bool GetBool(string key, bool defaultValue = false)
    {
        var s = GetString(key);
        if (s == null) return defaultValue;
        return s.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" => true,
            "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }

    public int? GetInt(string key) =>
        int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>List value of <paramref name="key"/>. A scalar is treated as a one-item list (or a comma-separated list).</summary>
    public List<string> GetList(string key)
    {
        if (!Fields.TryGetValue(key, out var v) || v == null) return new List<string>();
        return v switch
        {
            List<string> l => l.ToList(),
            string s when string.IsNullOrWhiteSpace(s) => new List<string>(),
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            _ => new List<string>()
        };
    }

    /// <summary>Nested map value of <paramref name="key"/> (e.g. <c>tools: { add: [a], remove: [] }</c>).</summary>
    public IReadOnlyDictionary<string, object?> GetMap(string key) =>
        Fields.TryGetValue(key, out var v) && v is Dictionary<string, object?> m
            ? m
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Small, dependency-free parser/editor for the YAML subset used in skill and
/// character frontmatter: scalars, quoted strings, inline lists <c>[a, b]</c>,
/// block lists (<c>- a</c>), inline maps <c>{ k: v }</c>, one level of nested
/// block maps, and <c># comments</c>. It is deliberately lenient: anything it
/// cannot understand is kept as a raw string instead of failing the file.
/// </summary>
public static class Frontmatter
{
    /// <summary>Split a markdown file into (frontmatter, body). Frontmatter is delimited by <c>---</c> lines.</summary>
    public static (string? frontmatter, string body) Split(string content)
    {
        if (string.IsNullOrEmpty(content)) return (null, content ?? string.Empty);
        var text = content.TrimStart('﻿');
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return (null, content);
        var rest = trimmed.Substring(3);
        // The opening delimiter must be alone on its line.
        var firstNl = rest.IndexOf('\n');
        if (firstNl < 0 || rest[..firstNl].Trim().Length > 0) return (null, content);
        var end = IndexOfClosingDelimiter(rest);
        if (end < 0) return (null, content);
        var frontmatter = rest.Substring(0, end).Trim('\r', '\n');
        var afterDelim = rest.IndexOf('\n', end + 1);
        var body = afterDelim < 0 ? string.Empty : rest.Substring(afterDelim + 1);
        return (frontmatter, body.TrimStart('\r', '\n'));
    }

    /// <summary>Index (in <paramref name="rest"/>) of the newline that starts the closing <c>---</c> line, or -1.</summary>
    private static int IndexOfClosingDelimiter(string rest)
    {
        var idx = 0;
        while (true)
        {
            var nl = rest.IndexOf('\n', idx);
            if (nl < 0) return -1;
            var lineEnd = rest.IndexOf('\n', nl + 1);
            var line = lineEnd < 0 ? rest[(nl + 1)..] : rest[(nl + 1)..lineEnd];
            if (line.TrimEnd('\r', ' ', '\t') == "---") return nl;
            idx = nl + 1;
        }
    }

    /// <summary>Parse a full markdown document.</summary>
    public static FrontmatterDocument Parse(string content)
    {
        var (fm, body) = Split(content);
        return new FrontmatterDocument
        {
            RawFrontmatter = fm,
            Body = body,
            Fields = fm == null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) : ParseFields(fm)
        };
    }

    /// <summary>Parse the frontmatter text (no delimiters) into fields.</summary>
    public static Dictionary<string, object?> ParseFields(string frontmatter)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var lines = frontmatter.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsBlankOrComment(line) || Indent(line) > 0) { i++; continue; }
            var colon = FindKeyColon(line);
            if (colon < 0) { i++; continue; }
            var key = line[..colon].Trim().Trim('"', '\'');
            var rawValue = StripComment(line[(colon + 1)..]).Trim();
            i++;
            if (rawValue.Length > 0)
            {
                result[key] = ParseInlineValue(rawValue);
                continue;
            }

            // Block value: collect indented continuation lines.
            var block = new List<string>();
            while (i < lines.Length && (IsBlankOrComment(lines[i]) || Indent(lines[i]) > 0 || lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal)))
            {
                if (Indent(lines[i]) == 0 && !lines[i].TrimStart().StartsWith("-", StringComparison.Ordinal) && !IsBlankOrComment(lines[i]))
                    break;
                block.Add(lines[i]);
                i++;
            }
            result[key] = ParseBlock(block);
        }
        return result;
    }

    private static object? ParseBlock(List<string> block)
    {
        var meaningful = block.Where(l => !IsBlankOrComment(l)).ToList();
        if (meaningful.Count == 0) return string.Empty;
        if (meaningful.All(l => l.TrimStart().StartsWith("-", StringComparison.Ordinal)))
        {
            return meaningful
                .Select(l => Unquote(StripComment(l.TrimStart()[1..]).Trim()))
                .Where(s => s.Length > 0)
                .ToList();
        }
        // Nested map (one level), e.g. "  add: [a]".
        var baseIndent = meaningful.Min(Indent);
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < meaningful.Count; j++)
        {
            var l = meaningful[j];
            if (Indent(l) != baseIndent) continue;
            var c = FindKeyColon(l);
            if (c < 0) continue;
            var k = l[..c].Trim().Trim('"', '\'');
            var v = StripComment(l[(c + 1)..]).Trim();
            if (v.Length > 0) { map[k] = ParseInlineValue(v); continue; }
            var sub = new List<string>();
            while (j + 1 < meaningful.Count && Indent(meaningful[j + 1]) > baseIndent)
                sub.Add(meaningful[++j]);
            map[k] = ParseBlock(sub);
        }
        return map;
    }

    private static object? ParseInlineValue(string raw)
    {
        if (raw.StartsWith('[') && raw.EndsWith(']'))
            return SplitTopLevel(raw[1..^1], ',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToList();
        if (raw.StartsWith('{') && raw.EndsWith('}'))
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in SplitTopLevel(raw[1..^1], ','))
            {
                var c = FindKeyColon(part);
                if (c < 0) continue;
                var k = part[..c].Trim().Trim('"', '\'');
                var v = part[(c + 1)..].Trim();
                map[k] = v.Length == 0 ? string.Empty : ParseInlineValue(v);
            }
            return map;
        }
        return Unquote(raw);
    }

    /// <summary>Split on <paramref name="sep"/> while ignoring separators inside brackets/braces/quotes.</summary>
    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (quote != null) { if (ch == quote) quote = null; continue; }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch is '[' or '{') depth++;
            else if (ch is ']' or '}') depth--;
            else if (ch == sep && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        if (start <= s.Length && s[start..].Trim().Length > 0) parts.Add(s[start..]);
        return parts;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            var inner = s[1..^1];
            return s[0] == '"' ? inner.Replace("\\\"", "\"").Replace("\\\\", "\\") : inner.Replace("''", "'");
        }
        return s;
    }

    /// <summary>Remove a trailing <c># comment</c> that is outside quotes and preceded by whitespace.</summary>
    private static string StripComment(string s)
    {
        char? quote = null;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (quote != null) { if (ch == quote) quote = null; continue; }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1]))) return s[..i];
        }
        return s;
    }

    /// <summary>Index of the colon that separates key from value (first colon followed by space/end, outside quotes).</summary>
    private static int FindKeyColon(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != null) { if (ch == quote) quote = null; continue; }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == ':' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1]))) return i;
        }
        return -1;
    }

    private static bool IsBlankOrComment(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || t.StartsWith('#');
    }

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }

    // ===================== Writing =====================

    /// <summary>Format a list as an inline YAML list: <c>[a, b]</c>.</summary>
    public static string FormatList(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(QuoteIfNeeded)) + "]";

    /// <summary>Quote a scalar when it contains characters that would change its YAML meaning.</summary>
    public static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0) return "\"\"";
        var needs = value.IndexOfAny(new[] { ':', '#', '[', ']', '{', '}', ',', '"', '\'' }) >= 0
                    || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])
                    || value.StartsWith('-') || value.StartsWith('*') || value.StartsWith('&') || value.StartsWith('!');
        return needs ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;
    }

    /// <summary>Build a complete document from ordered fields and a body.</summary>
    public static string Compose(IEnumerable<KeyValuePair<string, string>> fieldsYaml, string body)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (var (k, v) in fieldsYaml)
            sb.Append(k).Append(": ").Append(v).Append('\n');
        sb.Append("---\n");
        sb.Append(body.TrimStart('\r', '\n'));
        if (!body.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Set (or add) a top-level frontmatter field, replacing the key line and any
    /// indented/list continuation lines that belong to it. Every other line —
    /// comments, other keys, the body — is preserved byte-for-byte. Adds a
    /// frontmatter block when the document has none.
    /// </summary>
    public static string SetField(string content, string key, string valueYaml)
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var (fm, body) = Split(content);
        if (fm == null)
            return "---" + newline + key + ": " + valueYaml + newline + "---" + newline + content;

        var lines = fm.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => Indent(l) == 0 && FindKeyColon(l) > 0 &&
                                         string.Equals(l[..FindKeyColon(l)].Trim().Trim('"', '\''), key, StringComparison.OrdinalIgnoreCase));
        var newLine = key + ": " + valueYaml;
        if (start < 0)
        {
            lines.Add(newLine);
        }
        else
        {
            var end = start + 1;
            while (end < lines.Count && !IsBlankOrComment(lines[end]) &&
                   (Indent(lines[end]) > 0 || lines[end].TrimStart().StartsWith("- ", StringComparison.Ordinal)))
                end++;
            lines.RemoveRange(start, end - start);
            lines.Insert(start, newLine);
        }
        return "---" + newline + string.Join(newline, lines) + newline + "---" + newline + body;
    }

    /// <summary>Remove a top-level frontmatter field (and its continuation lines); every other line is preserved.</summary>
    public static string RemoveField(string content, string key)
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var (fm, body) = Split(content);
        if (fm == null) return content;
        var lines = fm.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => Indent(l) == 0 && FindKeyColon(l) > 0 &&
                                         string.Equals(l[..FindKeyColon(l)].Trim().Trim('"', '\''), key, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return content;
        var end = start + 1;
        while (end < lines.Count && !IsBlankOrComment(lines[end]) &&
               (Indent(lines[end]) > 0 || lines[end].TrimStart().StartsWith("- ", StringComparison.Ordinal)))
            end++;
        lines.RemoveRange(start, end - start);
        return "---" + newline + string.Join(newline, lines) + newline + "---" + newline + body;
    }

    /// <summary>Replace the body of a document, keeping its frontmatter as-is.</summary>
    public static string SetBody(string content, string body)
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var (fm, _) = Split(content);
        if (fm == null) return body;
        return "---" + newline + fm + newline + "---" + newline + body;
    }
}
