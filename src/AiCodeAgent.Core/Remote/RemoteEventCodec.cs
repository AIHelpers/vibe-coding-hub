using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Remote;

/// <summary>
/// Turns the events of a running flow into JSON messages and back. Only the events a viewer needs are sent (flow and node
/// state, output text, tool starts, usage, questions); the rest stay on the server (the audit trail has everything).
/// </summary>
public static class RemoteEventCodec
{
    /// <summary>The JSON message for an event, or null when the event is not sent. <paramref name="requestId"/> is required for questions.</summary>
    public static JsonObject? Encode(AgentEvent evt, string? requestId = null)
    {
        var inner = evt;
        if (inner is SessionScopedEvent scoped) inner = scoped.Inner;
        string? agent = null, role = null;
        if (inner is AgentTaggedEvent tagged)
        {
            agent = tagged.AgentId;
            role = tagged.Role;
            inner = tagged.Inner;
        }

        string kind;
        var data = new JsonObject();
        switch (inner)
        {
            case FlowStartedEvent s:
                kind = "flowStarted";
                data["flowName"] = s.FlowName;
                data["nodeIds"] = JsonSerializer.SerializeToNode(s.NodeIds, RemoteProtocol.Json);
                data["waves"] = JsonSerializer.SerializeToNode(s.Waves, RemoteProtocol.Json);
                break;
            case FlowNodeStatusEvent n:
                kind = "flowNodeStatus";
                data["nodeId"] = n.NodeId;
                data["state"] = n.State.ToString();
                data["iteration"] = n.Iteration;
                data["detail"] = n.Detail;
                data["outcome"] = n.Outcome;
                data["characterId"] = n.CharacterId;
                break;
            case FlowEdgeTakenEvent e:
                kind = "flowEdgeTaken";
                data["from"] = e.From;
                data["to"] = e.To;
                data["when"] = e.When;
                data["isLoop"] = e.IsLoop;
                data["loopCount"] = e.LoopCount;
                break;
            case FlowUsageEvent u:
                kind = "flowUsage";
                data["nodeId"] = u.NodeId;
                data["step"] = JsonSerializer.SerializeToNode(u.Step, RemoteProtocol.Json);
                data["node"] = JsonSerializer.SerializeToNode(u.Node, RemoteProtocol.Json);
                data["run"] = JsonSerializer.SerializeToNode(u.Run, RemoteProtocol.Json);
                data["budgetUsd"] = u.BudgetUsd;
                break;
            case FlowFinishedEvent f:
                kind = "flowFinished";
                data["result"] = JsonSerializer.SerializeToNode(f.Result, RemoteProtocol.Json);
                break;
            case TextDeltaEvent d:
                kind = "textDelta";
                data["delta"] = d.Delta;
                break;
            case ToolCallStartEvent t:
                kind = "toolCallStart";
                data["id"] = t.Call.Id;
                data["name"] = t.Call.Name;
                data["arguments"] = ToNode(t.Call.Arguments);
                break;
            case AgentFinishedEvent a:
                kind = "agentFinished";
                data["content"] = a.Response.Content;
                data["stopReason"] = a.Response.StopReason;
                data["wasCancelled"] = a.Response.WasCancelled;
                data["promptTokens"] = a.Response.TotalUsage.PromptTokens;
                data["completionTokens"] = a.Response.TotalUsage.CompletionTokens;
                break;
            case StatusUpdateEvent st:
                kind = "status";
                data["status"] = st.Status;
                data["detail"] = st.Detail;
                break;
            case AgentErrorEvent err:
                kind = "error";
                data["message"] = err.Error.Message;
                break;
            case ApprovalRequestEvent approval:
                if (requestId == null) throw new ArgumentException("A question needs a request id.", nameof(requestId));
                kind = "approvalRequest";
                data["requestId"] = requestId;
                data["callId"] = approval.Call.Id;
                data["tool"] = approval.Call.Name;
                data["arguments"] = ToNode(approval.Call.Arguments);
                data["risk"] = approval.Risk.ToString();
                break;
            case FlowHumanRequestEvent human:
                if (requestId == null) throw new ArgumentException("A question needs a request id.", nameof(requestId));
                kind = "humanRequest";
                data["requestId"] = requestId;
                data["nodeId"] = human.NodeId;
                data["prompt"] = human.Prompt;
                data["outcomes"] = JsonSerializer.SerializeToNode(human.Outcomes, RemoteProtocol.Json);
                break;
            case RemoteRequestResolvedEvent r:
                kind = "resolved";
                data["requestId"] = r.RequestId;
                data["nodeId"] = r.NodeId;
                break;
            default:
                return null;
        }

        var message = new JsonObject { ["type"] = "event", ["kind"] = kind };
        if (agent != null) message["agent"] = agent;
        if (role != null) message["role"] = role;
        message["data"] = data;
        return message;
    }

    public static string EncodeAnswer(RemoteAnswer answer)
    {
        var message = new JsonObject { ["type"] = answer.Kind, ["requestId"] = answer.RequestId };
        if (answer.Kind == "human")
        {
            message["outcome"] = answer.Outcome;
            message["stop"] = answer.Stop;
        }
        else
        {
            message["approved"] = answer.Approved;
            message["skip"] = answer.Skip;
        }
        message["note"] = answer.Note;
        return message.ToJsonString();
    }

    public static RemoteAnswer? DecodeAnswer(JsonElement message)
    {
        var type = Str(message, "type");
        var id = Str(message, "requestId");
        if (id == null || type is not ("answer" or "human")) return null;
        return new RemoteAnswer(type, id,
            Approved: Bool(message, "approved"), Note: Str(message, "note"), Skip: Bool(message, "skip"),
            Outcome: Str(message, "outcome"), Stop: Bool(message, "stop"));
    }

    internal static JsonNode? ToNode(object? value)
    {
        try { return JsonSerializer.SerializeToNode(value, RemoteProtocol.Json); }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonValue.Create(value?.ToString());
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Rebuilds events from their messages. Questions come back as real <see cref="ApprovalRequestEvent"/> /
/// <see cref="FlowHumanRequestEvent"/> objects: when the UI completes their task, the answer is sent to the server.
/// </summary>
public sealed class RemoteEventDecoder
{
    private readonly Func<RemoteAnswer, Task> _sendAnswer;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _approvals = new();
    private readonly Dictionary<string, TaskCompletionSource<FlowHumanChoice>> _humans = new();
    private readonly HashSet<string> _answeredByServer = new();

    public RemoteEventDecoder(Func<RemoteAnswer, Task> sendAnswer) => _sendAnswer = sendAnswer;

    /// <summary>The event for a message of type <c>event</c>; null for kinds this version does not know.</summary>
    public AgentEvent? Decode(JsonElement message)
    {
        var kind = RemoteEventCodec.Str(message, "kind");
        if (kind == null || !message.TryGetProperty("data", out var d)) return null;
        var agent = RemoteEventCodec.Str(message, "agent");
        var role = RemoteEventCodec.Str(message, "role");

        AgentEvent? evt = kind switch
        {
            "flowStarted" => new FlowStartedEvent(
                S(d, "flowName") ?? "",
                d.GetProperty("nodeIds").Deserialize<List<string>>(RemoteProtocol.Json) ?? new(),
                (d.GetProperty("waves").Deserialize<List<List<string>>>(RemoteProtocol.Json) ?? new())
                    .Select(w => (IReadOnlyList<string>)w).ToList()),
            "flowNodeStatus" => new FlowNodeStatusEvent(
                S(d, "nodeId") ?? "", Enum.Parse<FlowNodeState>(S(d, "state") ?? "Pending", ignoreCase: true),
                I(d, "iteration"), S(d, "detail"), S(d, "outcome"), S(d, "characterId")),
            "flowEdgeTaken" => new FlowEdgeTakenEvent(S(d, "from") ?? "", S(d, "to") ?? "", S(d, "when"), RemoteEventCodec.Bool(d, "isLoop"), I(d, "loopCount")),
            "flowUsage" => new FlowUsageEvent(
                S(d, "nodeId") ?? "",
                d.GetProperty("step").Deserialize<FlowStepRunUsage>(RemoteProtocol.Json)!,
                d.GetProperty("node").Deserialize<FlowUsageTotals>(RemoteProtocol.Json)!,
                d.GetProperty("run").Deserialize<FlowUsageTotals>(RemoteProtocol.Json)!,
                d.TryGetProperty("budgetUsd", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDecimal() : null),
            "flowFinished" => new FlowFinishedEvent(d.GetProperty("result").Deserialize<FlowRunResult>(RemoteProtocol.Json)!),
            "textDelta" => new TextDeltaEvent(S(d, "delta") ?? ""),
            "toolCallStart" => new ToolCallStartEvent(new ToolCall
            {
                Id = S(d, "id") ?? "",
                Name = S(d, "name") ?? "",
                Arguments = d.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                    ? args.Deserialize<Dictionary<string, object?>>(RemoteProtocol.Json) ?? new()
                    : new()
            }),
            "agentFinished" => new AgentFinishedEvent(new AgentResponse
            {
                Content = S(d, "content") ?? "",
                StopReason = S(d, "stopReason"),
                WasCancelled = RemoteEventCodec.Bool(d, "wasCancelled"),
                TotalUsage = new TokenUsage { PromptTokens = I(d, "promptTokens"), CompletionTokens = I(d, "completionTokens") }
            }),
            "status" => new StatusUpdateEvent(S(d, "status") ?? "", S(d, "detail")),
            "error" => new AgentErrorEvent(new RemoteAgentException(S(d, "message") ?? "error")),
            "approvalRequest" => DecodeApproval(d),
            "humanRequest" => DecodeHuman(d),
            "resolved" => Resolve(d),
            _ => null
        };

        if (evt == null) return null;
        return agent != null && evt is not RemoteRequestResolvedEvent ? new AgentTaggedEvent(evt, agent, role) : evt;
    }

    private AgentEvent DecodeApproval(JsonElement d)
    {
        var id = S(d, "requestId") ?? "";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new ToolCall
        {
            Id = S(d, "callId") ?? "",
            Name = S(d, "tool") ?? "",
            Arguments = d.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                ? args.Deserialize<Dictionary<string, object?>>(RemoteProtocol.Json) ?? new()
                : new()
        };
        var risk = Enum.TryParse<RiskLevel>(S(d, "risk"), true, out var r) ? r : RiskLevel.Write;
        var approval = new ApprovalRequestEvent(call, tcs, risk);
        lock (_gate) _approvals[id] = tcs;
        _ = tcs.Task.ContinueWith(t =>
        {
            lock (_gate) { if (_answeredByServer.Remove(id)) return Task.CompletedTask; _approvals.Remove(id); }
            var approved = t.Status == TaskStatus.RanToCompletion && t.Result;
            return SendSafe(new RemoteAnswer("answer", id, approved, approval.Feedback.Text, approval.Feedback.Skip));
        }, TaskScheduler.Default).Unwrap();
        return approval;
    }

    private AgentEvent DecodeHuman(JsonElement d)
    {
        var id = S(d, "requestId") ?? "";
        var tcs = new TaskCompletionSource<FlowHumanChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = d.TryGetProperty("outcomes", out var o) ? o.Deserialize<List<string>>(RemoteProtocol.Json) ?? new() : new List<string>();
        var human = new FlowHumanRequestEvent(S(d, "nodeId") ?? "", S(d, "prompt") ?? "", outcomes, tcs);
        lock (_gate) _humans[id] = tcs;
        _ = tcs.Task.ContinueWith(t =>
        {
            lock (_gate) { if (_answeredByServer.Remove(id)) return Task.CompletedTask; _humans.Remove(id); }
            var choice = t.Status == TaskStatus.RanToCompletion ? t.Result : new FlowHumanChoice(null, null, Stop: true);
            return SendSafe(new RemoteAnswer("human", id, Note: choice.Note, Outcome: choice.Outcome, Stop: choice.Stop));
        }, TaskScheduler.Default).Unwrap();
        return human;
    }

    /// <summary>The server says a question is closed: finish the local task without sending an answer back.</summary>
    private AgentEvent Resolve(JsonElement d)
    {
        var id = S(d, "requestId") ?? "";
        TaskCompletionSource<bool>? approval;
        TaskCompletionSource<FlowHumanChoice>? human;
        lock (_gate)
        {
            _approvals.Remove(id, out approval);
            _humans.Remove(id, out human);
            if ((approval != null && !approval.Task.IsCompleted) || (human != null && !human.Task.IsCompleted))
                _answeredByServer.Add(id);
        }
        approval?.TrySetResult(false);
        human?.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
        return new RemoteRequestResolvedEvent(id, S(d, "nodeId"));
    }

    private async Task SendSafe(RemoteAnswer answer)
    {
        try { await _sendAnswer(answer).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The connection is gone; the server denies unanswered questions after its timeout.
        }
    }

    private static string? S(JsonElement e, string name) => RemoteEventCodec.Str(e, name);
    private static int I(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>An error reported by the remote run (only its message travels).</summary>
public sealed class RemoteAgentException : Exception
{
    public RemoteAgentException(string message) : base(message) { }
    public RemoteAgentException(string message, Exception inner) : base(message, inner) { }
}
