using System.Text.Json;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Base;

public abstract class BaseTool : ITool
{
    protected readonly ILogger Logger;

    protected BaseTool(ILogger logger) => Logger = logger;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolDefinition Definition { get; }

    public abstract Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context);

    protected T GetArg<T>(ToolCall call, string key, T defaultValue = default!)
    {
        if (!call.Arguments.TryGetValue(key, out var value) || value == null)
            return defaultValue;

        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String when typeof(T) == typeof(string) => (T)(object)je.GetString()!,
                JsonValueKind.Number when typeof(T) == typeof(int) => (T)(object)je.GetInt32(),
                JsonValueKind.Number when typeof(T) == typeof(long) => (T)(object)je.GetInt64(),
                JsonValueKind.True or JsonValueKind.False when typeof(T) == typeof(bool) => (T)(object)je.GetBoolean(),
                JsonValueKind.Array when typeof(T) == typeof(string[]) =>
                    (T)(object)je.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
                _ => JsonSerializer.Deserialize<T>(je.GetRawText()) ?? defaultValue
            };
        }

        try { return (T)Convert.ChangeType(value, typeof(T)); }
        catch (Exception ex) 
        { 
            Logger.LogWarning(ex, "Failed to convert argument {Key} to {Type}", key, typeof(T).Name);
            return defaultValue; 
        }
    }

    protected ToolResult Success(string content, object? data = null) =>
        new() { ToolCallId = string.Empty, ToolName = Name, Content = content, Data = data };

    protected ToolResult Error(string message) =>
        new() { ToolCallId = string.Empty, ToolName = Name, Content = message, IsError = true };

    protected string ResolvePath(string path, AgentExecutionContext context)
    {
        var basePath = string.IsNullOrEmpty(context.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : context.WorkingDirectory;

        string resolvedPath;
        if (Path.IsPathRooted(path))
        {
            resolvedPath = Path.GetFullPath(path);
        }
        else
        {
            resolvedPath = Path.GetFullPath(Path.Combine(basePath, path));
        }

        // Normalize path separators before validation
        resolvedPath = Path.GetFullPath(resolvedPath);
        
        // Validate that the resolved path is within allowed paths
        ValidatePath(resolvedPath, context);
        return resolvedPath;
    }

    protected void ValidatePath(string resolvedPath, AgentExecutionContext context)
    {
        if (context.AllowedPaths.Count == 0) return;

        // Normalize path separators and ensure trailing separator for prefix matching
        resolvedPath = Path.GetFullPath(resolvedPath);
        
        var isAllowed = context.AllowedPaths.Any(allowed =>
        {
            var normalizedAllowed = Path.GetFullPath(allowed);
            // Ensure trailing separator to prevent prefix matching partial directory names
            if (!normalizedAllowed.EndsWith(Path.DirectorySeparatorChar))
                normalizedAllowed += Path.DirectorySeparatorChar;
            
            return resolvedPath.StartsWith(normalizedAllowed, StringComparison.OrdinalIgnoreCase) ||
                   resolvedPath.Equals(normalizedAllowed.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        });

        if (!isAllowed)
            throw new UnauthorizedAccessException($"Access denied to path: {resolvedPath}");
    }
}