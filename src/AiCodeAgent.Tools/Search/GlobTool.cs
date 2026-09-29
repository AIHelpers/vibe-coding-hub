using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Search;

/// <summary>Find files by glob pattern (e.g. <c>**/*.cs</c>, <c>src/**/Test*.ts</c>), newest first.</summary>
public class GlobTool : BaseTool
{
    private const int MaxResults = 200;

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "__pycache__", ".venv", "target", ".aiagent-worktrees"
    };

    public GlobTool(ILogger<GlobTool> logger) : base(logger) { }

    public override string Name => "glob";
    public override string Description =>
        "Find files by glob pattern (supports **, *, ?, {a,b}). A pattern without '/' matches file names at any depth. " +
        "Returns paths relative to the search root, most recently modified first. Skips .git, node_modules, bin, obj.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["pattern"] = new() { Type = "string", Description = "Glob pattern, e.g. '**/*.cs' or 'src/**/*.test.ts'" },
                ["path"] = new() { Type = "string", Description = "Directory to search in (default: working directory)" }
            },
            Required = ["pattern"]
        }
    };

    public override Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var pattern = GetArg<string>(call, "pattern");
        var path = GetArg<string>(call, "path", ".");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(Error("'pattern' is required."));

        string root;
        try { root = ResolvePath(path, context); }
        catch (UnauthorizedAccessException ex) { return Task.FromResult(Error(ex.Message)); }
        if (!Directory.Exists(root))
            return Task.FromResult(Error($"Directory not found: {path}"));

        var regex = GlobToRegex(pattern);
        var matches = new List<(string Rel, DateTime Modified)>();
        Walk(root, root, regex, matches, context.CancellationToken);

        if (matches.Count == 0)
            return Task.FromResult(Success($"No files match '{pattern}' under {path}."));

        var ordered = matches.OrderByDescending(m => m.Modified).Take(MaxResults).Select(m => m.Rel).ToList();
        var sb = new StringBuilder();
        foreach (var rel in ordered) sb.AppendLine(rel);
        if (matches.Count > MaxResults)
            sb.AppendLine($"... {matches.Count - MaxResults} more not shown. Narrow the pattern.");
        return Task.FromResult(Success(sb.ToString().TrimEnd()));
    }

    private static void Walk(string root, string dir, Regex regex, List<(string, DateTime)> matches, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            FileAttributes attrs;
            try { attrs = File.GetAttributes(entry); } catch { continue; }

            if ((attrs & FileAttributes.Directory) != 0)
            {
                if (SkippedDirs.Contains(name) || (attrs & FileAttributes.ReparsePoint) != 0) continue;
                Walk(root, entry, regex, matches, ct);
            }
            else
            {
                var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (regex.IsMatch(rel))
                {
                    DateTime modified;
                    try { modified = File.GetLastWriteTimeUtc(entry); } catch { modified = DateTime.MinValue; }
                    matches.Add((rel, modified));
                }
            }
        }
    }

    /// <summary>Translates a glob into an anchored regex over '/'-separated relative paths.</summary>
    internal static Regex GlobToRegex(string glob)
    {
        glob = glob.Replace('\\', '/');
        if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
        if (!glob.Contains('/')) glob = "**/" + glob;

        var sb = new StringBuilder("^");
        var braceDepth = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");   // '**/' = zero or more directories
                        }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '{': braceDepth++; sb.Append("(?:"); break;
                case '}' when braceDepth > 0: braceDepth--; sb.Append(')'); break;
                case ',' when braceDepth > 0: sb.Append('|'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        while (braceDepth-- > 0) sb.Append(')');
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
