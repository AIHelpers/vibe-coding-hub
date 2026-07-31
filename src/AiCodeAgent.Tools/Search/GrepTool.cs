using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Search;

public class GrepTool : BaseTool
{
    public GrepTool(ILogger<GrepTool> logger) : base(logger) { }

    public override string Name => "grep";
    public override string Description =>
        "Search for patterns in files using regex. Returns matching lines with context.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["pattern"] = new() { Type = "string", Description = "Regex pattern to search for" },
                ["path"] = new() { Type = "string", Description = "File or directory to search in" },
                ["recursive"] = new() { Type = "boolean", Description = "Search recursively (default: true)" },
                ["case_sensitive"] = new() { Type = "boolean", Description = "Case sensitive (default: false)" },
                ["context_lines"] = new() { Type = "integer", Description = "Lines of context around match (default: 2)" },
                ["file_pattern"] = new() { Type = "string", Description = "File pattern filter (e.g., '*.cs')" },
                ["max_results"] = new() { Type = "integer", Description = "Maximum results to return (default: 50)" }
            },
            Required = ["pattern"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var pattern = GetArg<string>(call, "pattern");
        var path = GetArg<string>(call, "path", ".");
        var recursive = GetArg<bool>(call, "recursive", true);
        var caseSensitive = GetArg<bool>(call, "case_sensitive", false);
        var contextLines = GetArg<int>(call, "context_lines", 2);
        var filePattern = GetArg<string>(call, "file_pattern", "*");
        var maxResults = GetArg<int>(call, "max_results", 50);

        var resolvedPath = ResolvePath(path, context);
        var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOptions | RegexOptions.Compiled);
        }
        catch (RegexParseException ex)
        {
            return Error($"Invalid regex pattern: {ex.Message}");
        }

        var results = new List<GrepMatch>();
        var files = GetFilesToSearch(resolvedPath, filePattern, recursive);

        // When searching a single file, use the file name as the base for relative paths
        var basePath = File.Exists(resolvedPath) ? Path.GetDirectoryName(resolvedPath) ?? resolvedPath : resolvedPath;

        foreach (var file in files)
        {
            if (results.Count >= maxResults) break;
            try
            {
                var lines = await File.ReadAllLinesAsync(file);
                for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        var start = Math.Max(0, i - contextLines);
                        var end = Math.Min(lines.Length - 1, i + contextLines);
                        results.Add(new GrepMatch
                        {
                            FilePath = Path.GetRelativePath(basePath, file),
                            LineNumber = i + 1,
                            MatchLine = lines[i],
                            ContextBefore = lines[start..i],
                            ContextAfter = lines[(i + 1)..(end + 1)]
                        });
                    }
                }
            }
            catch { /* Skip unreadable files */ }
        }

        if (results.Count == 0)
            return Success($"No matches found for pattern '{pattern}'");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es) for '{pattern}':");
        sb.AppendLine();

        foreach (var match in results)
        {
            sb.AppendLine($"📄 {match.FilePath}:{match.LineNumber}");
            foreach (var line in match.ContextBefore)
                sb.AppendLine($"  {line}");
            sb.AppendLine($"► {match.MatchLine}");
            foreach (var line in match.ContextAfter)
                sb.AppendLine($"  {line}");
            sb.AppendLine();
        }

        return Success(sb.ToString());
    }

    private static IEnumerable<string> GetFilesToSearch(string path, string pattern, bool recursive)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) return [];

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(path, pattern, searchOption)
            .Where(f => !IsInIgnoredDirectory(f));
    }

    private static bool IsInIgnoredDirectory(string path)
    {
        var ignored = new[] { ".git", "node_modules", "bin", "obj", "__pycache__" };
        return ignored.Any(d => path.Contains($"{Path.DirectorySeparatorChar}{d}{Path.DirectorySeparatorChar}"));
    }

    private record GrepMatch
    {
        public string FilePath { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string MatchLine { get; init; } = string.Empty;
        public string[] ContextBefore { get; init; } = [];
        public string[] ContextAfter { get; init; } = [];
    }
}
