using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Remote;

/// <summary>
/// E7-S3 prototype: the wire protocol of <c>aiagent serve</c>. One WebSocket (<see cref="EventsPath"/>), JSON text messages.
/// Server → client: <c>hello</c> (run id, flow definition, task), then <c>event</c> messages in order, <c>resolved</c> when a
/// question was answered, <c>end</c>. Client → server: <c>answer</c> (a tool approval or step gate), <c>human</c> (a human-step
/// choice), <c>cancel</c>. See docs/adr/0002-remote-runners.md.
/// </summary>
public static class RemoteProtocol
{
    public const int Version = 1;
    public const string EventsPath = "/events";
    public const string HealthPath = "/health";
    /// <summary>Largest message either side accepts.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>What a client learns when it connects: which run this is and the flow it runs (so the canvas can be drawn).</summary>
public sealed record RemoteHello(
    int Protocol, string RunId, string Flow, string Task, DateTimeOffset StartedAt, SdlcPipelineDefinition Definition);

/// <summary>
/// A question (tool approval, step gate, human step) was answered — by this or another client, by a timeout, or by the run
/// ending. Clients clear the matching "waiting for you" state.
/// </summary>
public sealed record RemoteRequestResolvedEvent(string RequestId, string? NodeId) : AgentEvent;

/// <summary>A client's answer to a question, as it travels back to the server.</summary>
/// <param name="Kind"><c>answer</c> (approval / gate) or <c>human</c>.</param>
public sealed record RemoteAnswer(
    string Kind, string RequestId, bool Approved = false, string? Note = null, bool Skip = false, string? Outcome = null, bool Stop = false);

internal static class RemoteSocket
{
    /// <summary>Reads one text message; null when the other side closed. Throws <see cref="InvalidDataException"/> for binary or oversized messages.</summary>
    public static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Only text messages are supported.");
            message.Write(buffer, 0, result.Count);
            if (message.Length > RemoteProtocol.MaxMessageBytes)
                throw new InvalidDataException("The message is too large.");
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellationToken);

    public static async Task CloseQuietlyAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The other side is already gone.
        }
    }
}
