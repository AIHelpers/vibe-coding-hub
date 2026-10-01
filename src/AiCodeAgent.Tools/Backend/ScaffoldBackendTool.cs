using System.Linq;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Backend;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Backend;

/// <summary>
/// Tool that scaffolds bundled backend primitives (auth, db, storage, etc.)
/// into a target directory within the agent's workspace.
/// </summary>
public class ScaffoldBackendTool : BaseTool
{
    private readonly BackendScaffolder _scaffolder;
    private readonly BackendPrimitiveCatalog _catalog;

    public ScaffoldBackendTool(
        BackendScaffolder scaffolder,
        BackendPrimitiveCatalog catalog,
        ILogger<ScaffoldBackendTool> logger)
        : base(logger)
    {
        _scaffolder = scaffolder;
        _catalog = catalog;
    }

    public override string Name => "scaffold_backend";

    public override string Description =>
        "Scaffold bundled backend primitives (auth, db, storage, email, payments) into a target directory. " +
        "Writes template files, .env.example, and a backend.js wiring hook. " +
        "Dependencies are resolved automatically (e.g. storage requires auth).";

    public override RiskLevel Risk => RiskLevel.Write;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["primitives"] = new()
                {
                    Type = "array",
                    Description = "Primitive ids to scaffold, e.g. [\"auth\",\"db\"]"
                },
                ["target_path"] = new()
                {
                    Type = "string",
                    Description = "Relative or absolute path to the target app directory"
                },
                ["project_name"] = new()
                {
                    Type = "string",
                    Description = "Project name used for token replacement and file headers"
                },
                ["stack"] = new()
                {
                    Type = "string",
                    Description = "Optional stack filter (e.g. 'node', 'python')"
                }
            },
            Required = ["primitives", "target_path", "project_name"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        try
        {
            if (context.IsReadOnly)
                return Error("Write operations are disabled in read-only mode.");

            var primitives = GetArg<string[]>(call, "primitives") ?? [];
            var targetPath = GetArg<string>(call, "target_path");
            var projectName = GetArg<string>(call, "project_name");
            var stack = GetArg<string>(call, "stack");

            if (primitives.Length == 0)
                return Error("At least one primitive id must be provided in 'primitives'.");
            if (string.IsNullOrWhiteSpace(targetPath))
                return Error("'target_path' is required.");
            if (string.IsNullOrWhiteSpace(projectName))
                return Error("'project_name' is required.");

            // Resolve path within allowed paths
            var resolved = ResolvePath(targetPath, context);

            // List available primitives for helpful errors
            var available = string.Join(", ", _catalog.GetAll().Select(p => p.Id));

            var result = await _scaffolder.ScaffoldAsync(
                primitives,
                resolved,
                projectName,
                stack,
                CancellationToken.None);

            var lines = new List<string>
            {
                $"Scaffolded {result.Primitives.Count} primitive(s) into {targetPath}:",
                string.Join(", ", result.Primitives.Select(p => p.Id)),
                "",
                "Files written:"
            };
            lines.AddRange(result.WrittenFiles.Select(f => $"  - {f}"));
            lines.Add("");
            lines.Add("Environment variables (.env.example):");
            lines.AddRange(result.EnvVars.Select(v => $"  - {v}"));

            if (result.Warnings.Count > 0)
            {
                lines.Add("");
                lines.Add("Warnings:");
                lines.AddRange(result.Warnings.Select(w => $"  ! {w}"));
            }

            lines.Add("");
            lines.Add($"Available primitives: {available}");

            return Success(string.Join("\n", lines), result);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in scaffold_backend");
            return Error($"Error scaffolding backend: {ex.Message}");
        }
    }
}