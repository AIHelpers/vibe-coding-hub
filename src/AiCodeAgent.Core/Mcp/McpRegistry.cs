using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Mcp;

/// <summary>
/// Registry that manages multiple MCP server connections.
/// Loads configuration from <c>mcp.json</c> files and connects to all enabled servers.
/// </summary>
public interface IMcpRegistry
{
    IReadOnlyList<IMcpClient> Clients { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync();
    IMcpClient? GetClient(string serverName);
    List<McpToolInfo> GetAllTools();
    IReadOnlyList<McpServerConfig> ListServers();
    string GetStatus(string serverName);
    int GetToolCount(string serverName);
}

public class McpRegistry : IMcpRegistry
{
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<McpServerConfig, IMcpClient> _clientFactory;
    private readonly ILogger<McpRegistry> _logger;
    private readonly List<McpServerConfig> _configs;

    public McpRegistry(
        IEnumerable<McpServerConfig> configs,
        Func<McpServerConfig, IMcpClient> clientFactory,
        ILogger<McpRegistry>? logger = null)
    {
        _configs = configs?.ToList() ?? new();
        _clientFactory = clientFactory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<McpRegistry>.Instance;
    }

    public IReadOnlyList<IMcpClient> Clients => _clients.Values.ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var cfg in _configs.Where(c => c.Enabled))
        {
            try
            {
                var client = _clientFactory(cfg);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _clients[cfg.Name] = client;
                _logger.LogInformation("MCP server '{Name}' connected ({Tools} tools)",
                    cfg.Name, (await client.ListToolsAsync(cancellationToken)).Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect MCP server '{Name}'", cfg.Name);
            }
        }
    }

    public async Task ShutdownAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.DisconnectAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting MCP client '{Name}'", client.ServerName); }
        }
        _clients.Clear();
    }

    public IMcpClient? GetClient(string serverName) =>
        _clients.TryGetValue(serverName, out var c) ? c : null;

    public List<McpToolInfo> GetAllTools()
    {
        var all = new List<McpToolInfo>();
        foreach (var client in _clients.Values)
        {
            try
            {
                var tools = client.ListToolsAsync().GetAwaiter().GetResult();
                foreach (var t in tools)
                    all.Add(t with { Name = $"{client.ServerName}__{t.Name}" });
            }
            catch { /* best-effort */ }
        }
        return all;
    }

    public IReadOnlyList<McpServerConfig> ListServers() => _configs.AsReadOnly();

    public string GetStatus(string serverName)
    {
        if (_clients.TryGetValue(serverName, out var client))
            return client.IsConnected ? "Connected" : "Disconnected";
        return _configs.Any(c => c.Name == serverName) ? "Failed" : "Unknown";
    }

    public int GetToolCount(string serverName)
    {
        if (!_clients.TryGetValue(serverName, out var client))
            return 0;
        try
        {
            return client.ListToolsAsync().GetAwaiter().GetResult().Count;
        }
        catch { return 0; }
    }
}
