using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AiCodeAgent.Tools.Acp;

/// <summary>An error object of a JSON-RPC response (or one to send back).</summary>
public sealed class AcpRpcException : Exception
{
    public AcpRpcException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int AuthRequired = -32000;
}

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON, as ACP uses on stdio. Both sides can send requests and notifications;
/// this class is used by the client (VCH) and, in tests, by the fake agent.
/// </summary>
internal sealed class AcpConnection : IAsyncDisposable
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _onRequest;
    private readonly Action<string, JsonElement> _onNotification;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private long _nextId;

    public AcpConnection(TextReader fromPeer, TextWriter toPeer,
        Func<string, JsonElement, CancellationToken, Task<object?>> onRequest,
        Action<string, JsonElement> onNotification)
    {
        _in = fromPeer;
        _out = toPeer;
        _onRequest = onRequest;
        _onNotification = onNotification;
    }

    /// <summary>Completes when the peer closed its output (the agent exited or hung up).</summary>
    public Task Closed => _reader;

    public void Start() => _reader = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = ToNode(parameters) }).ConfigureAwait(false);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = ToNode(parameters) });

    private static JsonNode? ToNode(object? value) => value == null ? null : JsonSerializer.SerializeToNode(value);

    private async Task WriteAsync(JsonObject message)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _out.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while (!_stop.IsCancellationRequested && (line = await _in.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue; // agents may print banners to stdout; only JSON objects count
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                Dispatch(doc);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            foreach (var p in _pending.Values) p.TrySetException(new IOException("The agent closed the connection."));
        }
    }

    private void Dispatch(JsonDocument doc)
    {
        var root = doc.RootElement;
        var hasMethod = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String;
        var hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null;

        if (hasMethod && hasId)
        {
            var method = methodEl.GetString()!;
            var id = idEl.Clone();
            var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            doc.Dispose();
            _ = Task.Run(async () =>
            {
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
                try
                {
                    response["result"] = ToNode(await _onRequest(method, args, _stop.Token).ConfigureAwait(false)) ?? new JsonObject();
                }
                catch (AcpRpcException ex)
                {
                    response["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message };
                }
                catch (Exception ex)
                {
                    response["error"] = new JsonObject { ["code"] = AcpRpcException.InternalError, ["message"] = ex.Message };
                }
                try { await WriteAsync(response).ConfigureAwait(false); } catch (IOException) { } catch (ObjectDisposedException) { }
            });
        }
        else if (hasMethod)
        {
            var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            var method = methodEl.GetString()!;
            doc.Dispose();
            try { _onNotification(method, args); } catch { /* a bad notification must not kill the connection */ }
        }
        else if (hasId && idEl.TryGetInt64(out var rid) && _pending.TryGetValue(rid, out var tcs))
        {
            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : AcpRpcException.InternalError;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error";
                tcs.TrySetException(new AcpRpcException(code, msg));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            }
            doc.Dispose();
        }
        else
        {
            doc.Dispose();
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // the runner and a test double may both dispose it
        _stop.Cancel();
        foreach (var p in _pending.Values) p.TrySetCanceled();
        await Task.WhenAny(_reader, Task.Delay(100)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
