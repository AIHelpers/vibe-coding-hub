using System.Text.Json;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Mcp;

/// <summary>
/// Adapts a tool exposed by an MCP server into the <see cref="ITool"/> interface so the
/// orchestrator can invoke it like any built-in tool.
/// </summary>
public class McpToolAdapter : BaseTool
{
    private readonly IMcpClient _client;
    private readonly McpToolInfo _toolInfo;

    public McpToolAdapter(IMcpClient client, McpToolInfo toolInfo, ILogger<McpToolAdapter> logger)
        : base(logger)
    {
        _client = client;
        _toolInfo = toolInfo;
        Name = $"{client.ServerName}__{toolInfo.Name}";
        Description = string.IsNullOrWhiteSpace(toolInfo.Description)
            ? $"MCP tool {toolInfo.Name} from {client.ServerName}"
            : toolInfo.Description;
        Risk = RiskLevel.Read; // MCP tools default to read risk; adjust per-server if needed
        Definition = BuildDefinition();
    }

    public override string Name { get; }
    public override string Description { get; }
    public override RiskLevel Risk { get; }
    public override ToolDefinition Definition { get; }

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        try
        {
            // Strip the server prefix to get the raw tool name for the MCP server.
            var rawName = _toolInfo.Name;
            var args = call.Arguments ?? new Dictionary<string, object?>();
            var result = await _client.CallToolAsync(rawName, args).ConfigureAwait(false);

            return new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Content = result.Content,
                IsError = result.IsError
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP tool '{Name}' failed", Name);
            return Error($"MCP tool '{Name}' failed: {ex.Message}");
        }
    }

    private ToolDefinition BuildDefinition()
    {
        var schema = new JsonSchema();
        if (_toolInfo.InputSchema is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            try
            {
                schema = JsonSerializer.Deserialize<JsonSchema>(el.GetRawText()) ?? new JsonSchema();
            }
            catch { /* fall back to empty schema */ }
        }

        return new ToolDefinition
        {
            Name = Name,
            Description = Description,
            Parameters = schema
        };
    }
}