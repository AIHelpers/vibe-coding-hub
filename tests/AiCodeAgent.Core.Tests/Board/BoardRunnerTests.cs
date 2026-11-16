using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Tests.Board;

/// <summary>E11-S1: running cards in their own worktrees, several at once, and how each run ends.</summary>
public sealed class BoardRunnerTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeIsolation : IWorkspaceIsolation
    {
        private int _counter;
        public bool Refuse { get; init; }
        public WorkspaceMergeOutcome Result { get; set; } = WorkspaceMergeOutcome.Kept;
        public ConcurrentQueue<WorkspaceLease> Leases { get; } = new();
        public ConcurrentQueue<(WorkspaceLease Lease, string Summary, bool Merge)> Completed { get; } = new();

        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Refuse) return Task.FromResult<WorkspaceLease?>(null);
            var n = Interlocked.Increment(ref _counter);
            var root = Path.Combine(Path.GetTempPath(), "wt", n.ToString());
            var lease = new WorkspaceLease(agentId, $"agent/{sessionId}/{n}", root, root, baseDirectory);
            Leases.Enqueue(lease);
            return Task.FromResult<WorkspaceLease?>(lease);
        }

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default)
        {
            Completed.Enqueue((lease, summary, merge));
            return Task.FromResult(new WorkspaceMergeResult(Result, lease.Branch, Result == WorkspaceMergeOutcome.Failed ? "commit failed" : "ok",
                Array.Empty<string>(), Result == WorkspaceMergeOutcome.NoChanges ? null : lease.RootPath));
        }
    }

    /// <summary>An agent that works until the test lets it go (or the run is stopped).</summary>
    private sealed class FakeAgent : IBoardAgentRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public int MaxActive;
        public int Started;
        public bool Hold { get; init; }
        public string Answer { get; init; } = "All done.";
        public Exception? Throw { get; init; }
        public bool ErrorEvent { get; init; }
        public bool AskForApproval { get; init; }
        public ConcurrentQueue<(string Prompt, string Session, AgentOptions Options)> Calls { get; } = new();
        public TaskCompletionSource<bool>? Approval;

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Enqueue((prompt, sessionId, options));
            Interlocked.Increment(ref Started);
            var now = Interlocked.Increment(ref _active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            try
            {
                if (Throw != null) throw Throw;
                yield return new ToolCallStartEvent(new ToolCall { Name = "edit_file", Arguments = new() { ["path"] = "src/A.cs" } });
                yield return new TokenUsageEvent(new TokenUsage { PromptTokens = 900, CompletionTokens = 100 }, 0.25m);
                if (AskForApproval)
                {
                    Approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    yield return new ApprovalRequestEvent(new ToolCall { Name = "run_command" }, Approval);
                }
                if (Hold)
                {
                    var stopped = false;
                    try { await _release.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) { stopped = true; }
                    if (stopped)
                    {
                        yield return new AgentFinishedEvent(new AgentResponse { WasCancelled = true });
                        yield break;
                    }
                }
                if (ErrorEvent)
                {
                    yield return new AgentErrorEvent(new InvalidOperationException("model unavailable"));
                    yield break;
                }
                yield return new AgentFinishedEvent(new AgentResponse { Content = Answer });
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static (TaskBoard Board, BoardRunner Runner) Create(FakeIsolation isolation, FakeAgent agent, Func<BoardCard, AgentOptions, AgentOptions>? configure = null)
    {
        var board = new TaskBoard();
        return (board, new BoardRunner(board, isolation, agent, "/repo", configure));
    }

    private static async Task<BoardCard> RunToEndAsync(TaskBoard board, BoardRunner runner, string prompt = "Fix the bug")
    {
        var id = board.Add(prompt).Id;
        await runner.Start(id)!;
        return board.Get(id)!;
    }

    // ------------------------------------------------------------------ a run

    [Fact]
    public async Task ACard_RunsInItsOwnWorktree_AndEndsInNeedsReview_WithItsWorkCommittedButNotMerged()
    {
        var isolation = new FakeIsolation();
        var agent = new FakeAgent { Answer = "Fixed the timeout." };
        var (board, runner) = Create(isolation, agent);

        var card = await RunToEndAsync(board, runner);

        Assert.Equal(BoardColumn.NeedsReview, card.Column);
        Assert.Equal(CardOutcome.Completed, card.Outcome);
        Assert.Equal("Fixed the timeout.", card.Summary);
        var lease = Assert.Single(isolation.Leases);
        Assert.Equal(lease.Branch, card.Branch);
        Assert.Equal(lease.RootPath, card.WorktreePath);
        var done = Assert.Single(isolation.Completed);
        Assert.False(done.Merge);
        Assert.Equal("Fix the bug", done.Summary);
        Assert.Equal(0, runner.RunningCount);

        var call = Assert.Single(agent.Calls);
        Assert.Equal("Fix the bug", call.Prompt);
        Assert.Equal(lease.WorkingDirectory, call.Options.WorkingDirectory);
        Assert.Equal(new[] { lease.WorkingDirectory }, call.Options.AllowedPaths);
        Assert.True(call.Options.NonInteractive);
        Assert.Equal(PermissionMode.AutoEdit, call.Options.PermissionMode);
        Assert.Equal($"card-{card.Id}-1", call.Session);
    }

    [Fact]
    public async Task TheCard_ShowsTheLastActionCostAndTokens()
    {
        var (board, runner) = Create(new FakeIsolation(), new FakeAgent());

        var card = await RunToEndAsync(board, runner);

        Assert.Equal("edit_file src/A.cs", card.LastAction);
        Assert.Equal(1, card.ToolCalls);
        Assert.Equal(0.25m, card.CostUsd);
        Assert.Equal(1000, card.Tokens);
    }

    [Fact]
    public async Task OptionsCanBeSetByTheHost()
    {
        var agent = new FakeAgent();
        var (board, runner) = Create(new FakeIsolation(), agent, (_, o) => o with { Model = "small-model", MaxBudgetUsd = 2m });

        await RunToEndAsync(board, runner);

        var options = agent.Calls.Single().Options;
        Assert.Equal("small-model", options.Model);
        Assert.Equal(2m, options.MaxBudgetUsd);
    }

    [Fact]
    public async Task ARunThatChangedNothing_EndsAsNoChanges_WithoutABranch()
    {
        var (board, runner) = Create(new FakeIsolation { Result = WorkspaceMergeOutcome.NoChanges }, new FakeAgent { Answer = "Nothing to fix." });

        var card = await RunToEndAsync(board, runner);

        Assert.Equal(CardOutcome.NoChanges, card.Outcome);
        Assert.Null(card.Branch);
        Assert.Null(card.WorktreePath);
        Assert.Equal("Nothing to fix.", card.Summary);
    }

    // ------------------------------------------------------------------ several at once

    [Fact]
    public async Task SeveralCards_RunAtTheSameTime_EachInItsOwnWorktree()
    {
        var isolation = new FakeIsolation();
        var agent = new FakeAgent { Hold = true };
        var (board, runner) = Create(isolation, agent);
        var ids = new[] { board.Add("one").Id, board.Add("two").Id, board.Add("three").Id };

        Assert.Equal(3, runner.StartAll());
        await WaitUntilAsync(() => agent.Started == 3);

        Assert.Equal(3, agent.MaxActive);
        Assert.Equal(3, runner.RunningCount);
        Assert.Equal(3, board.Count(BoardColumn.Running));
        Assert.Equal(3, isolation.Leases.Select(l => l.RootPath).Distinct().Count());
        Assert.Equal(3, isolation.Leases.Select(l => l.Branch).Distinct().Count());

        agent.Release();
        await runner.WhenIdleAsync();

        Assert.All(ids, id => Assert.Equal(BoardColumn.NeedsReview, board.Get(id)!.Column));
        Assert.Equal(0, runner.RunningCount);
    }

    [Fact]
    public async Task StartAll_OnlyTakesTheBacklog()
    {
        var (board, runner) = Create(new FakeIsolation(), new FakeAgent());
        var finished = await RunToEndAsync(board, runner, "already ran");
        board.Add("waiting");

        Assert.Equal(1, runner.StartAll());
        await runner.WhenIdleAsync();

        Assert.Equal(BoardColumn.NeedsReview, board.Get(finished.Id)!.Column);
        Assert.Null(runner.Start(finished.Id)); // not in the backlog: cannot start
        Assert.Null(runner.Start("missing"));
    }

    // ------------------------------------------------------------------ stop, failure, retry

    [Fact]
    public async Task Stopping_KeepsTheWorkSoFarOnTheBranch_AndMovesTheCardToNeedsReview()
    {
        var isolation = new FakeIsolation();
        var agent = new FakeAgent { Hold = true };
        var (board, runner) = Create(isolation, agent);
        var id = board.Add("long task").Id;
        var run = runner.Start(id)!;
        await WaitUntilAsync(() => agent.Started == 1);

        Assert.True(runner.Stop(id));
        await run;

        var card = board.Get(id)!;
        Assert.Equal(CardOutcome.Stopped, card.Outcome);
        Assert.Equal(BoardColumn.NeedsReview, card.Column);
        Assert.Equal("Stopped by you.", card.Summary);
        Assert.NotNull(card.Branch);
        Assert.False(Assert.Single(isolation.Completed).Merge);
        Assert.False(runner.Stop(id)); // nothing running any more
    }

    [Fact]
    public async Task AnAgentError_FailsTheCard_ButStillKeepsWhateverWasDone()
    {
        var isolation = new FakeIsolation();
        var (board, runner) = Create(isolation, new FakeAgent { ErrorEvent = true });

        var card = await RunToEndAsync(board, runner);

        Assert.Equal(CardOutcome.Failed, card.Outcome);
        Assert.Equal("model unavailable", card.Summary);
        Assert.NotNull(card.Branch);
        Assert.Single(isolation.Completed);
    }

    [Fact]
    public async Task AnExceptionInTheAgent_FailsOnlyThatCard()
    {
        var (board, runner) = Create(new FakeIsolation(), new FakeAgent { Throw = new InvalidOperationException("provider crashed") });

        var card = await RunToEndAsync(board, runner);

        Assert.Equal(CardOutcome.Failed, card.Outcome);
        Assert.Equal("provider crashed", card.Summary);
    }

    [Fact]
    public async Task WithoutAGitRepository_TheCardFails_AndTheAgentNeverRunsInTheSharedFolder()
    {
        var agent = new FakeAgent();
        var (board, runner) = Create(new FakeIsolation { Refuse = true }, agent);

        var card = await RunToEndAsync(board, runner);

        Assert.Equal(CardOutcome.Failed, card.Outcome);
        Assert.Contains("git repository", card.Summary);
        Assert.Equal(0, agent.Started);
        Assert.Null(card.Branch);
    }

    [Fact]
    public async Task ACommitThatFails_FailsTheCard_WithTheReason()
    {
        var (board, runner) = Create(new FakeIsolation { Result = WorkspaceMergeOutcome.Failed }, new FakeAgent());

        var card = await RunToEndAsync(board, runner);

        Assert.Equal(CardOutcome.Failed, card.Outcome);
        Assert.Equal("commit failed", card.Summary);
    }

    [Fact]
    public async Task AnApprovalRequest_IsDeniedAtOnce_SoNobodyWaitsForever()
    {
        var agent = new FakeAgent { AskForApproval = true };
        var (board, runner) = Create(new FakeIsolation(), agent);

        await RunToEndAsync(board, runner);

        Assert.False(await agent.Approval!.Task);
    }

    [Fact]
    public async Task ARetry_RunsAgainOnANewBranch_AndTheCostAddsUp()
    {
        var isolation = new FakeIsolation();
        var agent = new FakeAgent();
        var (board, runner) = Create(isolation, agent);
        var first = await RunToEndAsync(board, runner);
        board.ReturnToBacklog(first.Id);

        await runner.Start(first.Id)!;

        var card = board.Get(first.Id)!;
        Assert.Equal(2, card.Runs);
        Assert.Equal(0.5m, card.CostUsd);
        Assert.Equal(2000, card.Tokens);
        Assert.Equal(2, isolation.Leases.Select(l => l.Branch).Distinct().Count());
        Assert.Equal(new[] { $"card-{first.Id}-1", $"card-{first.Id}-2" }, agent.Calls.Select(c => c.Session));
    }

    [Fact]
    public void Describe_ShowsTheToolAndItsFirstArgument()
    {
        Assert.Equal("edit_file src/A.cs", BoardRunner.Describe(new ToolCall { Name = "edit_file", Arguments = new() { ["path"] = "src/A.cs" } }));
        Assert.Equal("list_files", BoardRunner.Describe(new ToolCall { Name = "list_files" }));
        Assert.EndsWith("…", BoardRunner.Describe(new ToolCall { Name = "run_command", Arguments = new() { ["command"] = new string('x', 200) } }));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
