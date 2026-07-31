using System.Text;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

public class WriteFileTool : BaseTool
{
    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB max file size
    private const int MaxContentLength = 1_000_000; // 1M characters max content

    public WriteFileTool(ILogger<WriteFileTool> logger) : base(logger) { }

    public override string Name => "write_file";
    public override string Description =>
        "Write content to a file. Creates the file and directories if they don't exist. " +
        "Can create new files or overwrite existing ones.";
    public override RiskLevel Risk => RiskLevel.Write;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Path to the file" },
                ["content"] = new() { Type = "string", Description = "Content to write" },
                ["append"] = new() { Type = "boolean", Description = "Append to file instead of overwriting" },
                ["create_dirs"] = new() { Type = "boolean", Description = "Create parent directories if needed (default: true)" }
            },
            Required = ["path", "content"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        var content = GetArg<string>(call, "content");
        var append = GetArg<bool>(call, "append", false);
        var createDirs = GetArg<bool>(call, "create_dirs", true);

        try
        {
            if (context.IsReadOnly)
                return Error("Write operations are disabled in read-only mode.");

            // Validate content size to prevent resource exhaustion
            if (content.Length > MaxContentLength)
                return Error($"Content too large ({content.Length:N0} characters). Maximum allowed is {MaxContentLength:N0} characters.");

            var resolvedPath = ResolvePath(path, context);
            ValidatePath(resolvedPath, context);

            // Check if the resulting file would exceed size limits
            var contentBytes = Encoding.UTF8.GetByteCount(content);
            if (contentBytes > MaxFileSizeBytes)
                return Error($"Content too large ({contentBytes:N0} bytes). Maximum file size is {MaxFileSizeBytes:N0} bytes.");

            if (createDirs)
            {
                var dir = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }

            if (append)
                await File.AppendAllTextAsync(resolvedPath, content, Encoding.UTF8);
            else
                await File.WriteAllTextAsync(resolvedPath, content, Encoding.UTF8);

            var lines = content.Split('\n').Length;
            var action = append ? "Appended" : (File.Exists(resolvedPath) ? "Updated" : "Created");

            return Success($"{action} {path} ({lines} lines, {contentBytes:N0} bytes)");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error writing file {Path}", path);
            return Error($"Error writing file: {ex.Message}");
        }
    }
}