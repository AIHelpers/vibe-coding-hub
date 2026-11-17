using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Tests.Board;

/// <summary>E11-S3: pausing, messaging, redirecting and stopping a running card.</summary>
public sealed class BoardControlTests
{
    private sealed class Isolation : IWorkspaceIsolation
    {
        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
        {
            var root = Path.Combine(Path.GetTempPath(), "wt-control");
            return Task.FromResult<WorkspaceLease?>(new WorkspaceLease(agentId, "agent/" + sessionId, root, root, baseDirectory));
        }

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceMergeResult(WorkspaceMergeOutcome.Kept, lease.Branch, "ok", Array.Empty<string>(), lease.RootPath));
    }

    /// <summary>Every turn works until the test finishes it, or until the turn is broken off.</summary>
    private sealed class TurnAgent : IBoardAgentRunner
    {
        private readonly SemaphoreSlim _finish = new(0);
        public ConcurrentQueue<(string Prompt, string Session)> Calls { get; } = new();
        public int CallCount => Calls.Count;

        public void FinishTurn() => _finish.Release();

        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Enqueue((prompt, sessionId));
            var turn = Calls.Count;
            yield return new ToolCallStartEvent(new ToolCall { Name = "edit_file", Arguments = new() { ["path"] = $"src/T{turn}.cs" } });
            // The session's cost is a running total, the tokens are those of the turn.
            yield return new TokenUsageEvent(new TokenUsage { PromptTokens = 900, CompletionTokens = 100 }, 0.1m * turn);

            var broken = false;
            try { await _finish.WaitAsync(ct); }
            catch (OperationCanceledException) { broken = true; }

            yield return broken
                ? new AgentFinishedEvent(new AgentResponse { WasCancelled = true })
                : new AgentFinishedEvent(new AgentResponse { Content = $"answer {turn}" });
        }
    }

    private sealed class FlowOnlyRunner : IBoardTargetRunner
    {
        public bool Supports(AgentTargetKind kind) => kind == AgentTargetKind.Flow;
        public Task<string?> CheckAsync(AgentTarget target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public async IAsyncEnumerable<AgentEvent> RunAsync(AgentTarget target, BoardRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new BoardProgressEvent("working");
            var cancelled = false;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { cancelled = true; }
            if (cancelled) yield return new AgentFinishedEvent(new AgentResponse { WasCancelled = true });
        }
    }

    private static (TaskBoard Board, BoardRunner Runner, TurnAgent Agent) Create()
    {
        var board = new TaskBoard();
        var agent = new TurnAgent();
        return (board, new BoardRunner(board, new Isolation(), agent, "/repo"), agent);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static async Task<Task> StartSteerableAsync(TaskBoard board, BoardRunner runner, TurnAgent agent, string id)
    {
        var task = runner.Start(id)!;
        await WaitFor(() => agent.CallCount == 1 && runner.CanSteer(id));
        return task;
    }

    [Fact]
    public async Task AMessage_BreaksOffTheTurn_AndContinuesTheSameSession()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;
        var run = await StartSteerableAsync(board, runner, agent, id);

        Assert.True(runner.Send(id, "use the existing Result type"));
        await WaitFor(() => agent.CallCount == 2);
        agent.FinishTurn();
        await run;

        var calls = agent.Calls.ToArray();
        Assert.Equal("Add a cache", calls[0].Prompt);
        Assert.Contains("use the existing Result type", calls[1].Prompt);
        Assert.Contains("carry on", calls[1].Prompt);
        Assert.Equal(calls[0].Session, calls[1].Session); // the same conversation: the agent keeps what it did

        var card = board.Get(id)!;
        Assert.Equal(BoardColumn.NeedsReview, card.Column);
        Assert.Equal(CardOutcome.Completed, card.Outcome); // breaking off a turn is not a stop
        Assert.Equal("answer 2", card.Summary);
        Assert.False(card.CanSteer);
        Assert.False(card.IsPaused);
    }

    [Fact]
    public async Task ARedirect_TellsTheAgentToDropItsApproach()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;
        var run = await StartSteerableAsync(board, runner, agent, id);

        Assert.True(runner.Send(id, "cache in Redis instead", SteerKind.Redirect));
        await WaitFor(() => agent.CallCount == 2);
        agent.FinishTurn();
        await run;

        var second = agent.Calls.ToArray()[1].Prompt;
        Assert.Contains("Change of direction", second);
        Assert.Contains("cache in Redis instead", second);
    }

    [Fact]
    public async Task APausedCard_WaitsUntilResumed_ThenContinuesWhereItLeftOff()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;
        var run = await StartSteerableAsync(board, runner, agent, id);

        Assert.True(runner.Pause(id));
        await WaitFor(() => board.Get(id)!.LastAction == "Paused. Waiting for you.");
        Assert.True(board.Get(id)!.IsPaused);
        Assert.Equal(BoardColumn.Running, board.Get(id)!.Column);
        await Task.Delay(100);
        Assert.Equal(1, agent.CallCount); // nothing runs while paused
        Assert.False(runner.Pause(id));   // already paused

        Assert.True(runner.Resume(id));
        await WaitFor(() => agent.CallCount == 2);
        Assert.Equal(BoardSteering.ResumePrompt, agent.Calls.ToArray()[1].Prompt);
        Assert.False(board.Get(id)!.IsPaused);
        agent.FinishTurn();
        await run;

        Assert.Equal(CardOutcome.Completed, board.Get(id)!.Outcome);
        Assert.False(runner.Resume(id)); // no longer running
    }

    [Fact]
    public async Task AMessage_ToAPausedCard_ResumesItWithTheMessage()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;
        var run = await StartSteerableAsync(board, runner, agent, id);
        runner.Pause(id);
        await WaitFor(() => board.Get(id)!.LastAction == "Paused. Waiting for you.");

        Assert.True(runner.Send(id, "skip the tests"));
        await WaitFor(() => agent.CallCount == 2);
        agent.FinishTurn();
        await run;

        Assert.Contains("skip the tests", agent.Calls.ToArray()[1].Prompt);
        Assert.False(board.Get(id)!.IsPaused);
    }

    [Fact]
    public async Task StoppingAPausedCard_EndsItAsStopped_WithoutAnotherTurn()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;
        var run = await StartSteerableAsync(board, runner, agent, id);
        runner.Pause(id);
        await WaitFor(() => board.Get(id)!.LastAction == "Paused. Waiting for you.");

        Assert.True(runner.Stop(id));
        await run;

        var card = board.Get(id)!;
        Assert.Equal(CardOutcome.Stopped, card.Outcome);
        Assert.False(card.IsPaused);
        Assert.Equal(1, agent.CallCount);
    }

    [Fact]
    public async Task CostAndTokens_AddUpOverTheTurnsOfOneRun()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;
        var run = await StartSteerableAsync(board, runner, agent, id);
        await WaitFor(() => board.Get(id)!.Tokens == 1000);

        runner.Send(id, "go on");
        await WaitFor(() => agent.CallCount == 2);
        agent.FinishTurn();
        await run;

        var card = board.Get(id)!;
        Assert.Equal(2000, card.Tokens);   // 1000 for each turn
        Assert.Equal(0.2m, card.CostUsd);  // the session total after the second turn
    }

    [Fact]
    public async Task Commands_AreRefused_ForACardThatIsNotRunning_AndForEmptyText()
    {
        var (board, runner, agent) = Create();
        var id = board.Add("Add a cache").Id;

        Assert.False(runner.Pause(id));
        Assert.False(runner.Send(id, "hello"));
        Assert.False(runner.CanSteer(id));

        var run = await StartSteerableAsync(board, runner, agent, id);
        Assert.False(runner.Send(id, "   "));
        agent.FinishTurn();
        await run;
        Assert.False(runner.Send(id, "too late"));
    }

    [Fact]
    public async Task AFlow_CanOnlyBeStopped()
    {
        var board = new TaskBoard();
        var runner = new BoardRunner(board, new Isolation(), new TurnAgent(), "/repo", targets: new FlowOnlyRunner());
        var id = board.Add("Review", agent: AgentTarget.ForFlow("sdlc")).Id;

        var run = runner.Start(id)!;
        await WaitFor(() => board.Get(id)!.LastAction == "working");

        Assert.False(runner.CanSteer(id));
        Assert.False(runner.Pause(id));
        Assert.False(runner.Send(id, "change course"));
        Assert.False(board.Get(id)!.CanSteer);

        Assert.True(runner.Stop(id));
        await run;
        Assert.Equal(CardOutcome.Stopped, board.Get(id)!.Outcome);
    }

    // ------------------------------------------------------------------ the channel itself

    [Fact]
    public async Task ACommandBetweenTwoTurns_IsNotLost()
    {
        var steering = new BoardSteering(true);
        Assert.True(steering.Post("first"));      // arrives while no turn runs yet

        using var turn = new CancellationTokenSource();
        steering.BeginTurn("original task", turn);

        Assert.True(turn.IsCancellationRequested);  // the turn ends before it starts
        Assert.True(steering.EndTurn());
        var next = await steering.NextAsync(CancellationToken.None);
        Assert.Equal("original task\n\nfirst", next); // the task is kept in front of the message
    }

    [Fact]
    public async Task AfterTheLastTurn_NoCommandIsAccepted()
    {
        var steering = new BoardSteering(true);
        using var turn = new CancellationTokenSource();
        steering.BeginTurn("task", turn);
        steering.EndTurn();

        Assert.Null(await steering.NextAsync(CancellationToken.None));
        Assert.False(steering.Post("late"));
        Assert.False(steering.Pause());
    }

    [Fact]
    public async Task ADisabledChannel_RefusesEverything()
    {
        var steering = new BoardSteering(false);
        using var turn = new CancellationTokenSource();
        steering.BeginTurn("task", turn);

        Assert.False(steering.Pause());
        Assert.False(steering.Post("x"));
        Assert.False(turn.IsCancellationRequested);
        Assert.False(steering.EndTurn());
        Assert.Null(await steering.NextAsync(CancellationToken.None));
    }

    [Fact]
    public void ThePausedAndSteerableFlags_AreNeverSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), "board-flags-" + Guid.NewGuid().ToString("N"), "board.json");
        try
        {
            var board = new TaskBoard(new BoardStore(path));
            var id = board.Add("Task").Id;
            board.BeginRun(id);
            board.Update(id, c => c with { IsPaused = true, CanSteer = true, LastAction = "x" }); // Update saves

            var reloaded = new BoardStore(path).Load().Single();
            Assert.False(reloaded.IsPaused);
            Assert.False(reloaded.CanSteer);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { }
        }
    }
}
