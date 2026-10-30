using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Mcp;

namespace AiCodeAgent.Tools.Acp;

/// <summary>The two ends of an agent: what it writes (its stdout) and what it reads (its stdin).</summary>
internal interface IAcpTransport : IAsyncDisposable
{
    TextReader FromAgent { get; }
    TextWriter ToAgent { get; }
    /// <summary>The last lines the agent wrote to stderr, for error messages.</summary>
    string StderrTail { get; }
}

/// <summary>
/// Prototype (E6-S5): drives one external Agent Client Protocol agent through a single prompt turn.
/// initialize → session/new → session/prompt, collecting <c>agent_message_chunk</c> updates, answering
/// <c>session/request_permission</c> from the flow's permission mode and serving <c>fs/*</c> inside the working directory.
/// Protocol version 1 (the version the shipping agents implement).
/// </summary>
public sealed class AcpAgentRunner : IExternalAgentRunner
{
    public const int ProtocolVersion = 1;

    private readonly Func<ExternalAgentRequest, IAcpTransport> _launch;

    public AcpAgentRunner() : this(LaunchProcess) { }

    internal AcpAgentRunner(Func<ExternalAgentRequest, IAcpTransport> launch) => _launch = launch;

    public async Task<ExternalAgentResult> RunAsync(ExternalAgentRequest request, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command)) throw new ExternalAgentException("No command is set for the ACP step.");

        IAcpTransport transport;
        try { transport = _launch(request); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            throw new ExternalAgentException($"Could not start '{request.Command}': {ex.Message}. Is it installed and on PATH?", ex);
        }

        await using var _ = transport;
        var turn = new TurnState(request, onProgress);
        await using var connection = new AcpConnection(transport.FromAgent, transport.ToAgent, turn.HandleRequestAsync, turn.HandleNotification);
        turn.Connection = connection;
        connection.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            var init = await connection.RequestAsync("initialize", new
            {
                protocolVersion = ProtocolVersion,
                clientCapabilities = new { fs = new { readTextFile = true, writeTextFile = true }, terminal = false },
                clientInfo = new { name = "vibe-coding-hub", title = "Vibe Coding Hub", version = "1.0" }
            }, timeout.Token).ConfigureAwait(false);

            var agentVersion = init.ValueKind == JsonValueKind.Object && init.TryGetProperty("protocolVersion", out var pv) && pv.TryGetInt32(out var v) ? v : 0;
            if (agentVersion != ProtocolVersion)
                throw new ExternalAgentException($"The agent speaks ACP version {agentVersion}; this build supports version {ProtocolVersion}.");

            var session = await connection.RequestAsync("session/new", new { cwd = Path.GetFullPath(request.WorkingDirectory), mcpServers = Array.Empty<object>() }, timeout.Token).ConfigureAwait(false);
            var sessionId = session.ValueKind == JsonValueKind.Object && session.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
            if (string.IsNullOrEmpty(sessionId)) throw new ExternalAgentException("The agent did not return a session id.");
            turn.SessionId = sessionId;

            var response = await connection.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new[] { new { type = "text", text = request.Prompt } }
            }, timeout.Token).ConfigureAwait(false);

            var stop = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("stopReason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";
            return new ExternalAgentResult(turn.FinalMessage(), stop, turn.ToolCalls, turn.Permissions.ToList());
        }
        catch (OperationCanceledException)
        {
            // The user stopped the flow (or the step timed out): ask the agent to stop, then give up.
            if (turn.SessionId != null)
            {
                try { await connection.NotifyAsync("session/cancel", new { sessionId = turn.SessionId }).ConfigureAwait(false); } catch { }
            }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new ExternalAgentException($"The agent did not finish within {request.Timeout.TotalSeconds:0} seconds.");
        }
        catch (AcpRpcException ex) when (ex.Code == AcpRpcException.AuthRequired)
        {
            throw new ExternalAgentException($"The agent needs you to sign in first ({ex.Message}). Run its login command once in a terminal, then retry.", ex);
        }
        catch (AcpRpcException ex)
        {
            throw new ExternalAgentException($"The agent returned an error: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            var tail = transport.StderrTail;
            throw new ExternalAgentException("The agent exited before it answered." + (string.IsNullOrWhiteSpace(tail) ? string.Empty : " " + tail.Trim()), ex);
        }
    }

    /// <summary>Everything that happens while one prompt turn runs.</summary>
    private sealed class TurnState
    {
        private readonly ExternalAgentRequest _request;
        private readonly Action<string>? _progress;
        private readonly StringBuilder _all = new();
        private readonly StringBuilder _sinceLastTool = new();
        private readonly object _gate = new();
        private readonly List<ExternalAgentPermission> _permissions = new();
        private int _toolCalls;

        public TurnState(ExternalAgentRequest request, Action<string>? progress)
        {
            _request = request;
            _progress = progress;
        }

        public AcpConnection? Connection { get; set; }
        public string? SessionId { get; set; }
        public int ToolCalls { get { lock (_gate) return _toolCalls; } }
        public IReadOnlyList<ExternalAgentPermission> Permissions { get { lock (_gate) return _permissions.ToList(); } }

        /// <summary>The text after the agent's last tool call; when it said nothing after it, everything it said.</summary>
        public string FinalMessage()
        {
            lock (_gate)
            {
                var tail = _sinceLastTool.ToString().Trim();
                return tail.Length > 0 ? tail : _all.ToString().Trim();
            }
        }

        public void HandleNotification(string method, JsonElement args)
        {
            if (method != "session/update" || !args.TryGetProperty("update", out var update)) return;
            var kind = update.TryGetProperty("sessionUpdate", out var k) ? k.GetString() : null;
            switch (kind)
            {
                case "agent_message_chunk":
                    if (update.TryGetProperty("content", out var content) && content.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && content.TryGetProperty("text", out var text))
                    {
                        lock (_gate)
                        {
                            _all.Append(text.GetString());
                            _sinceLastTool.Append(text.GetString());
                        }
                    }
                    break;
                case "tool_call":
                    lock (_gate)
                    {
                        _toolCalls++;
                        _sinceLastTool.Clear();
                    }
                    _progress?.Invoke("ACP tool: " + (update.TryGetProperty("title", out var title) ? title.GetString() : "(unnamed)"));
                    break;
            }
        }

        public async Task<object?> HandleRequestAsync(string method, JsonElement args, CancellationToken ct)
        {
            switch (method)
            {
                case "session/request_permission":
                    return RequestPermission(args);
                case "fs/read_text_file":
                    return await ReadFileAsync(args, ct).ConfigureAwait(false);
                case "fs/write_text_file":
                    return await WriteFileAsync(args, ct).ConfigureAwait(false);
                default:
                    throw new AcpRpcException(AcpRpcException.MethodNotFound, $"Method not supported: {method}");
            }
        }

        private object RequestPermission(JsonElement args)
        {
            var toolCall = args.TryGetProperty("toolCall", out var tc) ? tc : default;
            var kind = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("kind", out var k) ? k.GetString() ?? "other" : "other";
            var title = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("title", out var ti) ? ti.GetString() ?? kind : kind;
            var options = args.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Select(x => new AcpPermissionOption(
                    x.TryGetProperty("optionId", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    x.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    x.TryGetProperty("kind", out var kd) ? kd.GetString() ?? string.Empty : string.Empty)).ToList()
                : new List<AcpPermissionOption>();

            var chosen = AcpPermissionPolicy.Choose(_request.PermissionMode, kind, options);
            var allowed = AcpPermissionPolicy.Allows(_request.PermissionMode, kind) && chosen != null && chosen.Kind.StartsWith("allow", StringComparison.Ordinal);
            lock (_gate) _permissions.Add(new ExternalAgentPermission(title, kind, allowed));
            _progress?.Invoke($"ACP permission {(allowed ? "granted" : "refused")}: {title} ({kind})");
            return chosen == null
                ? new { outcome = new { outcome = "cancelled" } }
                : new { outcome = new { outcome = "selected", optionId = chosen.OptionId } };
        }

        private string Confine(JsonElement args)
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(path)) throw new AcpRpcException(AcpRpcException.InvalidParams, "path is required");
            var root = Path.GetFullPath(_request.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.Equals(root, cmp) && !full.StartsWith(root + Path.DirectorySeparatorChar, cmp))
                throw new AcpRpcException(AcpRpcException.InvalidParams, "Path is outside the working directory.");
            return full;
        }

        private async Task<object> ReadFileAsync(JsonElement args, CancellationToken ct)
        {
            var full = Confine(args);
            if (!File.Exists(full)) throw new AcpRpcException(AcpRpcException.InvalidParams, "File not found.");
            var lines = await File.ReadAllLinesAsync(full, ct).ConfigureAwait(false);
            var start = args.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? Math.Max(1, li) : 1;
            var limit = args.TryGetProperty("limit", out var lm) && lm.TryGetInt32(out var lmi) ? Math.Max(0, lmi) : int.MaxValue;
            return new { content = string.Join("\n", lines.Skip(start - 1).Take(limit)) };
        }

        private async Task<object> WriteFileAsync(JsonElement args, CancellationToken ct)
        {
            // Writing is an edit: the flow's mode must allow edits (the same rule as for permission requests).
            if (!AcpPermissionPolicy.Allows(_request.PermissionMode, "edit"))
                throw new AcpRpcException(AcpRpcException.InvalidParams, "Writing files is not allowed in this permission mode.");
            var full = Confine(args);
            var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            _progress?.Invoke("ACP wrote " + Path.GetFileName(full));
            return new { };
        }
    }

    // ------------------------------------------------------------------ process transport

    private static IAcpTransport LaunchProcess(ExternalAgentRequest request)
    {
        var (fileName, leading) = StdioMcpClient.ResolveLaunch(request.Command);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var a in leading) psi.ArgumentList.Add(a);
        foreach (var a in request.Args) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("The process did not start.");
        return new ProcessTransport(process);
    }

    private sealed class ProcessTransport : IAcpTransport
    {
        private readonly Process _process;
        private readonly Queue<string> _stderr = new();

        public ProcessTransport(Process process)
        {
            _process = process;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > 10) _stderr.Dequeue();
                }
            };
            process.BeginErrorReadLine();
        }

        public TextReader FromAgent => _process.StandardOutput;
        public TextWriter ToAgent => _process.StandardInput;
        public string StderrTail { get { lock (_stderr) return string.Join("\n", _stderr); } }

        public async ValueTask DisposeAsync()
        {
            try { _process.StandardInput.Close(); } catch { }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            catch { }
            _process.Dispose();
        }
    }
}
