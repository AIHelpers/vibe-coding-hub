using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Mcp;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Mcp;

/// <summary>
/// MCP client that communicates with a local MCP server over stdio (JSON-RPC 2.0).
/// Launches the server process, sends requests on stdin, and reads responses from stdout.
/// </summary>
public class StdioMcpClient : IMcpClient
{
    private readonly McpServerConfig _config;
    private readonly ILogger<StdioMcpClient> _logger;
    private Process? _process;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _nextRequestId;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerTask;

    public string ServerName => _config.Name;
    public bool IsConnected => _process is not null && !_process.HasExited;

    public StdioMcpClient(McpServerConfig config, ILogger<StdioMcpClient>? logger = null)
    {
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StdioMcpClient>.Instance;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.Command))
            throw new InvalidOperationException($"MCP server '{_config.Name}' has no command (use Url for remote servers).");

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in _config.Args)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in _config.Env)
            psi.Environment[key] = value;

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start MCP server '{_config.Name}'.");

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogDebug("[{Name}] stderr: {Data}", _config.Name, e.Data);
        };
        _process.BeginErrorReadLine();

        _readerTask = Task.Run(() => ReaderLoop(_readerCts.Token), _readerCts.Token);

        // Initialize handshake
        await SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "AiCodeAgent", version = "1.0" }
        }, cancellationToken).ConfigureAwait(false);

        // Send initialized notification
        await SendNotificationAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("MCP stdio server '{Name}' connected", _config.Name);
    }

    public async Task DisconnectAsync()
    {
        _readerCts.Cancel();
        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
                _process.WaitForExit(2000);
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
        }
        _process?.Dispose();
        _process = null;

        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); } catch { /* ignored */ }
        }

        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();
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
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters)
        };

        await WriteAsync(request, cancellationToken).ConfigureAwait(false);

        using var ctr = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters)
        };
        await WriteAsync(notification, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(JsonNode node, CancellationToken cancellationToken)
    {
        if (_process is null) throw new InvalidOperationException("MCP client not connected.");
        var json = node.ToJsonString();
        var line = json + "\n";

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReaderLoop(CancellationToken cancellationToken)
    {
        if (_process is null) return;
        var reader = _process.StandardOutput;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { break; }
            if (line is null) break;

            try
            {
                var node = JsonNode.Parse(line);
                if (node is null) continue;

                // Response (has id)
                if (node["id"] is JsonValue idVal && idVal.TryGetValue<int>(out var id))
                {
                    if (_pending.TryGetValue(id, out var tcs))
                    {
                        _pending.Remove(id);
                        if (node["error"] is JsonNode err)
                            tcs.TrySetException(new InvalidOperationException(err.ToJsonString()));
                        else if (node["result"] is JsonNode result)
                            tcs.TrySetResult(result);
                        else
                            tcs.TrySetException(new InvalidOperationException("MCP response missing result."));
                    }
                }
                // Notification (no id) – ignored for now
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse MCP message from '{Name}'", _config.Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _readerCts.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}