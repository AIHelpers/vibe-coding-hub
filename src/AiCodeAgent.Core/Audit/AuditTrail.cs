using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Audit;

/// <summary>What a run is, written as its first entry.</summary>
/// <param name="Kind"><c>chat</c> or <c>flow</c>.</param>
public sealed record AuditRunInfo(
    string Kind,
    string RunId,
    string Name,
    string Task,
    string? Model = null,
    string? CharacterId = null,
    string? PermissionMode = null,
    string? WorkingDirectory = null);

/// <summary>
/// The append-only record of one run (E7-S2): who (user, character, model), what (tool calls with arguments and results,
/// approvals and denials and who decided, files changed with hashes), when, and how much (tokens, cost).
/// Feed it every event of the run with <see cref="Observe"/>; close it with <see cref="Complete"/>.
/// Every string is redacted (<see cref="SecretRedactor"/>) before it is written, and a failure to write never fails the run.
/// </summary>
public sealed class AuditTrail : IDisposable
{
    /// <summary>Tool output longer than this is cut in the file; its full SHA-256 and length are kept.</summary>
    public const int MaxOutputChars = 8_000;
    /// <summary>A diff longer than this is cut in the file; its full SHA-256 is kept.</summary>
    public const int MaxDiffChars = 100_000;

    private sealed record Actor(string? Agent, string? Character, string? Model, string? Provider);

    private readonly AuditChainWriter _writer;
    private readonly SecretRedactor _redactor;
    private readonly AuditRunInfo _info;
    private readonly string _user;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Actor> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Actor _runActor;
    private AgentResponse? _finished;
    private decimal _lastCostUsd;
    private bool _closed;

    private AuditTrail(AuditChainWriter writer, AuditRunInfo info, SecretRedactor redactor, string user, ILogger? logger)
    {
        _writer = writer;
        _info = info;
        _redactor = redactor;
        _user = user;
        _logger = logger;
        _runActor = new Actor(null, info.CharacterId, info.Model, null);
    }

    /// <summary>Starts the file and writes the <c>run.start</c> entry. Returns null (and logs) when the file cannot be created.</summary>
    public static AuditTrail? TryOpen(string path, AuditRunInfo info, SecretRedactor? redactor = null, string? user = null, ILogger? logger = null)
    {
        try { return Open(path, info, redactor, user, logger); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            logger?.LogWarning(ex, "Could not start the audit trail {Path}", path);
            return null;
        }
    }

    public static AuditTrail Open(string path, AuditRunInfo info, SecretRedactor? redactor = null, string? user = null, ILogger? logger = null)
    {
        var trail = new AuditTrail(new AuditChainWriter(path), info, redactor ?? SecretRedactor.FromEnvironment(), user ?? DefaultUser(), logger);
        trail.Record("run.start", null, new JsonObject
        {
            ["runKind"] = info.Kind,
            ["runId"] = info.RunId,
            ["name"] = info.Name,
            ["task"] = info.Task,
            ["workingDirectory"] = info.WorkingDirectory,
            ["permissionMode"] = info.PermissionMode,
            ["machine"] = Environment.MachineName
        });
        return trail;
    }

    /// <summary>
    /// Starts the audit file of one chat task under <c>~/.aiagent/runs/</c> (<c>&lt;time&gt;-&lt;session&gt;.audit.jsonl</c>).
    /// Returns null when it cannot be created: a task never fails because of its audit trail.
    /// </summary>
    public static AuditTrail? OpenChat(string sessionId, string message, AgentOptions options, ILogger? logger = null, string? runsRoot = null)
    {
        var root = runsRoot ?? AiCodeAgent.Core.Configuration.AppPaths.Runs;
        var started = DateTime.UtcNow;
        var path = AuditLocator.ChatFilePath(root, started, sessionId);
        for (var i = 2; File.Exists(path); i++)
            path = AuditLocator.ChatFilePath(root, started, sessionId, i);
        return TryOpen(path, new AuditRunInfo("chat", AuditLocator.Id(path), "chat task", message, options.Model, options.CharacterId,
            options.PermissionMode.ToString(), options.WorkingDirectory), logger: logger);
    }

    public static string DefaultUser()
    {
        try { return string.IsNullOrWhiteSpace(Environment.UserName) ? "unknown" : Environment.UserName; }
        catch (InvalidOperationException) { return "unknown"; }
    }

    public string Path => _writer.Path;
    public string User => _user;
    /// <summary>Hash of the last line: store it elsewhere to also detect a tampered or cut-off tail.</summary>
    public string HeadHash => _writer.HeadHash;
    public long EntryCount => _writer.Count;

    // ------------------------------------------------------------------ explicit entries

    /// <summary>A flow step starts: remember who runs it so its events carry the character and model.</summary>
    public void StepStarted(string nodeId, int round, string? characterId, string? model, string? provider)
    {
        lock (_gate) _actors[nodeId] = new Actor(nodeId, characterId, model, provider);
        Record("step.start", nodeId, new JsonObject { ["step"] = nodeId, ["round"] = round });
    }

    /// <summary>A flow step finished (any way it can).</summary>
    public void StepFinished(string nodeId, int round, string state, string? outcome, string? detail, int tokens, decimal costUsd, TimeSpan duration, int toolCalls, int filesChanged)
    {
        Record("step.end", nodeId, new JsonObject
        {
            ["step"] = nodeId,
            ["round"] = round,
            ["state"] = state,
            ["outcome"] = outcome,
            ["detail"] = detail,
            ["tokens"] = tokens,
            ["costUsd"] = costUsd,
            ["durationMs"] = (long)duration.TotalMilliseconds,
            ["toolCalls"] = toolCalls,
            ["filesChanged"] = filesChanged
        });
    }

    /// <summary>Any other entry (flow edges, notes). <paramref name="data"/> is redacted like everything else.</summary>
    public void Record(string kind, string? agentId, JsonObject data)
    {
        try
        {
            var entry = new JsonObject
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["kind"] = kind,
                ["user"] = _user
            };
            Actor actor;
            lock (_gate) actor = agentId != null && _actors.TryGetValue(agentId, out var a) ? a : _runActor;
            if (agentId != null) entry["agent"] = agentId;
            if (actor.Character != null) entry["character"] = actor.Character;
            if (actor.Model != null) entry["model"] = actor.Model;
            if (actor.Provider != null) entry["provider"] = actor.Provider;
            entry["data"] = data;
            RedactNode(entry);
            var json = entry.ToJsonString();
            lock (_gate)
            {
                if (_closed) return;
                _writer.Append(json);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not write an audit entry ({Kind})", kind);
        }
    }

    /// <summary>Writes the closing <c>run.end</c> entry and closes the file. Later calls do nothing.</summary>
    public void Complete(string status, string? message = null, TimeSpan? duration = null, int? tokens = null, decimal? costUsd = null)
    {
        lock (_gate) if (_closed) return;
        var t = _finished?.TotalUsage;
        Record("run.end", null, new JsonObject
        {
            ["status"] = status,
            ["message"] = message,
            ["durationMs"] = (long)(duration ?? _finished?.Duration ?? TimeSpan.Zero).TotalMilliseconds,
            ["tokens"] = tokens ?? (t == null ? 0 : t.PromptTokens + t.CompletionTokens + t.CacheReadTokens + t.CacheCreationTokens),
            ["costUsd"] = costUsd ?? _lastCostUsd,
            ["entries"] = _writer.Count + 1
        });
        Close();
    }

    public void Dispose() => Close();

    private void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _writer.Dispose();
        }
    }

    // ------------------------------------------------------------------ events

    /// <summary>Records what an agent event says about the run. Events the audit does not need are ignored.</summary>
    public void Observe(AgentEvent evt) => Observe(evt, null);

    private void Observe(AgentEvent evt, string? agentId)
    {
        switch (evt)
        {
            case SessionScopedEvent scoped:
                Observe(scoped.Inner, agentId);
                break;
            case AgentTaggedEvent tagged:
                Observe(tagged.Inner, tagged.AgentId);
                break;
            case ToolCallStartEvent start:
                Record("tool.call", agentId, new JsonObject
                {
                    ["id"] = start.Call.Id,
                    ["tool"] = start.Call.Name,
                    ["arguments"] = ToNode(start.Call.Arguments)
                });
                break;
            case ToolCallEndEvent end:
                Record("tool.result", agentId, ToolResultData(end));
                break;
            case ApprovalRequestEvent approval:
                RecordApproval(approval, agentId);
                break;
            case DiffProducedEvent diff:
                Record("file.change", agentId ?? diff.Diff.AgentId, FileChangeData(diff.Diff));
                break;
            case CheckpointCreatedEvent checkpoint:
                Record("checkpoint", agentId, new JsonObject
                {
                    ["id"] = checkpoint.Checkpoint.CheckpointId,
                    ["path"] = checkpoint.Checkpoint.FilePath,
                    ["originalSha256"] = AuditChain.Sha256(checkpoint.Checkpoint.OriginalContent ?? string.Empty)
                });
                break;
            case TokenUsageEvent usage:
                lock (_gate) _lastCostUsd = usage.SessionCostUsd;
                Record("usage", agentId, new JsonObject
                {
                    ["promptTokens"] = usage.Usage.PromptTokens,
                    ["completionTokens"] = usage.Usage.CompletionTokens,
                    ["cacheReadTokens"] = usage.Usage.CacheReadTokens,
                    ["cacheCreationTokens"] = usage.Usage.CacheCreationTokens,
                    ["sessionCostUsd"] = usage.SessionCostUsd
                });
                break;
            case AgentFinishedEvent finished when agentId == null:
                lock (_gate) _finished = finished.Response;
                break;
            case AgentErrorEvent error:
                Record("error", agentId, new JsonObject { ["message"] = error.Error.Message, ["type"] = error.Error.GetType().Name });
                break;
        }
    }

    private void RecordApproval(ApprovalRequestEvent approval, string? agentId)
    {
        var id = approval.Call.Id;
        var tool = approval.Call.Name;
        Record("approval.request", agentId, new JsonObject
        {
            ["id"] = id,
            ["tool"] = tool,
            ["risk"] = approval.Risk.ToString(),
            ["arguments"] = ToNode(approval.Call.Arguments)
        });
        var feedback = approval.Feedback;
        // The answer arrives later (a dialog, a prompt, a timeout). Write it on the thread that sets it, so the log keeps the real order.
        _ = approval.Approval.Task.ContinueWith(t =>
        {
            var answered = t.Status == TaskStatus.RanToCompletion;
            var approved = answered && t.Result;
            Record("approval.decision", agentId, new JsonObject
            {
                ["id"] = id,
                ["tool"] = tool,
                ["decision"] = approved ? "approved" : answered ? "denied" : "cancelled",
                ["decidedBy"] = feedback.DecidedBy ?? _user,
                ["note"] = feedback.HasText ? feedback.Text : null
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static JsonObject ToolResultData(ToolCallEndEvent end)
    {
        var output = end.Result.Content ?? string.Empty;
        var data = new JsonObject
        {
            ["id"] = end.Call.Id,
            ["tool"] = end.Call.Name,
            ["isError"] = end.Result.IsError,
            ["durationMs"] = (long)end.Duration.TotalMilliseconds,
            ["outputChars"] = output.Length,
            ["outputSha256"] = AuditChain.Sha256(output)
        };
        if (output.Length > MaxOutputChars)
        {
            data["output"] = output[..MaxOutputChars];
            data["outputTruncated"] = true;
        }
        else
        {
            data["output"] = output;
        }
        return data;
    }

    private static JsonObject FileChangeData(DiffEntry diff)
    {
        var data = new JsonObject
        {
            ["path"] = diff.FilePath,
            ["beforeSha256"] = AuditChain.Sha256(diff.OriginalContent ?? string.Empty),
            ["afterSha256"] = AuditChain.Sha256(diff.ModifiedContent ?? string.Empty),
            ["created"] = string.IsNullOrEmpty(diff.OriginalContent),
            ["diffSha256"] = AuditChain.Sha256(diff.DiffText ?? string.Empty)
        };
        var text = diff.DiffText ?? string.Empty;
        if (text.Length > MaxDiffChars)
        {
            data["diff"] = text[..MaxDiffChars];
            data["diffTruncated"] = true;
        }
        else
        {
            data["diff"] = text;
        }
        return data;
    }

    private static JsonNode? ToNode(object? value)
    {
        try { return JsonSerializer.SerializeToNode(value); }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonValue.Create(value?.ToString());
        }
    }

    // ------------------------------------------------------------------ redaction

    private void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                        obj[key] = JsonValue.Create(_redactor.Redact(s));
                    else
                        RedactNode(child);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                        arr[i] = JsonValue.Create(_redactor.Redact(s));
                    else
                        RedactNode(child);
                }
                break;
        }
    }
}
