using System.Text;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.FileSystem;

/// <summary>Apply unified-diff hunks to one existing file. Use one call per file.</summary>
public class ApplyPatchTool : BaseTool, IPreviewableTool
{
    private static readonly UTF8Encoding NoBom = new(false);

    public ApplyPatchTool(ILogger<ApplyPatchTool> logger) : base(logger) { }

    public override string Name => "apply_patch";
    public override string Description =>
        "Apply unified-diff hunks ('@@ -a,b +c,d @@' followed by ' ' context, '-' removed and '+' added lines) to ONE existing file. " +
        "Good for several separate changes in a large file. Hunks are located by their context, so line numbers may be approximate. " +
        "All hunks must apply or the file is left untouched. For several files, call once per file.";
    public override RiskLevel Risk => RiskLevel.Write;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Path to the file to patch" },
                ["patch"] = new() { Type = "string", Description = "Unified diff hunks for this file (---/+++ headers optional)" }
            },
            Required = ["path", "patch"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        var patch = GetArg<string>(call, "patch");
        if (string.IsNullOrWhiteSpace(patch)) return Error("'patch' is required.");

        try
        {
            var (plan, error) = await PlanAsync(path, patch, context);
            if (plan == null)
                return Error(error!);

            await File.WriteAllTextAsync(plan.ResolvedPath, plan.Updated, plan.HadBom ? new UTF8Encoding(true) : NoBom);
            var diff = UnifiedDiffBuilder.Build(plan.Original, plan.Updated, path);
            return Success($"Patched {path}.\n{diff}", new FileWriteResult(plan.Original, plan.Updated, FileExistedBefore: true));
        }
        catch (UnauthorizedAccessException ex) { return Error(ex.Message); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "apply_patch failed for {Path}", path);
            return Error($"Error patching file: {ex.Message}");
        }
    }

    /// <summary>The patched file this call would produce, computed exactly like <see cref="ExecuteAsync"/> but without writing.</summary>
    public async Task<ToolPreview?> PreviewAsync(ToolCall call, AgentExecutionContext context)
    {
        try
        {
            var path = GetArg<string>(call, "path");
            var patch = GetArg<string>(call, "patch");
            if (string.IsNullOrWhiteSpace(patch)) return ToolPreview.Failed("'patch' is required.");

            var (plan, error) = await PlanAsync(path, patch, context);
            return plan == null
                ? ToolPreview.Failed(error!)
                : new ToolPreview { FileChange = new FileChangePreview(path, plan.Original, plan.Updated) };
        }
        catch (Exception ex)
        {
            return ToolPreview.Failed(ex.Message);
        }
    }

    private sealed record PatchPlan(string ResolvedPath, string Original, string Updated, bool HadBom);

    private async Task<(PatchPlan? Plan, string? Error)> PlanAsync(string path, string patch, AgentExecutionContext context)
    {
        if (context.IsReadOnly)
            return (null, "Write operations are disabled in read-only mode.");

        var resolved = ResolvePath(path, context);
        if (!File.Exists(resolved))
            return (null, $"File not found: {path}");

        var bytes = await File.ReadAllBytesAsync(resolved);
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var original = NoBom.GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));

        var updated = UnifiedPatchApplier.Apply(original, patch, out var error);
        return updated == null
            ? (null, $"{error} No changes were written to {path}.")
            : (new PatchPlan(resolved, original, updated, hadBom), null);
    }
}
