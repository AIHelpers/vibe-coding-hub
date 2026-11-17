using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.Board;

/// <summary>E11-S3: the controls of a running card (pause, message, redirect), its terminal and its changes.</summary>
public sealed class MissionControlControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mission-control-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<Action> _ui = new();
    private readonly string _worktree;

    public MissionControlControlTests()
    {
        _worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Isolation(string path) : IWorkspaceIsolation
    {
        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default) =>
            Task.FromResult<WorkspaceLease?>(new WorkspaceLease(agentId, "agent/" + sessionId, path, path, baseDirectory));

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceMergeResult(WorkspaceMergeOutcome.Kept, lease.Branch, "kept", Array.Empty<string>(), lease.RootPath));
    }

    /// <summary>Works until released; a broken-off turn ends cancelled.</summary>
    private sealed class HoldingAgent : IBoardAgentRunner
    {
        private readonly SemaphoreSlim _finish = new(0);
        public ConcurrentQueue<string> Prompts { get; } = new();
        public void FinishTurn() => _finish.Release();

        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Prompts.Enqueue(prompt);
            yield return new ToolCallStartEvent(new ToolCall { Name = "edit_file", Arguments = new() { ["path"] = "a.cs" } });
            var broken = false;
            try { await _finish.WaitAsync(ct); }
            catch (OperationCanceledException) { broken = true; }
            yield return broken
                ? new AgentFinishedEvent(new AgentResponse { WasCancelled = true })
                : new AgentFinishedEvent(new AgentResponse { Content = "done" });
        }
    }

    private MissionControlViewModel Create(HoldingAgent agent, BoardChangeReader? changes = null) =>
        new(new Isolation(_worktree), agent, dir => new BoardStore(Path.Combine(_root, "boards", Path.GetFileName(dir), "board.json")),
            a => _ui.Enqueue(a), changes: changes);

    private void Pump()
    {
        while (_ui.TryDequeue(out var action)) action();
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Pump();
            if (condition()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private async Task<(MissionControlViewModel Vm, BoardCardViewModel Card, HoldingAgent Agent)> RunningCardAsync(BoardChangeReader? changes = null)
    {
        var agent = new HoldingAgent();
        var vm = Create(agent, changes);
        vm.SetProject(Path.Combine(_root, "app"));
        vm.NewTaskText = "Add a cache";
        vm.AddCardCommand.Execute(null);
        Pump();
        var card = vm.Backlog.Single();
        card.StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.Running.Count == 1 && vm.Running[0].CanSteer && agent.Prompts.Count == 1);
        return (vm, vm.Running[0], agent);
    }

    // ------------------------------------------------------------------ pause, message, redirect

    [Fact]
    public async Task ARunningCard_OffersPauseAndMessage_AndSendingClearsTheBox()
    {
        var (vm, card, agent) = await RunningCardAsync();

        Assert.True(card.CanPause);
        Assert.False(card.CanResume);
        Assert.False(card.SendMessageCommand.CanExecute(null)); // nothing typed yet

        card.MessageText = "use the existing Result type";
        Assert.True(card.SendMessageCommand.CanExecute(null));
        card.SendMessageCommand.Execute(null);

        Assert.Equal(string.Empty, card.MessageText);
        await WaitUntilAsync(() => agent.Prompts.Count == 2);
        Assert.Contains("use the existing Result type", agent.Prompts.ToArray()[1]);
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }

    [Fact]
    public async Task APausedCard_ShowsItsState_AndResumeBringsTheAgentBack()
    {
        var (vm, card, agent) = await RunningCardAsync();

        card.PauseCommand.Execute(null);
        await WaitUntilAsync(() => card.CanResume);
        Assert.Equal("❚❚ Paused", card.StatusText);
        Assert.False(card.CanPause);

        card.ResumeCommand.Execute(null);
        await WaitUntilAsync(() => card.CanPause && agent.Prompts.Count == 2);
        Assert.Equal("● Running", card.StatusText);
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }

    [Fact]
    public async Task Redirect_SendsAChangeOfDirection()
    {
        var (vm, card, agent) = await RunningCardAsync();

        card.MessageText = "cache in Redis";
        card.RedirectCommand.Execute(null);

        await WaitUntilAsync(() => agent.Prompts.Count == 2);
        Assert.Contains("Change of direction", agent.Prompts.ToArray()[1]);
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }

    [Fact]
    public async Task AFinishedCard_OffersNoControls()
    {
        var (vm, _, agent) = await RunningCardAsync();
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        var card = vm.NeedsReview.Single();
        Assert.False(card.CanSteer);
        Assert.False(card.CanPause);
        Assert.False(card.CanResume);
    }

    // ------------------------------------------------------------------ the terminal

    [Fact]
    public async Task OpenTerminal_AsksForAShellInTheCardsWorktree()
    {
        var (vm, card, agent) = await RunningCardAsync();
        (string Directory, string Title)? request = null;
        vm.OpenTerminalRequested += (dir, title) => request = (dir, title);

        Assert.True(card.CanOpenTerminal);
        card.OpenTerminalCommand.Execute(null);

        Assert.Equal(_worktree, request!.Value.Directory);
        Assert.Contains("Add a cache", request.Value.Title);
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }

    [Fact]
    public async Task OpenTerminal_ExplainsWhenTheWorktreeIsGone()
    {
        var (vm, card, agent) = await RunningCardAsync();
        var asked = false;
        vm.OpenTerminalRequested += (_, _) => asked = true;
        Directory.Delete(_worktree, true);

        card.OpenTerminalCommand.Execute(null);

        Assert.False(asked);
        Assert.Contains("no longer exists", vm.Notice);
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }

    // ------------------------------------------------------------------ the changes

    private sealed class FakeChanges : BoardChangeReader
    {
        public override Task<CardChangeSet> ListAsync(string projectDirectory, BoardCard card, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardChangeSet(new[]
            {
                new CardFileChange("src/A.cs", CardFileKind.Modified),
                new CardFileChange("src/B.cs", CardFileKind.Added)
            }, null, "base"));

        public override Task<CardFileDiff> ReadAsync(string projectDirectory, BoardCard card, CardChangeSet set, CardFileChange file, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardFileDiff("old " + file.Path, "new " + file.Path, "Before the card", "In the worktree"));
    }

    [Fact]
    public async Task ShowChanges_ListsTheFiles_AndOpeningOneAsksForItsDiff()
    {
        var (vm, card, agent) = await RunningCardAsync(new FakeChanges());
        (string Path, string Old, string New, string OldLabel, string NewLabel)? diff = null;
        vm.ShowDiffRequested += (p, o, n, ol, nl) => diff = (p, o, n, ol, nl);

        await card.ShowChangesCommand.ExecuteAsync(null);
        Pump();

        Assert.True(vm.IsChangesOpen);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, vm.ChangedFiles.Select(f => f.Path));
        Assert.Equal(new[] { "M", "A" }, vm.ChangedFiles.Select(f => f.Badge));

        await vm.ChangedFiles[0].OpenCommand.ExecuteAsync(null);
        Pump();
        Assert.Equal(("src/A.cs", "old src/A.cs", "new src/A.cs", "Before the card", "In the worktree"), diff);

        vm.CloseChangesCommand.Execute(null);
        Assert.False(vm.IsChangesOpen);
        Assert.Empty(vm.ChangedFiles);
        agent.FinishTurn();
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);
    }
}

/// <summary>E11-S3: reading what a card changed from a real git repository.</summary>
public sealed class BoardChangeReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "board-changes-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");

    public BoardChangeReaderTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Git(string directory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        return output;
    }

    private void Write(string directory, string path, string text)
    {
        var full = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void InitRepo()
    {
        Git(Repo, "init", "-q", "-b", "main");
        Write(Repo, "a.txt", "one\n");
        Write(Repo, "b.txt", "two\n");
        Git(Repo, "add", "-A");
        Git(Repo, "commit", "-q", "-m", "base");
    }

    [Fact]
    public async Task AfterTheWorktreeIsGone_TheBranchIsCompared_WithWhereItLeftTheProject()
    {
        InitRepo();
        Git(Repo, "switch", "-q", "-c", "card/x");
        Write(Repo, "a.txt", "one\nchanged\n");
        Write(Repo, "c.txt", "new\n");
        File.Delete(Path.Combine(Repo, "b.txt"));
        Git(Repo, "add", "-A");
        Git(Repo, "commit", "-q", "-m", "card");
        Git(Repo, "switch", "-q", "main");
        Write(Repo, "later.txt", "main moved on\n"); // the project moved on after the card forked
        Git(Repo, "add", "-A");
        Git(Repo, "commit", "-q", "-m", "later");

        var reader = new BoardChangeReader();
        var card = new BoardCard { Branch = "card/x" };
        var set = await reader.ListAsync(Repo, card);

        Assert.Null(set.Problem);
        Assert.Equal(
            new[] { "a.txt:Modified", "b.txt:Deleted", "c.txt:Added" },
            set.Files.Select(f => $"{f.Path}:{f.Kind}"));

        var diff = await reader.ReadAsync(Repo, card, set, set.Files.Single(f => f.Path == "a.txt"));
        Assert.Equal("one\n", diff.Baseline);
        Assert.Equal("one\nchanged\n", diff.Current);
        var added = await reader.ReadAsync(Repo, card, set, set.Files.Single(f => f.Path == "c.txt"));
        Assert.Equal(string.Empty, added.Baseline);
        var deleted = await reader.ReadAsync(Repo, card, set, set.Files.Single(f => f.Path == "b.txt"));
        Assert.Equal("two\n", deleted.Baseline);
        Assert.Equal(string.Empty, deleted.Current);
    }

    [Fact]
    public async Task WhileTheWorktreeExists_UncommittedAndNewFilesAreIncluded()
    {
        InitRepo();
        var worktree = Path.Combine(_root, "wt");
        Git(Repo, "worktree", "add", "-q", "-b", "card/y", worktree);
        Write(worktree, "a.txt", "one\nedited, not committed\n");
        Write(worktree, "src/new.cs", "class New {}\n"); // untracked

        var reader = new BoardChangeReader();
        var card = new BoardCard { Branch = "card/y", WorktreePath = worktree };
        var set = await reader.ListAsync(Repo, card);

        Assert.Equal(new[] { "a.txt:Modified", "src/new.cs:Added" }, set.Files.Select(f => $"{f.Path}:{f.Kind}"));
        var diff = await reader.ReadAsync(Repo, card, set, set.Files[0]);
        Assert.Equal("one\n", diff.Baseline);
        Assert.Equal("one\nedited, not committed\n", diff.Current);
    }

    [Fact]
    public async Task ACardWithoutABranch_ExplainsWhyThereIsNothingToShow()
    {
        var set = await new BoardChangeReader().ListAsync(Repo, new BoardCard());
        Assert.NotNull(set.Problem);
        Assert.Empty(set.Files);
    }

    [Fact]
    public async Task ABranchThatWasDeleted_IsReportedNotThrown()
    {
        InitRepo();
        var set = await new BoardChangeReader().ListAsync(Repo, new BoardCard { Branch = "card/gone" });
        Assert.Contains("not found", set.Problem);
    }

    [Fact]
    public void NameStatus_ParsesRenames()
    {
        var files = BoardChangeReader.ParseNameStatus("M\ta.cs\nR100\told.cs\tnew.cs\nA\tb.cs\nD\tc.cs\n").ToList();

        Assert.Equal(CardFileKind.Modified, files[0].Kind);
        Assert.Equal(("new.cs", "old.cs", CardFileKind.Renamed), (files[1].Path, files[1].OldPath, files[1].Kind));
        Assert.Equal(CardFileKind.Added, files[2].Kind);
        Assert.Equal(CardFileKind.Deleted, files[3].Kind);
    }
}
