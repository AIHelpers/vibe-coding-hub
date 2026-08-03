using System.Text;
using AiCodeAgent.Core.Models;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Code;

/// <summary>
/// Agent-facing LSP tool: resolves the definition of a symbol at a given
/// file/line/character using the language server. Read-only, cheap to approve.
/// </summary>
public class GoToDefinitionTool : LspToolBase
{
    public GoToDefinitionTool(ILogger<GoToDefinitionTool> logger, LanguageProviderRegistry registry)
        : base(logger, registry)
    {
    }

    public override string Name => "go_to_definition";
    public override string Description =>
        "Resolve the definition location of a symbol using the language server (LSP). " +
        "Provide path, line (0-based), and character (0-based) of the reference.";

    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Path to the file containing the symbol reference" },
                ["line"] = new() { Type = "integer", Description = "0-based line number of the reference" },
                ["character"] = new() { Type = "integer", Description = "0-based character offset of the reference" }
            },
            Required = ["path", "line", "character"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        var line = GetArg<int>(call, "line", 0);
        var character = GetArg<int>(call, "character", 0);

        var (resolvedPath, uri, client) = await PrepareAsync(path, context);
        if (client == null || string.IsNullOrEmpty(uri))
            return Error($"No language server available for: {path}");

        try
        {
            var locations = await client.DefinitionAsync(uri, new Position(line, character));

            if (locations.Count == 0)
                return Success($"No definition found for symbol at {resolvedPath}:{line + 1}:{character + 1}");

            var sb = new StringBuilder();
            sb.AppendLine($"Definition found at:");
            sb.AppendLine();

            foreach (var loc in locations.Take(20))
            {
                sb.AppendLine($"  {FormatLocation(loc)}");
            }

            return Success(sb.ToString(), locations.Take(20).Select(FormatLocation).ToList());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "go_to_definition failed for {Path}", resolvedPath);
            return Error($"go_to_definition failed: {ex.Message}");
        }
    }
}