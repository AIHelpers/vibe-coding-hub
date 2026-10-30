using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using AiCodeAgent.Tools.Acp;

namespace AiCodeAgent.Tools.Tests.Acp;

/// <summary>A line pipe: what one side writes, the other reads, one line at a time.</summary>
internal sealed class LinePipe
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    public TextReader Reader { get; }
    public TextWriter Writer { get; }
    public LinePipe()
    {
        Reader = new PipeReader(_channel.Reader);
        Writer = new PipeWriter(_channel.Writer);
    }
    public void Complete() => _channel.Writer.TryComplete();

    private sealed class PipeReader : TextReader
    {
        private readonly ChannelReader<string> _r;
        public PipeReader(ChannelReader<string> r) => _r = r;
        public override async Task<string?> ReadLineAsync()
        {
            try { return await _r.ReadAsync().ConfigureAwait(false); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class PipeWriter : TextWriter
    {
        private readonly ChannelWriter<string> _w;
        public PipeWriter(ChannelWriter<string> w) => _w = w;
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value)
        {
            _w.TryWrite(value ?? string.Empty);
            return Task.CompletedTask;
        }
        public override Task FlushAsync() => Task.CompletedTask;
        protected override void Dispose(bool disposing)
        {
            if (disposing) _w.TryComplete();
            base.Dispose(disposing);
        }
    }
}

/// <summary>An in-memory ACP agent: the tests script what it answers and what it asks of the client.</summary>
internal sealed class FakeAcpAgent : IAcpTransport
{
    private readonly LinePipe _toAgent = new();
    private readonly LinePipe _fromAgent = new();
    private readonly AcpConnection _connection;

    public FakeAcpAgent(Func<FakeAcpAgent, string, JsonElement, Task<object?>> onRequest)
    {
        _connection = new AcpConnection(_toAgent.Reader, _fromAgent.Writer,
            (method, args, _) => Handle(onRequest, method, args),
            (method, args) => { lock (Notifications) Notifications.Add(method); });
        _connection.Start();
    }

    public List<string> Requests { get; } = new();
    public List<string> Notifications { get; } = new();
    public AcpConnection Connection => _connection;

    private Task<object?> Handle(Func<FakeAcpAgent, string, JsonElement, Task<object?>> onRequest, string method, JsonElement args)
    {
        lock (Requests) Requests.Add(method);
        return onRequest(this, method, args);
    }

    /// <summary>The agent hangs up (as if it crashed).</summary>
    public void Hangup() => _fromAgent.Complete();

    public Task Update(object update) => _connection.NotifyAsync("session/update", new { sessionId = "s1", update });

    public Task Say(string text) => Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });

    public Task ToolCall(string title) => Update(new { sessionUpdate = "tool_call", toolCallId = Guid.NewGuid().ToString("N"), title, kind = "read", status = "pending" });

    public async Task<JsonElement> Ask(string kind, string title = "Do something") =>
        await _connection.RequestAsync("session/request_permission", new
        {
            sessionId = "s1",
            toolCall = new { toolCallId = "t1", title, kind },
            options = new object[]
            {
                new { optionId = "yes", name = "Allow", kind = "allow_once" },
                new { optionId = "always", name = "Always", kind = "allow_always" },
                new { optionId = "no", name = "Reject", kind = "reject_once" }
            }
        }, default).ConfigureAwait(false);

    public static object Initialize(int version = 1) => new { protocolVersion = version, agentCapabilities = new { }, authMethods = Array.Empty<object>() };

    /// <summary>The usual script: initialize, session/new, then <paramref name="prompt"/> for session/prompt.</summary>
    public static FakeAcpAgent Scripted(Func<FakeAcpAgent, JsonElement, Task<object?>> prompt, int version = 1) =>
        new((agent, method, args) => method switch
        {
            "initialize" => Task.FromResult<object?>(Initialize(version)),
            "session/new" => Task.FromResult<object?>(new { sessionId = "s1" }),
            "session/prompt" => prompt(agent, args),
            _ => throw new AcpRpcException(AcpRpcException.MethodNotFound, method)
        });

    public TextReader FromAgent => _fromAgent.Reader;
    public TextWriter ToAgent => _toAgent.Writer;
    public string StderrTail => "fake agent stderr";

    public async ValueTask DisposeAsync()
    {
        _toAgent.Complete();
        _fromAgent.Complete();
        await _connection.DisposeAsync();
    }
}
