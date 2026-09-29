using System.Text;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

public sealed class EditOperation
{
    [System.Text.Json.Serialization.JsonPropertyName("old_string")]
    public string OldString { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("new_string")]
    public string NewString { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("replace_all")]
    public bool ReplaceAll { get; set; }
}

/// <summary>
/// Several exact-text replacements in ONE file, applied in order and all-or-nothing: if any edit fails to match,
/// nothing is written. Cheaper and safer than a chain of edit_file calls.
/// </summary>
public class MultiEditTool : BaseTool
{
    private static readonly UTF8Encoding NoBom = new(false);

    public MultiEditTool(ILogger<MultiEditTool> logger) : base(logger) { }

    public override string Name => "multi_edit";
    public override string Description =>
        "Apply several text replacements to a single file atomically. Edits run in order (later edits see earlier results). " +
        "Each old_string must match exactly once unless replace_all is true. If any edit fails, the file is left untouched.";
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
                ["edits"] = new()
                {
                    Type = "array",
                    Description = "Ordered list of replacements",
                    Items = new PropertySchema
                    {
                        Type = "object",
                        Properties = new()
                        {
                            ["old_string"] = new() { Type = "string", Description = "Exact text to replace" },
                            ["new_string"] = new() { Type = "string", Description = "Replacement text" },
                            ["replace_all"] = new() { Type = "boolean", Description = "Replace every occurrence (default false)" }
                        },
                        Required = ["old_string", "new_string"]
                    }
                }
            },
            Required = ["path", "edits"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        List<EditOperation>? edits;
        try { edits = GetArg<List<EditOperation>>(call, "edits"); }
        catch (Exception ex) { return Error($"Invalid 'edits': {ex.Message}"); }

        if (edits == null || edits.Count == 0)
            return Error("'edits' must contain at least one {old_string, new_string}.");

        try
        {
            if (context.IsReadOnly)
                return Error("Write operations are disabled in read-only mode.");

            var resolved = ResolvePath(path, context);
            if (!File.Exists(resolved))
                return Error($"File not found: {path}");

            var bytes = await File.ReadAllBytesAsync(resolved);
            var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var original = NoBom.GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));

            var updated = ApplyEdits(original, edits, out var error);
            if (updated == null)
                return Error($"{error} No changes were written to {path}.");

            await File.WriteAllTextAsync(resolved, updated, hadBom ? new UTF8Encoding(true) : NoBom);
            var diff = UnifiedDiffBuilder.Build(original, updated, path);
            return Success($"Applied {edits.Count} edit(s) to {path}.\n{diff}",
                new FileWriteResult(original, updated, FileExistedBefore: true));
        }
        catch (UnauthorizedAccessException ex) { return Error(ex.Message); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "multi_edit failed for {Path}", path);
            return Error($"Error editing file: {ex.Message}");
        }
    }

    /// <summary>Applies edits in order. Returns null (and an error message) if any edit is missing or ambiguous.</summary>
    internal static string? ApplyEdits(string content, IReadOnlyList<EditOperation> edits, out string error)
    {
        error = string.Empty;
        var crlf = content.Contains("\r\n", StringComparison.Ordinal);
        var current = content;

        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var oldS = Match(e.OldString, crlf);
            var newS = Match(e.NewString, crlf);
            if (string.IsNullOrEmpty(oldS)) { error = $"Edit {i + 1}: old_string is empty."; return null; }
            if (oldS == newS) { error = $"Edit {i + 1}: old_string and new_string are identical."; return null; }

            var count = Count(current, oldS);
            if (count == 0) { error = $"Edit {i + 1}: text not found (it must match exactly, including whitespace)."; return null; }
            if (count > 1 && !e.ReplaceAll)
            {
                error = $"Edit {i + 1}: text matches {count} places. Add surrounding context to make it unique, or set replace_all.";
                return null;
            }
            current = e.ReplaceAll
                ? current.Replace(oldS, newS, StringComparison.Ordinal)
                : string.Concat(current.AsSpan(0, current.IndexOf(oldS, StringComparison.Ordinal)), newS,
                                current.AsSpan(current.IndexOf(oldS, StringComparison.Ordinal) + oldS.Length));
        }
        return current;
    }

    private static string Match(string text, bool crlf)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return crlf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    private static int Count(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1) { count++; index += pattern.Length; }
        return count;
    }
}
