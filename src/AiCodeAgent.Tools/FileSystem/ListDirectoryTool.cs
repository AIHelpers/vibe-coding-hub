using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

public class ListDirectoryTool : BaseTool
{
    private static readonly HashSet<string> DefaultIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".idea", "dist", "build", ".nuget", "packages"
    };

    public ListDirectoryTool(ILogger<ListDirectoryTool> logger) : base(logger) { }

    public override string Name => "list_directory";
    public override string Description =>
        "List files and directories. Shows a tree structure with file sizes.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Directory path (default: current directory)" },
                ["recursive"] = new() { Type = "boolean", Description = "List recursively" },
                ["depth"] = new() { Type = "integer", Description = "Max depth for recursive listing (default: 3)" },
                ["pattern"] = new() { Type = "string", Description = "Glob pattern filter (e.g., '*.cs')" },
                ["show_hidden"] = new() { Type = "boolean", Description = "Show hidden files" }
            },
            Required = []
        }
    };

    public override Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path", ".");
        var recursive = GetArg<bool>(call, "recursive", false);
        var depth = GetArg<int>(call, "depth", 3);
        var pattern = GetArg<string>(call, "pattern", "*");
        var showHidden = GetArg<bool>(call, "show_hidden", false);

        try
        {
            var resolvedPath = ResolvePath(path, context);
            if (!Directory.Exists(resolvedPath))
                return Task.FromResult(Error($"Directory not found: {path}"));

            var sb = new StringBuilder();
            sb.AppendLine(resolvedPath);
            BuildTree(resolvedPath, sb, "", 0, recursive ? depth : 0, pattern, showHidden);

            return Task.FromResult(Success($"```\n{sb}\n```"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error($"Error listing directory: {ex.Message}"));
        }
    }

    private static void BuildTree(
        string path, StringBuilder sb, string prefix,
        int currentDepth, int maxDepth, string pattern, bool showHidden)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(path)
                .OrderBy(e => File.Exists(e) ? 1 : 0)
                .ThenBy(e => Path.GetFileName(e));
        }
        catch { return; }

        var items = entries.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = Path.GetFileName(item);
            var isLast = i == items.Count - 1;

            if (!showHidden && name.StartsWith('.')) continue;
            if (DefaultIgnore.Contains(name)) continue;

            var connector = isLast ? "└── " : "├── ";
            var extension = isLast ? "    " : "│   ";

            if (File.Exists(item))
            {
                if (!MatchesPattern(name, pattern)) continue;
                var size = new FileInfo(item).Length;
                sb.AppendLine($"{prefix}{connector}{name} ({FormatSize(size)})");
            }
            else
            {
                sb.AppendLine($"{prefix}{connector}{name}/");
                if (currentDepth < maxDepth)
                    BuildTree(item, sb, prefix + extension, currentDepth + 1, maxDepth, pattern, showHidden);
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1}MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F1}GB"
    };
}