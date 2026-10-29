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

    /// <summary>Why the server could not be connected at the last <see cref="InitializeAsync"/>, or null.</summary>
    string? GetError(string serverName) => null;

    /// <summary>Replaces the configured servers (after the user changed mcp.json). Call <see cref="ShutdownAsync"/> before and <see cref="InitializeAsync"/> after.</summary>
    void ReplaceConfigs(IEnumerable<McpServerConfig> configs) { }
}

public class McpRegistry : IMcpRegistry
{
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<McpServerConfig, IMcpClient> _clientFactory;
    private readonly ILogger<McpRegistry> _logger;
    private List<McpServerConfig> _configs;
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

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
        _errors.Clear();
        foreach (var cfg in _configs.Where(c => c.Enabled))
        {
            IMcpClient? client = null;
            try
            {
                if (cfg.MissingEnv.Count > 0)
                    throw new InvalidOperationException("Environment variable " + string.Join(", ", cfg.MissingEnv) + " is not set.");
                client = _clientFactory(cfg);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _clients[cfg.Name] = client;
                _logger.LogInformation("MCP server '{Name}' connected ({Tools} tools)",
                    cfg.Name, (await client.ListToolsAsync(cancellationToken)).Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect MCP server '{Name}'", cfg.Name);
                _errors[cfg.Name] = ex.Message;
                if (client != null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
                }
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

    public void ReplaceConfigs(IEnumerable<McpServerConfig> configs) => _configs = configs?.ToList() ?? new();

    public string? GetError(string serverName) => _errors.TryGetValue(serverName, out var e) ? e : null;

    public string GetStatus(string serverName)
    {
        if (_clients.TryGetValue(serverName, out var client))
            return client.IsConnected ? "Connected" : "Disconnected";
        var config = _configs.FirstOrDefault(c => string.Equals(c.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (config == null) return "Unknown";
        return config.Enabled ? "Failed" : "Disabled";
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
