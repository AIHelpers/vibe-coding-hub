using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Remote;

/// <summary>
/// Client side of <c>aiagent serve</c>: connects, reads the run's description, and exposes its events as a normal
/// <see cref="AgentEvent"/> stream. Questions in that stream answer themselves over the socket when the UI completes them.
/// </summary>
public sealed class RemoteRunClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly RemoteEventDecoder _decoder;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private RemoteRunClient(ClientWebSocket socket, RemoteHello hello)
    {
        _socket = socket;
        Hello = hello;
        _decoder = new RemoteEventDecoder(SendAnswerAsync);
    }

    /// <summary>What the server is running: flow, task and the full definition (so the canvas can be drawn).</summary>
    public RemoteHello Hello { get; }

    public static async Task<RemoteRunClient> ConnectAsync(Uri url, string? token, CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        try
        {
            if (!string.IsNullOrEmpty(token))
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);

            var text = await RemoteSocket.ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false)
                ?? throw new RemoteAgentException("The server closed the connection before it said hello.");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (RemoteEventCodec.Str(root, "type") != "hello")
                throw new RemoteAgentException("The server did not start with a hello message.");
            var protocol = root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            if (protocol != RemoteProtocol.Version)
                throw new RemoteAgentException($"Protocol mismatch: server speaks {protocol}, this client speaks {RemoteProtocol.Version}.");

            var definition = root.GetProperty("definition").Deserialize<SdlcPipelineDefinition>(SdlcPipelineLoader.FileJsonOptions)
                ?? throw new RemoteAgentException("The server sent no flow definition.");
            var hello = new RemoteHello(
                protocol,
                RemoteEventCodec.Str(root, "runId") ?? "",
                RemoteEventCodec.Str(root, "flow") ?? definition.Name,
                RemoteEventCodec.Str(root, "task") ?? "",
                root.TryGetProperty("startedAt", out var s) && s.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.UtcNow,
                definition);
            return new RemoteRunClient(socket, hello);
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            throw new RemoteAgentException("Could not connect: " + ex.Message, ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The run's events (backlog first). Ends when the run finishes, the server says <c>end</c>, or the connection drops.</summary>
    public async IAsyncEnumerable<AgentEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (_socket.State == WebSocketState.Open)
        {
            string? text;
            try { text = await RemoteSocket.ReceiveTextAsync(_socket, linked.Token).ConfigureAwait(false); }
            catch (WebSocketException) { yield break; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { yield break; }
            if (text == null) yield break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var type = RemoteEventCodec.Str(doc.RootElement, "type");
                if (type == "end") yield break;
                if (type != "event") continue;
                var evt = _decoder.Decode(doc.RootElement);
                if (evt == null) continue;
                yield return evt;
                if (Unwrap(evt) is FlowFinishedEvent) yield break;
            }
        }
    }

    /// <summary>Asks the server to stop the run it is executing.</summary>
    public Task CancelRunAsync() => SendAsync("{\"type\":\"cancel\"}");

    private Task SendAnswerAsync(RemoteAnswer answer) => SendAsync(RemoteEventCodec.EncodeAnswer(answer));

    private async Task SendAsync(string text)
    {
        if (_socket.State != WebSocketState.Open) return; // the run is gone; nobody is waiting for this answer
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await RemoteSocket.SendTextAsync(_socket, text, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _sendLock.Release(); }
    }

    private static AgentEvent Unwrap(AgentEvent evt)
    {
        var inner = evt is SessionScopedEvent s ? s.Inner : evt;
        return inner is AgentTaggedEvent t ? t.Inner : inner;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await RemoteSocket.CloseQuietlyAsync(_socket, "bye").ConfigureAwait(false);
        _socket.Dispose();
        _sendLock.Dispose();
        _lifetime.Dispose();
    }
}
