using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Mcp;

/// <summary>
/// Configuration for a single MCP server connection.
/// Local servers use a command + args (stdio transport); remote servers use a URL (HTTP/SSE).
/// </summary>
public record McpServerConfig
{
    /// <summary>Unique server name used to namespace tools (e.g. "github").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Command to launch a local MCP server (stdio transport). Leave null for remote servers.</summary>
    public string? Command { get; init; }

    /// <summary>Arguments for the local server command.</summary>
    public List<string> Args { get; init; } = new();

    /// <summary>Environment variables for the local server process.</summary>
    public Dictionary<string, string> Env { get; init; } = new();

    /// <summary>URL for a remote MCP server (HTTP/SSE transport). Leave null for local servers.</summary>
    public string? Url { get; init; }

    /// <summary>Optional bearer token / API key for remote servers.</summary>
    public string? Token { get; init; }

    /// <summary>Whether this server is enabled. Disabled servers are skipped at startup.</summary>
    public bool Enabled { get; init; } = true;

    [JsonIgnore]
    public bool IsRemote => !string.IsNullOrEmpty(Url);
}