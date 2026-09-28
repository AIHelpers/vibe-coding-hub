using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using AiCodeAgent.Tools.Code;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

/// <summary>
/// Targeted file editing - replacing specific fragments.
/// After a successful edit, surfaces live LSP diagnostics (type errors,
/// warnings) to the agent so it can react immediately.
/// </summary>
public class EditFileTool : BaseTool
{
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AfterEditDiagnosticsReporter? _diagnosticsReporter;

    public EditFileTool(ILogger<EditFileTool> logger) : base(logger) { }

    public EditFileTool(ILogger<EditFileTool> logger, AfterEditDiagnosticsReporter diagnosticsReporter)
        : base(logger)
    {
        _diagnosticsReporter = diagnosticsReporter;
    }

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
            if (occurrence != 0 && occurrence > count)
                return Error($"Requested occurrence {occurrence} but only {count} occurrence(s) of the text were found in {path}.");

            string newContent;
            if (occurrence == 0)
            {
                newContent = content.Replace(oldString, newString, StringComparison.Ordinal);
            }
            else
            {
                newContent = ReplaceNthOccurrence(content, oldString, newString, occurrence);
            }

            // Write without a BOM (a prior version always wrote UTF-8-with-BOM,
            // adding a BOM to files that had none, which produced noisy diffs
            // and broke some shell scripts). No .bak side file either —
            // CheckpointManager already captured the pre-edit content, so Undo
            // works without one, and a stray .bak next to the user's file was
            // triggering file watchers and getting left behind on crashes.
            await File.WriteAllTextAsync(resolvedPath, newContent, NoBomUtf8);

            var diff = UnifiedDiffBuilder.Build(content, newContent, path);

            // Surface live LSP diagnostics (type errors/warnings) after the edit.
            var diagnostics = await ReportDiagnosticsAsync(resolvedPath, newContent, context);
            return Success(
                $"Successfully edited {path}.\n\n{diff}{diagnostics}",
                new FileWriteResult(content, newContent, FileExistedBefore: true));
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

    /// <summary>
    /// Surface live LSP diagnostics after the edit, if a reporter is configured.
    /// </summary>
    private async Task<string> ReportDiagnosticsAsync(
        string resolvedPath, string newContent, AgentExecutionContext context)
    {
        if (_diagnosticsReporter == null)
            return string.Empty;

        var workspaceRoot = string.IsNullOrEmpty(context.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : context.WorkingDirectory;

        return await _diagnosticsReporter.ReportAsync(resolvedPath, newContent, workspaceRoot);
    }
}
