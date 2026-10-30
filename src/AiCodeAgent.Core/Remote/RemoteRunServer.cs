using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Remote;

/// <summary>
/// E7-S3 prototype of <c>aiagent serve</c>: streams the events of one flow run to any number of authenticated WebSocket
/// clients and takes their answers (tool approvals, step gates, human steps, cancel) back. A client that connects late first
/// receives everything that happened so far. Built on <see cref="HttpListener"/> to keep the prototype dependency-free;
/// the ADR recommends Kestrel (TLS, limits) for a product version.
/// </summary>
public sealed class RemoteRunServer : IAsyncDisposable
{
    private sealed record BacklogItem(string Json, string? RequestId, bool IsRequest);

    private sealed class Pending
    {
        public required string NodeId { get; init; }
        public ApprovalRequestEvent? Approval { get; init; }
        public FlowHumanRequestEvent? Human { get; init; }
    }

    private sealed class Session
    {
        public required WebSocket Socket { get; init; }
        public required string Endpoint { get; init; }
        public Channel<string> Outbox { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    }

    /// <summary>Messages kept for late joiners. A run that produces more drops the oldest (streamed text first in a real version).</summary>
    public const int MaxBacklog = 20_000;

    private readonly HttpListener _listener = new();
    private readonly string _token;
    private readonly RemoteHello _hello;
    private readonly TimeSpan _approvalTimeout;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly List<BacklogItem> _backlog = new();
    private readonly List<Session> _sessions = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _cancelRun = new();
    private Task? _acceptLoop;
    private int _nextId;
    private int _clientsEver;

    /// <param name="hello">The run: id, flow definition, task.</param>
    /// <param name="token">The bearer token clients must present.</param>
    /// <param name="host">Address to listen on; loopback by default.</param>
    /// <param name="port">0 = pick a free port.</param>
    /// <param name="approvalTimeout">How long a question waits for a client before it is denied; null or zero = wait forever.</param>
    public RemoteRunServer(RemoteHello hello, string token, string host = "127.0.0.1", int port = 0, TimeSpan? approvalTimeout = null, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("A token is required.", nameof(token));
        _hello = hello;
        _token = token;
        _approvalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(10);
        _logger = logger;
        Host = host;
        Port = port == 0 ? FreePort(host) : port;
    }

    public string Host { get; }
    public int Port { get; }
    public Uri Url => new($"ws://{Host}:{Port}{RemoteProtocol.EventsPath}");

    /// <summary>Cancelled when a client asks to stop the run.</summary>
    public CancellationToken RunCancelRequested => _cancelRun.Token;

    public int ClientCount { get { lock (_gate) return _sessions.Count; } }
    public int ClientsEver => Volatile.Read(ref _clientsEver);

    public void Start()
    {
        // HttpListener spells "every address" as "+"; an IPv6 literal needs brackets.
        var prefixHost = Host is "0.0.0.0" or "::" or "*" ? "+" : Host.Contains(':') ? $"[{Host}]" : Host;
        _listener.Prefixes.Add($"http://{prefixHost}:{Port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    // ------------------------------------------------------------------ publishing

    /// <summary>Sends an event to every client (and keeps it for those who connect later). Safe to call from the run's thread.</summary>
    public void Publish(AgentEvent evt)
    {
        var (nodeId, inner) = Unwrap(evt);
        string? requestId = null;
        Pending? pending = null;
        switch (inner)
        {
            case ApprovalRequestEvent approval:
                requestId = NewRequestId();
                pending = new Pending { NodeId = nodeId ?? "", Approval = approval };
                break;
            case FlowHumanRequestEvent human:
                requestId = NewRequestId();
                pending = new Pending { NodeId = human.NodeId, Human = human };
                break;
        }

        var message = RemoteEventCodec.Encode(evt, requestId);
        if (message == null) return;
        var json = message.ToJsonString();

        lock (_gate)
        {
            if (pending != null) _pending[requestId!] = pending;
            AddToBacklog(new BacklogItem(json, requestId, IsRequest: pending != null));
            foreach (var s in _sessions) s.Outbox.Writer.TryWrite(json);
        }

        if (pending != null)
            WatchQuestion(requestId!, pending);
    }

    private void WatchQuestion(string id, Pending pending)
    {
        // Whoever completes the question (a client, the flow's own timeout, the end of the run), tell everybody it is closed.
        Task done = pending.Approval != null ? pending.Approval.Approval.Task : pending.Human!.Choice.Task;
        _ = done.ContinueWith(_ => CloseQuestion(id), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        if (_approvalTimeout > TimeSpan.Zero)
        {
            _ = Task.Delay(_approvalTimeout, _stop.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                bool stillOpen;
                lock (_gate) stillOpen = _pending.ContainsKey(id);
                if (!stillOpen) return;
                if (pending.Approval != null)
                {
                    pending.Approval.Feedback.DecidedBy = $"remote run: no client answered within {(int)_approvalTimeout.TotalSeconds}s (denied)";
                    pending.Approval.Approval.TrySetResult(false);
                }
                else
                {
                    pending.Human!.Choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
                }
            }, TaskScheduler.Default);
        }
    }

    private void CloseQuestion(string id)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.Remove(id, out pending)) return;
        }
        var json = RemoteEventCodec.Encode(new RemoteRequestResolvedEvent(id, pending.NodeId))!.ToJsonString();
        lock (_gate)
        {
            AddToBacklog(new BacklogItem(json, id, IsRequest: false));
            foreach (var s in _sessions) s.Outbox.Writer.TryWrite(json);
        }
    }

    private void AddToBacklog(BacklogItem item)
    {
        _backlog.Add(item);
        if (_backlog.Count > MaxBacklog) _backlog.RemoveRange(0, _backlog.Count - MaxBacklog);
    }

    /// <summary>Waits until a client has connected (for demos: start the flow once someone watches).</summary>
    public async Task<bool> WaitForClientAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            while (ClientsEver == 0)
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return ClientsEver > 0;
        }
    }

    /// <summary>After the run: give connected clients up to <paramref name="linger"/> to read the end of it, then close them.</summary>
    public async Task FinishAsync(TimeSpan linger)
    {
        var end = new JsonObject { ["type"] = "end" }.ToJsonString();
        lock (_gate)
        {
            foreach (var s in _sessions) s.Outbox.Writer.TryWrite(end);
        }
        var deadline = DateTime.UtcNow + linger;
        while (ClientCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ accepting clients

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";
            if (path == RemoteProtocol.HealthPath)
            {
                await Respond(context, 200, "ok").ConfigureAwait(false);
                return;
            }
            if (path != RemoteProtocol.EventsPath || !request.IsWebSocketRequest)
            {
                await Respond(context, 404, "Not found").ConfigureAwait(false);
                return;
            }
            if (!IsAuthorized(request))
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer");
                await Respond(context, 401, "Unauthorized").ConfigureAwait(false);
                _logger?.LogWarning("Rejected a connection from {Endpoint}: bad or missing token", request.RemoteEndPoint);
                return;
            }

            HttpListenerWebSocketContext socketContext;
            try { socketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or HttpListenerException or InvalidOperationException)
            {
                await Respond(context, 500, "WebSocket handshake failed").ConfigureAwait(false);
                return;
            }
            await RunSessionAsync(new Session { Socket = socketContext.WebSocket, Endpoint = request.RemoteEndPoint?.ToString() ?? "unknown" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "A remote client connection ended with an error");
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var supplied = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(supplied) && supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            supplied = supplied["Bearer ".Length..].Trim();
        else
            supplied = request.QueryString["token"];
        if (string.IsNullOrEmpty(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_token));
    }

    private static async Task Respond(HttpListenerContext context, int status, string body)
    {
        try
        {
            context.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task RunSessionAsync(Session session)
    {
        Interlocked.Increment(ref _clientsEver);
        var hello = new JsonObject
        {
            ["type"] = "hello",
            ["protocol"] = RemoteProtocol.Version,
            ["runId"] = _hello.RunId,
            ["flow"] = _hello.Flow,
            ["task"] = _hello.Task,
            ["startedAt"] = _hello.StartedAt,
            ["definition"] = JsonSerializer.SerializeToNode(_hello.Definition, SdlcPipelineLoader.FileJsonOptions)
        }.ToJsonString();

        // Hello, then everything so far, then live events: queued under one lock so nothing is missed or doubled.
        lock (_gate)
        {
            session.Outbox.Writer.TryWrite(hello);
            foreach (var item in _backlog)
            {
                // A closed question is not replayed: there is nothing left to answer.
                if (item.RequestId != null && (!item.IsRequest || !_pending.ContainsKey(item.RequestId))) continue;
                session.Outbox.Writer.TryWrite(item.Json);
            }
            _sessions.Add(session);
        }
        _logger?.LogInformation("Client connected: {Endpoint}", session.Endpoint);

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in session.Outbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    await RemoteSocket.SendTextAsync(session.Socket, message, _stop.Token).ConfigureAwait(false);
                    if (message.Contains("\"type\":\"end\"", StringComparison.Ordinal)) break;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException) { }
        });

        try
        {
            while (!_stop.IsCancellationRequested && session.Socket.State == WebSocketState.Open && !sender.IsCompleted)
            {
                var receive = RemoteSocket.ReceiveTextAsync(session.Socket, _stop.Token);
                var first = await Task.WhenAny(receive, sender).ConfigureAwait(false);
                if (first == sender) break;
                var text = await receive.ConfigureAwait(false);
                if (text == null) break;
                HandleClientMessage(session, text);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidDataException) { }
        finally
        {
            lock (_gate) _sessions.Remove(session);
            session.Outbox.Writer.TryComplete();
            try { await sender.ConfigureAwait(false); } catch { /* the send loop reports nothing */ }
            await RemoteSocket.CloseQuietlyAsync(session.Socket, "bye").ConfigureAwait(false);
            session.Socket.Dispose();
            _logger?.LogInformation("Client disconnected: {Endpoint}", session.Endpoint);
        }
    }

    private void HandleClientMessage(Session session, string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return; }
        using (doc)
        {
            var type = RemoteEventCodec.Str(doc.RootElement, "type");
            if (type == "cancel")
            {
                _logger?.LogInformation("Client {Endpoint} asked to stop the run", session.Endpoint);
                _cancelRun.Cancel();
                return;
            }
            var answer = RemoteEventCodec.DecodeAnswer(doc.RootElement);
            if (answer == null) return;
            Pending? pending;
            lock (_gate) _pending.TryGetValue(answer.RequestId, out pending);
            if (pending == null) return; // already answered by somebody else, or unknown
            var who = $"remote client {session.Endpoint}";
            if (answer.Kind == "answer" && pending.Approval != null)
            {
                pending.Approval.Feedback.Text = answer.Note;
                pending.Approval.Feedback.Skip = answer.Skip;
                pending.Approval.Feedback.DecidedBy = who;
                pending.Approval.Approval.TrySetResult(answer.Approved);
            }
            else if (answer.Kind == "human" && pending.Human != null)
            {
                pending.Human.Choice.TrySetResult(new FlowHumanChoice(answer.Outcome, answer.Note, answer.Stop));
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private string NewRequestId() => "q" + Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static (string? NodeId, AgentEvent Inner) Unwrap(AgentEvent evt)
    {
        var inner = evt is SessionScopedEvent s ? s.Inner : evt;
        return inner is AgentTaggedEvent t ? (t.AgentId, t.Inner) : (null, inner);
    }

    private static int FreePort(string host)
    {
        var listener = new TcpListener(IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_gate)
        {
            foreach (var s in _sessions) s.Outbox.Writer.TryComplete();
        }
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* stopped */ }
        }
        _listener.Close();
        _cancelRun.Dispose();
        _stop.Dispose();
    }
}
