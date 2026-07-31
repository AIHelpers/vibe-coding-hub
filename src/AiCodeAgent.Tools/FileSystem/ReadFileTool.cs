using System.Text;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

public class ReadFileTool : BaseTool
{
    public ReadFileTool(ILogger<ReadFileTool> logger) : base(logger) { }

    public override string Name => "read_file";
    public override string Description => 
        "Read the contents of a file. Supports reading specific line ranges.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Path to the file to read" },
                ["start_line"] = new() { Type = "integer", Description = "Start line (1-based, optional)" },
                ["end_line"] = new() { Type = "integer", Description = "End line (1-based, optional)" },
                ["encoding"] = new() { Type = "string", Description = "File encoding (default: utf-8)" }
            },
            Required = ["path"]
        }
    };

    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB max file size

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        var startLine = GetArg<int>(call, "start_line", 0);
        var endLine = GetArg<int>(call, "end_line", 0);
        var encoding = GetArg<string>(call, "encoding", "utf-8");

        try
        {
            var resolvedPath = ResolvePath(path, context);
            ValidatePath(resolvedPath, context);

            if (!File.Exists(resolvedPath))
                return Error($"File not found: {path}");

            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.Length > MaxFileSizeBytes)
                return Error($"File too large ({fileInfo.Length:N0} bytes). Use start_line/end_line to read portions.");

            var enc = Encoding.GetEncoding(encoding);

            if (startLine > 0 || endLine > 0)
            {
                var lines = await File.ReadAllLinesAsync(resolvedPath, enc);
                var start = Math.Max(0, startLine - 1);
                var end = endLine > 0 ? Math.Min(lines.Length, endLine) : lines.Length;
                var selectedLines = lines[start..end];

                var content = string.Join(Environment.NewLine, 
                    selectedLines.Select((l, i) => $"{start + i + 1}: {l}"));
                
                return Success($"```\n{content}\n```\n[Lines {start + 1}-{end} of {lines.Length}]");
            }
            else
            {
                var content = await File.ReadAllTextAsync(resolvedPath, enc);
                var lineCount = content.Split('\n').Length;
                var lang = GetLanguageFromExtension(Path.GetExtension(resolvedPath));
                
                return Success($"```{lang}\n{content}\n```\n[{lineCount} lines, {fileInfo.Length:N0} bytes]");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error(ex.Message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error reading file {Path}", path);
            return Error($"Error reading file: {ex.Message}");
        }
    }

    private static string GetLanguageFromExtension(string ext) => ext.ToLower() switch
    {
        ".cs" => "csharp",
        ".py" => "python",
        ".ts" => "typescript",
        ".js" => "javascript",
        ".rs" => "rust",
        ".go" => "go",
        ".java" => "java",
        ".cpp" or ".cc" or ".cxx" => "cpp",
        ".c" => "c",
        ".json" => "json",
        ".yaml" or ".yml" => "yaml",
        ".md" => "markdown",
        ".sh" or ".bash" => "bash",
        ".sql" => "sql",
        ".html" => "html",
        ".css" => "css",
        ".xml" => "xml",
        _ => ""
    };
}