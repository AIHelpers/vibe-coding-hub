using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.Board;

/// <summary>E11-S1: the board as the user sees it: columns, actions, several cards at once, one board per project.</summary>
public sealed class MissionControlViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mission-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<Action> _ui = new();

    public MissionControlViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeIsolation : IWorkspaceIsolation
    {
        private int _n;
        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _n);
            var path = Path.Combine(Path.GetTempPath(), "wt-" + n);
            return Task.FromResult<WorkspaceLease?>(new WorkspaceLease(agentId, "agent/" + sessionId, path, path, baseDirectory));
        }

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceMergeResult(WorkspaceMergeOutcome.Kept, lease.Branch, "kept", Array.Empty<string>(), lease.RootPath));
    }

    private sealed class FakeAgent : IBoardAgentRunner
    {
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public bool Hold { get; init; }
        public bool Fail { get; set; }

        public void Release() => _go.TrySetResult();

        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            yield return new ToolCallStartEvent(new ToolCall { Name = "edit_file", Arguments = new() { ["path"] = "a.cs" } });
            yield return new TokenUsageEvent(new TokenUsage { PromptTokens = 1_500 }, 0.1m);
            if (Hold)
            {
                var stopped = false;
                try { await _go.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { stopped = true; }
                if (stopped)
                {
                    yield return new AgentFinishedEvent(new AgentResponse { WasCancelled = true });
                    yield break;
                }
            }
            if (Fail) yield return new AgentErrorEvent(new InvalidOperationException("no model"));
            else yield return new AgentFinishedEvent(new AgentResponse { Content = "Done." });
        }
    }

    private MissionControlViewModel Create(FakeAgent? agent, bool withIsolation = true) =>
        new(withIsolation ? new FakeIsolation() : null, agent, dir => new BoardStore(Path.Combine(_root, "boards", Path.GetFileName(dir), "board.json")), a => _ui.Enqueue(a));

    /// <summary>Applies the board changes that agent threads queued, the way the UI thread would.</summary>
    private void Pump()
    {
        while (_ui.TryDequeue(out var action)) action();
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Pump();
            if (condition()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static string[] Titles(System.Collections.ObjectModel.ObservableCollection<BoardCardViewModel> column) =>
        column.Select(c => c.Title).ToArray();

    // ------------------------------------------------------------------ tests

    [Fact]
    public void AddingACard_PutsItInTheBacklog_AndClearsTheInput()
    {
        var vm = Create(new FakeAgent());
        vm.SetProject(Path.Combine(_root, "app"));
        Assert.False(vm.AddCardCommand.CanExecute(null)); // nothing typed yet
        Assert.True(vm.HasNoCards);

        vm.NewTaskText = "Fix the login timeout";
        Assert.True(vm.AddCardCommand.CanExecute(null));
        vm.AddCardCommand.Execute(null);
        Pump();

        Assert.Equal(new[] { "Fix the login timeout" }, Titles(vm.Backlog));
        Assert.Equal(string.Empty, vm.NewTaskText);
        Assert.False(vm.HasNoCards);
        Assert.True(vm.Backlog[0].CanStart);
        Assert.False(vm.Backlog[0].CanStop);
    }

    [Fact]
    public async Task AStartedCard_MovesFromBacklogToRunningToNeedsReview()
    {
        var agent = new FakeAgent { Hold = true };
        var vm = Create(agent);
        vm.SetProject(Path.Combine(_root, "app"));
        vm.NewTaskText = "Add tests";
        vm.AddCardCommand.Execute(null);
        Pump();

        vm.Backlog[0].StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.Running.Count == 1 && vm.Running[0].LastActionText == "edit_file a.cs"); // not the earlier "Preparing…"

        Assert.Empty(vm.Backlog);
        var card = vm.Running[0];
        Assert.True(card.CanStop);
        Assert.Equal("edit_file a.cs", card.LastActionText);
        Assert.Equal(1, vm.RunningCount);
        Assert.True(vm.HasRunning);

        agent.Release();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        Assert.Empty(vm.Running);
        var done = vm.NeedsReview[0];
        Assert.Equal("✓ Ready for review", done.StatusText);
        Assert.Equal("Done.", done.SummaryText);
        Assert.True(done.HasBranch);
        Assert.True(done.CostText == "$0.10" && done.HasCost);
        Assert.Equal("1.5k tokens", done.TokensText);
        Assert.Equal(0, vm.RunningCount);
    }

    [Fact]
    public async Task StartAll_RunsEveryBacklogCardAtTheSameTime()
    {
        var agent = new FakeAgent { Hold = true };
        var vm = Create(agent);
        vm.SetProject(Path.Combine(_root, "app"));
        foreach (var task in new[] { "one", "two", "three" })
        {
            vm.NewTaskText = task;
            vm.AddCardCommand.Execute(null);
        }
        Pump();
        Assert.Equal("▶ Start all (3)", vm.StartAllText);

        vm.StartAllCommand.Execute(null);
        await WaitUntilAsync(() => vm.Running.Count == 3);

        Assert.Equal(3, vm.RunningCount);
        Assert.Equal(new[] { "one", "two", "three" }, Titles(vm.Running)); // oldest first
        agent.Release();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 3);
        Assert.Equal(new[] { "▶ Start" }, new[] { vm.StartAllText });
    }

    [Fact]
    public async Task StoppingACard_MovesItToNeedsReview_AsStopped()
    {
        var agent = new FakeAgent { Hold = true };
        var vm = Create(agent);
        vm.SetProject(Path.Combine(_root, "app"));
        vm.NewTaskText = "long task";
        vm.AddCardCommand.Execute(null);
        Pump();
        vm.Backlog[0].StartCommand.Execute(null);
        await WaitUntilAsync(() => agent.Started == 1 && vm.Running.Count == 1);

        vm.Running[0].StopCommand.Execute(null);
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        Assert.Equal("○ Stopped", vm.NeedsReview[0].StatusText);
        Assert.True(vm.NeedsReview[0].CanRetry);
    }

    [Fact]
    public async Task AFailedCard_CanBeRetried_AndASuccessfulOneMarkedDoneOrSentBack()
    {
        var agent = new FakeAgent { Fail = true };
        var vm = Create(agent);
        vm.SetProject(Path.Combine(_root, "app"));
        vm.NewTaskText = "flaky";
        vm.AddCardCommand.Execute(null);
        Pump();
        vm.Backlog[0].StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        var failed = vm.NeedsReview[0];
        Assert.Equal("✗ Failed", failed.StatusText);
        Assert.Equal("no model", failed.SummaryText);
        Assert.True(failed.SummaryIsError);
        Assert.True(failed.CanRetry);

        agent.Fail = false;
        failed.RetryCommand.Execute(null);
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1 && vm.NeedsReview[0].Card.Runs == 2 && vm.NeedsReview[0].Card.Outcome == CardOutcome.Completed);

        var ok = vm.NeedsReview[0];
        Assert.False(ok.CanRetry);
        Assert.Equal("run 2", ok.RunText);

        ok.MarkDoneCommand.Execute(null);
        Pump();
        Assert.Empty(vm.NeedsReview);
        Assert.Equal(new[] { "flaky" }, Titles(vm.Done));
        Assert.Equal("✓ Done", vm.Done[0].StatusText);

        vm.Done[0].ReturnCommand.Execute(null);
        Pump();
        Assert.Empty(vm.Done);
        Assert.Single(vm.Backlog);

        vm.Backlog[0].RemoveCommand.Execute(null);
        Pump();
        Assert.True(vm.HasNoCards);
    }

    [Fact]
    public async Task ARunningCard_CannotBeRemoved()
    {
        var agent = new FakeAgent { Hold = true };
        var vm = Create(agent);
        vm.SetProject(Path.Combine(_root, "app"));
        vm.NewTaskText = "busy";
        vm.AddCardCommand.Execute(null);
        Pump();
        vm.Backlog[0].StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.Running.Count == 1);

        Assert.False(vm.Running[0].CanRemove);
        vm.Running[0].RemoveCommand.Execute(null);
        Pump();
        Assert.Single(vm.Running);
        agent.Release();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }

    [Fact]
    public async Task EachProjectHasItsOwnBoard_AndCardsKeepRunningWhenYouSwitchAway()
    {
        var agent = new FakeAgent { Hold = true };
        var vm = Create(agent);
        var a = Path.Combine(_root, "alpha");
        var b = Path.Combine(_root, "beta");
        vm.SetProject(a);
        vm.NewTaskText = "alpha task";
        vm.AddCardCommand.Execute(null);
        Pump();
        vm.Backlog[0].StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.Running.Count == 1);

        vm.SetProject(b);

        Assert.Equal("beta", vm.ProjectName);
        Assert.True(vm.HasNoCards);
        Assert.Equal(1, vm.RunningCount); // still counted for the badge

        agent.Release();
        await WaitUntilAsync(() => vm.RunningCount == 0);
        vm.SetProject(a);
        Assert.Equal(new[] { "alpha task" }, Titles(vm.NeedsReview));
    }

    [Fact]
    public async Task TheBoard_SurvivesARestart_AndACardThatWasRunningShowsAsInterrupted()
    {
        var agent = new FakeAgent { Hold = true };
        var first = Create(agent);
        var dir = Path.Combine(_root, "app");
        first.SetProject(dir);
        first.NewTaskText = "waiting";
        first.AddCardCommand.Execute(null);
        first.NewTaskText = "cut off";
        first.AddCardCommand.Execute(null);
        Pump();
        first.Backlog[1].StartCommand.Execute(null);
        await WaitUntilAsync(() => first.Running.Count == 1);

        var second = Create(new FakeAgent());   // a new process reading the same file
        second.SetProject(dir);

        Assert.Equal(new[] { "waiting" }, Titles(second.Backlog));
        var interrupted = Assert.Single(second.NeedsReview);
        Assert.Equal("cut off", interrupted.Title);
        Assert.Equal("⚠ Interrupted", interrupted.StatusText);
        agent.Release();
        await WaitUntilAsync(() => first.RunningCount == 0);
    }

    [Fact]
    public void WithoutAnAgent_CardsCanBePlanned_ButNotStarted_AndTheUserIsTold()
    {
        var vm = Create(agent: null);
        vm.SetProject(Path.Combine(_root, "app"));
        vm.NewTaskText = "plan only";
        vm.AddCardCommand.Execute(null);
        Pump();

        Assert.False(vm.CanRun);
        Assert.False(vm.Backlog[0].CanStart);
        vm.Backlog[0].StartCommand.Execute(null);
        Assert.True(vm.HasNotice);
        vm.DismissNoticeCommand.Execute(null);
        Assert.False(vm.HasNotice);
        Assert.Single(vm.Backlog);
    }

    [Fact]
    public void Formatting_IsShortAndReadable()
    {
        Assert.Equal("42 s", BoardCardViewModel.FormatDuration(TimeSpan.FromSeconds(42)));
        Assert.Equal("3 min 12 s", BoardCardViewModel.FormatDuration(TimeSpan.FromSeconds(192)));
        Assert.Equal("1 h 05 min", BoardCardViewModel.FormatDuration(TimeSpan.FromMinutes(65)));
        Assert.Equal("950 tokens", BoardCardViewModel.FormatTokens(950));
        Assert.Equal("12.3k tokens", BoardCardViewModel.FormatTokens(12_300));
        Assert.Equal("1.25M tokens", BoardCardViewModel.FormatTokens(1_250_000));
    }
}
