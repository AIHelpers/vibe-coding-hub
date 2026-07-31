using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

/// <summary>
/// Targeted file editing - replacing specific fragments
/// </summary>
public class EditFileTool : BaseTool
{
    public EditFileTool(ILogger<EditFileTool> logger) : base(logger) { }

    public override string Name => "edit_file";
    public override string Description =>
        "Make targeted edits to a file by replacing specific text. " +
        "More precise than write_file for small changes.";
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
                ["old_string"] = new() { Type = "string", Description = "Exact text to replace (must match exactly)" },
                ["new_string"] = new() { Type = "string", Description = "Text to replace with" },
                ["occurrence"] = new() { Type = "integer", Description = "Which occurrence to replace (1-based, 0=all, default: 1)" }
            },
            Required = ["path", "old_string", "new_string"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        var oldString = GetArg<string>(call, "old_string");
        var newString = GetArg<string>(call, "new_string");
        var occurrence = GetArg<int>(call, "occurrence", 1);

        try
        {
            if (context.IsReadOnly)
                return Error("Write operations are disabled in read-only mode.");

            var resolvedPath = ResolvePath(path, context);
            ValidatePath(resolvedPath, context);

            if (!File.Exists(resolvedPath))
                return Error($"File not found: {path}");

            var content = await File.ReadAllTextAsync(resolvedPath, Encoding.UTF8);

            // Validate content size to prevent resource exhaustion
            if (content.Length > 1_000_000)
                return Error($"File too large ({content.Length:N0} characters). Maximum supported is 1,000,000 characters.");

            int count = CountOccurrences(content, oldString);
            if (count == 0)
                return Error($"Text not found in {path}. Make sure the text matches exactly (including whitespace).");

            string newContent;
            if (occurrence == 0)
            {
                newContent = content.Replace(oldString, newString, StringComparison.Ordinal);
            }
            else
            {
                newContent = ReplaceNthOccurrence(content, oldString, newString, occurrence);
            }

            // Create backup and schedule cleanup
            var backupPath = resolvedPath + ".bak";
            await File.WriteAllTextAsync(backupPath, content, Encoding.UTF8);

            try
            {
                await File.WriteAllTextAsync(resolvedPath, newContent, Encoding.UTF8);
            }
            catch
            {
                // If write fails, restore from backup
                await File.WriteAllTextAsync(resolvedPath, content, Encoding.UTF8);
                throw;
            }

            // Clean up backup file after successful edit
            try { File.Delete(backupPath); } catch { /* Best effort cleanup */ }

            var diff = GenerateDiff(content, newContent, path);
            return Success($"Successfully edited {path}.\n\n{diff}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error editing file {Path}", path);
            return Error($"Error editing file: {ex.Message}");
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string ReplaceNthOccurrence(string text, string oldStr, string newStr, int n)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(oldStr, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            if (count == n)
            {
                return string.Concat(text.AsSpan(0, index), newStr, text.AsSpan(index + oldStr.Length));
            }
            index += oldStr.Length;
        }
        return text;
    }

    private static string GenerateDiff(string original, string modified, string path)
    {
        var origLines = original.Split('\n');
        var modLines = modified.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"--- {path} (original)");
        sb.AppendLine($"+++ {path} (modified)");

        // Simple diff - show changed regions
        int maxLines = Math.Max(origLines.Length, modLines.Length);
        for (int i = 0; i < maxLines; i++)
        {
            var origLine = i < origLines.Length ? origLines[i] : null;
            var modLine = i < modLines.Length ? modLines[i] : null;

            if (origLine != modLine)
            {
                if (origLine != null) sb.AppendLine($"- {origLine}");
                if (modLine != null) sb.AppendLine($"+ {modLine}");
            }
        }

        return sb.ToString();
    }
}