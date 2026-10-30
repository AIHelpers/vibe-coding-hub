using System.Net.Http;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Core.Network;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Mcp;

/// <summary>Creates the right client for a server entry: HTTP for a URL, stdio for a command.</summary>
public static class McpClients
{
    public static IMcpClient Create(McpServerConfig config, ILoggerFactory? loggerFactory = null) =>
        config.IsRemote
            ? new HttpMcpClient(config, new HttpClient(NetworkPolicyHandler.Wrap(NetworkPurpose.Mcp)), loggerFactory?.CreateLogger<HttpMcpClient>())
            : new StdioMcpClient(config, loggerFactory?.CreateLogger<StdioMcpClient>());
}
