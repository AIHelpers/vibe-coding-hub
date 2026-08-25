using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Mcp;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Mcp;

/// <summary>
/// MCP client that communicates with a remote MCP server over HTTP (JSON-RPC 2.0 POST).
/// Uses a simple request/response model; SSE streaming endpoints can be added later.
/// </summary>
public class HttpMcpClient : IMcpClient
{
    private readonly McpServerConfig _config;
    private readonly ILogger<HttpMcpClient> _logger;
    private readonly HttpClient _httpClient;
    private int _nextRequestId;
    private bool _connected;

    public string ServerName => _config.Name;
    public bool IsConnected => _connected;

    public HttpMcpClient(McpServerConfig config, HttpClient httpClient, ILogger<HttpMcpClient>? logger = null)
    {
        _config = config;
        _httpClient = httpClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpMcpClient>.Instance;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
            throw new InvalidOperationException($"MCP server '{_config.Name}' has no URL.");

        if (!string.IsNullOrEmpty(_config.Token))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.Token);

        // Initialize handshake
        await SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "AiCodeAgent", version = "1.0" }
        }, cancellationToken).ConfigureAwait(false);

        // Send initialized notification
        await SendNotificationAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);

        _connected = true;
        _logger.LogInformation("MCP HTTP server '{Name}' connected", _config.Name);
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("tools/list", new { }, cancellationToken).ConfigureAwait(false);
        var tools = new List<McpToolInfo>();
        if (resp["tools"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                tools.Add(new McpToolInfo
                {
                    Name = t?["name"]?.GetValue<string>() ?? "",
                    Description = t?["description"]?.GetValue<string>() ?? "",
                    InputSchema = t?["inputSchema"] is JsonNode is2 ? is2.Deserialize<JsonElement?>() : null
                });
            }
        }
        return tools;
    }

    public async Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("tools/call", new
        {
            name = toolName,
            arguments = args
        }, cancellationToken).ConfigureAwait(false);

        var content = resp["content"] as JsonArray;
        var text = content?.Select(c => c?["text"]?.GetValue<string>() ?? "").FirstOrDefault() ?? "";
        var isError = resp["isError"]?.GetValue<bool>() ?? false;
        return new McpToolResult { Content = text, IsError = isError };
    }

    public async Task<List<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("resources/list", new { }, cancellationToken).ConfigureAwait(false);
        var resources = new List<McpResourceInfo>();
        if (resp["resources"] is JsonArray arr)
        {
            foreach (var r in arr)
            {
                resources.Add(new McpResourceInfo
                {
                    Uri = r?["uri"]?.GetValue<string>() ?? "",
                    Name = r?["name"]?.GetValue<string>() ?? "",
                    Description = r?["description"]?.GetValue<string>() ?? "",
                    MimeType = r?["mimeType"]?.GetValue<string>() ?? ""
                });
            }
        }
        return resources;
    }

    public async Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("resources/read", new { uri }, cancellationToken).ConfigureAwait(false);
        var contents = resp["contents"] as JsonArray;
        var first = contents?.FirstOrDefault();
        return new McpResourceContent
        {
            Uri = uri,
            Text = first?["text"]?.GetValue<string>() ?? ""
        };
    }

    private async Task<JsonNode> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters)
        };

        var content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(_config.Url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty MCP response.");
        if (node["error"] is JsonNode err)
            throw new InvalidOperationException(err.ToJsonString());
        return node["result"] ?? new JsonObject();
    }

    private async Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters)
        };

        var content = new StringContent(notification.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(_config.Url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}