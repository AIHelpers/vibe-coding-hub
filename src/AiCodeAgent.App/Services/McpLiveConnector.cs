using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Applies changes made in Settings → MCP servers without restarting the app: disconnects every server, reads mcp.json again,
/// connects the enabled ones and registers their tools with the agent's tool registry.
/// </summary>
public sealed class McpLiveConnector
{
    private readonly IMcpRegistry _registry;
    private readonly IToolRegistry _tools;
    private readonly ILoggerFactory? _loggers;
    private readonly Func<string?> _projectRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public McpLiveConnector(IMcpRegistry registry, IToolRegistry tools, ILoggerFactory? loggers, Func<string?> projectRoot)
    {
        _registry = registry;
        _tools = tools;
        _loggers = loggers;
        _projectRoot = projectRoot;
    }

    public IMcpClient CreateClient(McpServerConfig config) => McpClients.Create(config, _loggers);

    public async Task ReloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var tool in _tools.GetAllTools().Where(t => t is McpToolAdapter).ToList())
                _tools.Unregister(tool.Name);
            await _registry.ShutdownAsync().ConfigureAwait(false);

            _registry.ReplaceConfigs(McpConfigLoader.Load(_projectRoot()));
            await _registry.InitializeAsync().ConfigureAwait(false);

            var logger = _loggers?.CreateLogger<McpToolAdapter>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<McpToolAdapter>.Instance;
            foreach (var client in _registry.Clients)
            {
                try
                {
                    foreach (var info in await client.ListToolsAsync().ConfigureAwait(false))
                        _tools.Register(new McpToolAdapter(client, info, logger));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not list the tools of MCP server '{Name}'", client.ServerName);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
