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
    public abstract RiskLevel Risk { get; }
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

    /// <summary>Resolves symlinks/junctions on the deepest existing part of the path.</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var current = full;
            var tail = new Stack<string>();
            while (!File.Exists(current) && !Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent)) return full;
                tail.Push(Path.GetFileName(current));
                current = parent;
            }

            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var real = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            while (tail.Count > 0)
                real = Path.Combine(real, tail.Pop());
            return Path.GetFullPath(real);
        }
        catch
        {
            return full;
        }
    }

    private static readonly string[] SensitiveNames =
        { ".env", ".netrc", ".npmrc", ".pgpass", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials", "secrets.json" };
    private static readonly string[] SensitiveExtensions = { ".pem", ".key", ".pfx", ".p12", ".kdbx" };

    /// <summary>Credential-style files the agent has no business reading or writing (only enforced when file access is confined).</summary>
    internal static bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) &&
             !name.EndsWith(".example", StringComparison.OrdinalIgnoreCase) &&
             !name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) &&
             !name.EndsWith(".template", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (SensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase) && !name.Equals(".env", StringComparison.OrdinalIgnoreCase))
            return true;
        if (SensitiveExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            return true;
        var dirs = path.Replace('\\', '/');
        return dirs.Contains("/.ssh/", StringComparison.OrdinalIgnoreCase) ||
               dirs.Contains("/.aws/", StringComparison.OrdinalIgnoreCase) ||
               dirs.Contains("/.git/hooks/", StringComparison.OrdinalIgnoreCase) ||
               dirs.EndsWith("/.git/config", StringComparison.OrdinalIgnoreCase);
    }

    protected void ValidatePath(string resolvedPath, AgentExecutionContext context)
    {
        if (context.AllowedPaths.Count == 0) return;

        // Judge the REAL location: a symlink inside the workspace that points outside
        // it must not be a way out.
        resolvedPath = RealPath(resolvedPath);

        if (IsSensitiveFile(resolvedPath))
            throw new UnauthorizedAccessException($"Access denied to sensitive file: {Path.GetFileName(resolvedPath)}");

        var isAllowed = context.AllowedPaths.Any(allowed =>
        {
            var normalizedAllowed = RealPath(allowed);
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