using System.Text.Json;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Mcp;

/// <summary>
/// Definition of a tool exposed by an MCP server.
/// </summary>
public record McpToolInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public JsonElement? InputSchema { get; init; }
}

/// <summary>
/// Result of invoking an MCP tool.
/// </summary>
public record McpToolResult
{
    public string Content { get; init; } = string.Empty;
    public bool IsError { get; init; }
}

/// <summary>
/// A resource exposed by an MCP server.
/// </summary>
public record McpResourceInfo
{
    public string Uri { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
}

/// <summary>
/// Contents of an MCP resource.
/// </summary>
public record McpResourceContent
{
    public string Uri { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Client for a single MCP server connection.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    string ServerName { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<List<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default);
    Task<List<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default);
    Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken cancellationToken = default);
}