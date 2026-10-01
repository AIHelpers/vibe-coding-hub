using System.Text;
using AiCodeAgent.Core.Models;
using AiCodeAgent.LanguageServices;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Code;

/// <summary>
/// Agent-facing LSP tool: retrieves structured diagnostics for a file from
/// the live language server cache, instead of shelling out to run_diagnostics.
/// Read-only, cheap to approve.
/// </summary>
public class GetDiagnosticsTool : LspToolBase
{
    public GetDiagnosticsTool(ILogger<GetDiagnosticsTool> logger, LanguageProviderRegistry registry)
        : base(logger, registry)
    {
    }

    public override string Name => "get_diagnostics";
    public override string Description =>
        "Get live diagnostics (errors, warnings) for a file from the language server (LSP). " +
        "Returns structured diagnostics with line/character ranges and severity.";

    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["path"] = new() { Type = "string", Description = "Path to the file to check for diagnostics" },
                ["severity"] = new() { Type = "string", Description = "Filter by severity: error, warning, information, hint (optional)" }
            },
            Required = ["path"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var path = GetArg<string>(call, "path");
        var severityFilter = GetArg<string>(call, "severity", "").ToLowerInvariant();

        var (resolvedPath, uri, client) = await PrepareAsync(path, context);
        if (client == null || string.IsNullOrEmpty(uri))
            return Error($"No language server available for: {path}");

        // Give the server a moment to publish diagnostics for the opened file.
        // The diagnostics arrive asynchronously via publishDiagnostics.
        await Task.Delay(500);

        var diagnostics = client.GetDiagnostics(uri);

        if (!string.IsNullOrEmpty(severityFilter))
        {
            diagnostics = diagnostics
                .Where(d => d.Severity.ToString().Equals(severityFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (diagnostics.Count == 0)
            return Success($"No diagnostics for {resolvedPath}");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {diagnostics.Count} diagnostic(s) in {resolvedPath}:");
        sb.AppendLine();

        foreach (var diag in diagnostics.Take(100))
        {
            var line = diag.Range.Start.Line + 1;
            var col = diag.Range.Start.Character + 1;
            sb.AppendLine($"  [{diag.Severity}] {line}:{col} {diag.Message}");
            if (!string.IsNullOrEmpty(diag.Code))
                sb.AppendLine($"      Code: {diag.Code}");
        }

        if (diagnostics.Count > 100)
            sb.AppendLine($"  ... and {diagnostics.Count - 100} more");

        return Success(sb.ToString(), diagnostics.Take(100).Select(d => new
        {
            line = d.Range.Start.Line + 1,
            character = d.Range.Start.Character + 1,
            endLine = d.Range.End.Line + 1,
            endCharacter = d.Range.End.Character + 1,
            severity = d.Severity.ToString().ToLowerInvariant(),
            message = d.Message,
            code = d.Code,
            source = d.Source
        }).ToList());
    }
}