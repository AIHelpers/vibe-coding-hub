using System.Net.WebSockets;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Remote;

namespace AiCodeAgent.Core.Tests.Remote;

/// <summary>E7-S3: a flow's events travel over a real loopback WebSocket and the questions come back answered.</summary>
public class RemoteRunTests
{
    private const string Token = "s3cret-token";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static SdlcPipelineDefinition Flow() => new()
    {
        Name = "demo",
        Stages = new List<SdlcStageDefinition>
        {
            new() { Id = "plan", Name = "Plan" },
            new() { Id = "build", Name = "Build" }
        },
        Edges = new List<FlowEdge> { new() { From = "plan", To = "build" } }
    };

    private static RemoteRunServer StartServer(TimeSpan? approvalTimeout = null)
    {
        var hello = new RemoteHello(RemoteProtocol.Version, "run-1", "demo", "do the thing", DateTimeOffset.UtcNow, Flow());
        var server = new RemoteRunServer(hello, Token, "127.0.0.1", 0, approvalTimeout ?? TimeSpan.FromSeconds(30));
        server.Start();
        return server;
    }

    private static FlowStartedEvent Started() =>
        new("demo", new[] { "plan", "build" }, new IReadOnlyList<string>[] { new[] { "plan" }, new[] { "build" } });

    private static async Task<AgentEvent> Next(IAsyncEnumerator<AgentEvent> events)
    {
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Wait), "the stream ended early");
        return events.Current;
    }

    private static FlowFinishedEvent Finished() =>
        new(new FlowRunResult(FlowRunStatus.Succeeded, "done", Array.Empty<FlowNodeRunSummary>()));

    [Fact]
    public async Task Late_client_gets_the_definition_the_history_and_the_live_events()
    {
        await using var server = StartServer();
        server.Publish(Started());
        server.Publish(new FlowNodeStatusEvent("plan", FlowNodeState.Running, 1, null, null, null));

        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        Assert.Equal("demo", client.Hello.Flow);
        Assert.Equal("do the thing", client.Hello.Task);
        Assert.Equal(new[] { "plan", "build" }, client.Hello.Definition.Stages.Select(s => s.Id).ToArray());

        await using var events = client.EventsAsync().GetAsyncEnumerator();
        Assert.IsType<FlowStartedEvent>(await Next(events));
        var running = Assert.IsType<FlowNodeStatusEvent>(await Next(events));
        Assert.Equal(FlowNodeState.Running, running.State);

        server.Publish(new FlowNodeStatusEvent("plan", FlowNodeState.Done, 1, null, "ok", null));
        server.Publish(Finished());
        var done = Assert.IsType<FlowNodeStatusEvent>(await Next(events));
        Assert.Equal(FlowNodeState.Done, done.State);
        Assert.IsType<FlowFinishedEvent>(await Next(events));
        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(Wait)); // the stream ends after the flow finished
    }

    [Fact]
    public async Task Streamed_text_keeps_its_node_tag()
    {
        await using var server = StartServer();
        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var events = client.EventsAsync().GetAsyncEnumerator();

        server.Publish(new AgentTaggedEvent(new TextDeltaEvent("hello"), "plan", "planner"));
        var tagged = Assert.IsType<AgentTaggedEvent>(await Next(events));
        Assert.Equal("plan", tagged.AgentId);
        Assert.Equal("planner", tagged.Role);
        Assert.Equal("hello", Assert.IsType<TextDeltaEvent>(tagged.Inner).Delta);
    }

    [Fact]
    public async Task A_wrong_or_missing_token_is_refused()
    {
        await using var server = StartServer();
        await Assert.ThrowsAsync<RemoteAgentException>(() => RemoteRunClient.ConnectAsync(server.Url, "wrong"));
        await Assert.ThrowsAsync<RemoteAgentException>(() => RemoteRunClient.ConnectAsync(server.Url, null));
        Assert.Equal(0, server.ClientsEver);

        // The token also works as a query parameter (browsers cannot set headers on a WebSocket).
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.Url + "?token=" + Token), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task An_approval_answered_in_the_client_completes_the_request_on_the_server()
    {
        await using var server = StartServer();
        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var events = client.EventsAsync().GetAsyncEnumerator();

        var call = new ToolCall { Id = "c1", Name = "run_command", Arguments = new() { ["command"] = "dotnet test" } };
        var request = new ApprovalRequestEvent(call, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), RiskLevel.Execute);
        server.Publish(new AgentTaggedEvent(request, "build"));

        var tagged = Assert.IsType<AgentTaggedEvent>(await Next(events));
        var remote = Assert.IsType<ApprovalRequestEvent>(tagged.Inner);
        Assert.Equal("run_command", remote.Call.Name);
        Assert.Equal("dotnet test", remote.Call.Arguments["command"]?.ToString());
        Assert.Equal(RiskLevel.Execute, remote.Risk);

        remote.Approval.TrySetResult(true);
        Assert.True(await request.Approval.Task.WaitAsync(Wait));
        Assert.Contains("remote client", request.Feedback.DecidedBy);
    }

    [Fact]
    public async Task A_denial_with_a_note_reaches_the_flow()
    {
        await using var server = StartServer();
        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var events = client.EventsAsync().GetAsyncEnumerator();

        var request = new ApprovalRequestEvent(new ToolCall { Id = "c2", Name = "write_file", Arguments = new() }, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        server.Publish(new AgentTaggedEvent(request, "build"));
        var remote = Assert.IsType<ApprovalRequestEvent>(Assert.IsType<AgentTaggedEvent>(await Next(events)).Inner);
        remote.Feedback.Text = "use a different file";
        remote.Approval.TrySetResult(false);

        Assert.False(await request.Approval.Task.WaitAsync(Wait));
        Assert.Equal("use a different file", request.Feedback.Text);
    }

    [Fact]
    public async Task A_human_step_can_be_answered_remotely()
    {
        await using var server = StartServer();
        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var events = client.EventsAsync().GetAsyncEnumerator();

        var request = new FlowHumanRequestEvent("build", "Ship it?", new[] { "yes", "no" }, new TaskCompletionSource<FlowHumanChoice>(TaskCreationOptions.RunContinuationsAsynchronously));
        server.Publish(new AgentTaggedEvent(request, "build"));
        var remote = Assert.IsType<FlowHumanRequestEvent>(Assert.IsType<AgentTaggedEvent>(await Next(events)).Inner);
        Assert.Equal(new[] { "yes", "no" }, remote.Outcomes);

        remote.Choice.TrySetResult(new FlowHumanChoice("no", "not yet"));
        var choice = await request.Choice.Task.WaitAsync(Wait);
        Assert.Equal("no", choice.Outcome);
        Assert.Equal("not yet", choice.Note);
        Assert.False(choice.Stop);
    }

    [Fact]
    public async Task A_question_nobody_answers_is_denied_after_the_timeout()
    {
        await using var server = StartServer(TimeSpan.FromMilliseconds(200));
        var request = new ApprovalRequestEvent(new ToolCall { Id = "c3", Name = "run_command", Arguments = new() }, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        server.Publish(new AgentTaggedEvent(request, "build"));

        Assert.False(await request.Approval.Task.WaitAsync(Wait));
        Assert.Contains("denied", request.Feedback.DecidedBy);
    }

    [Fact]
    public async Task The_second_client_is_told_when_the_first_one_answered()
    {
        await using var server = StartServer();
        await using var first = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var second = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var firstEvents = first.EventsAsync().GetAsyncEnumerator();
        await using var secondEvents = second.EventsAsync().GetAsyncEnumerator();

        var request = new ApprovalRequestEvent(new ToolCall { Id = "c4", Name = "run_command", Arguments = new() }, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        server.Publish(new AgentTaggedEvent(request, "build"));
        var firstRemote = Assert.IsType<ApprovalRequestEvent>(Assert.IsType<AgentTaggedEvent>(await Next(firstEvents)).Inner);
        Assert.IsType<ApprovalRequestEvent>(Assert.IsType<AgentTaggedEvent>(await Next(secondEvents)).Inner);

        firstRemote.Approval.TrySetResult(true);
        Assert.True(await request.Approval.Task.WaitAsync(Wait));

        var resolved = Assert.IsType<RemoteRequestResolvedEvent>(await Next(secondEvents));
        Assert.Equal("build", resolved.NodeId);
    }

    [Fact]
    public async Task A_client_that_joins_after_a_question_was_answered_does_not_see_it()
    {
        await using var server = StartServer();
        var request = new ApprovalRequestEvent(new ToolCall { Id = "c5", Name = "run_command", Arguments = new() }, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        server.Publish(new AgentTaggedEvent(request, "build"));
        request.Approval.TrySetResult(true);
        await Task.Delay(200); // let the server notice the answer
        server.Publish(Started());

        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        await using var events = client.EventsAsync().GetAsyncEnumerator();
        Assert.IsType<FlowStartedEvent>(await Next(events));
    }

    [Fact]
    public async Task Cancel_from_a_client_stops_the_run()
    {
        await using var server = StartServer();
        await using var client = await RemoteRunClient.ConnectAsync(server.Url, Token);
        Assert.False(server.RunCancelRequested.IsCancellationRequested);

        await client.CancelRunAsync();
        var deadline = DateTime.UtcNow + Wait;
        while (!server.RunCancelRequested.IsCancellationRequested && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(server.RunCancelRequested.IsCancellationRequested);
    }

    [Fact]
    public async Task Health_answers_without_a_token()
    {
        await using var server = StartServer();
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://{server.Host}:{server.Port}{RemoteProtocol.HealthPath}");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public void A_server_needs_a_token()
    {
        var hello = new RemoteHello(RemoteProtocol.Version, "r", "demo", "t", DateTimeOffset.UtcNow, Flow());
        Assert.Throws<ArgumentException>(() => new RemoteRunServer(hello, ""));
    }
}
