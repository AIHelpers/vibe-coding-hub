using System.Text;
using AiCodeAgent.Core.Models;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Code;

/// <summary>
/// Agent-facing LSP tool: finds all references to a symbol at a given
/// file/line/character using the language server. Read-only, cheap to approve.
/// </summary>
public class FindReferencesTool : LspToolBase
{
    public FindReferencesTool(ILogger<FindReferencesTool> logger, LanguageProviderRegistry registry)
        : base(logger, registry)
    {
    }

    public override string Name => "find_references";
    public override string Description =>
        "Find all references to a symbol in the workspace using the language server (LSP). " +
        "Provide path, line (1-based), and character (1-based) of the symbol.";

    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Path to the file containing the symbol" },
                ["line"] = new() { Type = "integer", Description = "0-based line number of the symbol" },
                ["character"] = new() { Type = "integer", Description = "0-based character offset of the symbol" }
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
            var locations = await client.ReferencesAsync(uri, new Position(line, character), includeDeclaration: true);

            if (locations.Count == 0)
                return Success($"No references found for symbol at {resolvedPath}:{line + 1}:{character + 1}");

            var sb = new StringBuilder();
            sb.AppendLine($"Found {locations.Count} reference(s):");
            sb.AppendLine();

            foreach (var loc in locations.Take(100))
            {
                sb.AppendLine($"  {FormatLocation(loc)}");
            }

            if (locations.Count > 100)
                sb.AppendLine($"  ... and {locations.Count - 100} more");

            return Success(sb.ToString(), locations.Take(100).Select(FormatLocation).ToList());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "find_references failed for {Path}", resolvedPath);
            return Error($"find_references failed: {ex.Message}");
        }
    }
}