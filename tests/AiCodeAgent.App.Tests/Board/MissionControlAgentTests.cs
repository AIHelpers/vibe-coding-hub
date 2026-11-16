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
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.Board;

/// <summary>E11-S2: choosing who works on a card: the default agent, a flow or an external agent such as Claude Code.</summary>
public sealed class MissionControlAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mission-agents-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<Action> _ui = new();

    public MissionControlAgentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeIsolation : IWorkspaceIsolation
    {
        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
        {
            var path = Path.Combine(Path.GetTempPath(), "wt-agents-app");
            return Task.FromResult<WorkspaceLease?>(new WorkspaceLease(agentId, "agent/" + sessionId, path, path, baseDirectory));
        }

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceMergeResult(WorkspaceMergeOutcome.Kept, lease.Branch, "kept", Array.Empty<string>(), lease.RootPath));
    }

    private sealed class DefaultAgent : IBoardAgentRunner
    {
        public int Started;

        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            await Task.Yield();
            yield return new AgentFinishedEvent(new AgentResponse { Content = "Default done." });
        }
    }

    private static readonly SdlcPipelineDefinition FeatureFlow = new()
    {
        Name = "feature-team",
        Description = "Plan, build, review",
        Edges = new List<FlowEdge> { new() { From = "a", To = "b" } }
    };

    private static FlowBoardRunner FlowRunner(Action<string>? onTask = null) => new(
        name => name == "feature-team" ? FeatureFlow : null,
        (_, task, _, _, _) =>
        {
            onTask?.Invoke(task);
            var result = new FlowRunResult(FlowRunStatus.Succeeded, "ok", new[]
            {
                new FlowNodeRunSummary("b", null, FlowNodeState.Done, 1, null, TimeSpan.Zero, "Flow report", null)
            });
            return Task.FromResult(new FlowHeadlessOutcome(0, result, string.Empty, Array.Empty<string>()));
        });

    /// <summary>Claude Code is not installed; Codex is. The flow "feature-team" exists.</summary>
    private static BoardAgentCatalog Catalog() => new(
        null,
        () => new (string, string?)[] { ("feature-team", "Plan, build, review") },
        new ExternalAgentDetector(name => name is "codex" or "npx" ? "/bin/" + name : null));

    private MissionControlViewModel Create(DefaultAgent agent, BoardAgentCatalog? catalog = null, IBoardTargetRunner? targets = null) =>
        new(new FakeIsolation(), agent, dir => new BoardStore(Path.Combine(_root, "boards", Path.GetFileName(dir), "board.json")),
            a => _ui.Enqueue(a), targets, catalog);

    private void Pump()
    {
        while (_ui.TryDequeue(out var action)) action();
    }

    private async Task<MissionControlViewModel> OpenAsync(BoardAgentCatalog? catalog = null, IBoardTargetRunner? targets = null, DefaultAgent? agent = null)
    {
        var vm = Create(agent ?? new DefaultAgent(), catalog, targets);
        vm.SetProject(Path.Combine(_root, "app"));
        await vm.RefreshAgentsAsync();
        Pump();
        return vm;
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

    private static AgentChoice Choice(MissionControlViewModel vm, string label) => vm.AgentChoices.Single(c => c.Label == label);

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task WithoutAnyAgentSources_OnlyTheDefaultAgentIsOffered_AndNoPickerShows()
    {
        var vm = await OpenAsync();

        Assert.Equal(AgentTarget.Default, Assert.Single(vm.AgentChoices).Target);
        Assert.False(vm.HasAgentChoices);
        vm.NewTaskText = "Task";
        vm.AddCardCommand.Execute(null);
        Pump();
        Assert.False(vm.Backlog.Single().CanChooseAgent);
        Assert.False(vm.Backlog.Single().ShowAgentText); // the default agent needs no label
    }

    [Fact]
    public async Task ThePicker_ListsFlows_AndExternalAgents_WithWhatIsInstalled()
    {
        var vm = await OpenAsync(Catalog());

        Assert.True(vm.HasAgentChoices);
        Assert.Equal(
            new[] { "Default agent", "Flow: feature-team", "Claude Code (ACP) — not installed", "Codex (ACP)", "Gemini CLI (ACP) — not installed" },
            vm.AgentChoices.Select(c => c.Label));
        Assert.Equal(AgentTarget.Default, vm.SelectedAgent!.Target);
    }

    // ------------------------------------------------------------------ new cards

    [Fact]
    public async Task ANewCard_GetsTheChosenAgent_AndTheChoiceStaysForTheNextCard()
    {
        var vm = await OpenAsync(Catalog());
        vm.SelectedAgent = Choice(vm, "Codex (ACP)");

        vm.NewTaskText = "First";
        vm.AddCardCommand.Execute(null);
        vm.NewTaskText = "Second";
        vm.AddCardCommand.Execute(null);
        Pump();

        Assert.All(vm.Backlog, c => Assert.Equal(AgentTarget.ForPreset(ExternalAgentPresets.Codex), c.Card.Agent));
        Assert.Equal("Codex (ACP)", vm.SelectedAgent!.Label);
    }

    [Fact]
    public async Task AnAgentThatIsNotInstalled_CannotBeChosen_AndTheNoticeSaysWhatToDo()
    {
        var vm = await OpenAsync(Catalog());
        vm.SelectedAgent = Choice(vm, "Codex (ACP)");

        vm.SelectedAgent = Choice(vm, "Claude Code (ACP) — not installed");

        Assert.Equal("Codex (ACP)", vm.SelectedAgent!.Label); // unchanged
        Assert.True(vm.HasNotice);
        Assert.Contains("'claude' was not found on the PATH", vm.Notice);
    }

    // ------------------------------------------------------------------ cards

    [Fact]
    public async Task TheAgent_OfACardInTheBacklog_CanBeChanged()
    {
        var vm = await OpenAsync(Catalog());
        vm.NewTaskText = "Task";
        vm.AddCardCommand.Execute(null);
        Pump();
        var card = vm.Backlog.Single();
        Assert.True(card.CanChooseAgent);
        Assert.Equal("Default agent", card.SelectedAgent!.Label);

        card.SelectedAgent = Choice(vm, "Flow: feature-team");
        Pump();

        Assert.Equal(AgentTarget.ForFlow("feature-team"), vm.Backlog.Single().Card.Agent);
        Assert.Equal("Flow: feature-team", vm.Backlog.Single().SelectedAgent!.Label);
    }

    [Fact]
    public async Task ACardCannotBeGivenAnAgentThatIsNotInstalled()
    {
        var vm = await OpenAsync(Catalog());
        vm.NewTaskText = "Task";
        vm.AddCardCommand.Execute(null);
        Pump();
        var card = vm.Backlog.Single();

        card.SelectedAgent = Choice(vm, "Gemini CLI (ACP) — not installed");
        Pump();

        Assert.Equal(AgentTarget.Default, vm.Backlog.Single().Card.Agent);
        Assert.Contains("'gemini' was not found", vm.Notice);
    }

    [Fact]
    public async Task ACardWhoseAgentWasRemoved_StaysVisible_AsNotFound()
    {
        var store = new BoardStore(Path.Combine(_root, "boards", "app", "board.json"));
        new TaskBoard(store).Add("Old task", agent: AgentTarget.ForFlow("deleted-flow"));

        var vm = await OpenAsync(Catalog());

        var card = vm.Backlog.Single();
        Assert.Equal("Flow deleted-flow (not found)", card.SelectedAgent!.Label);
        Assert.False(card.SelectedAgent.IsAvailable);
        Assert.Equal(AgentTarget.ForFlow("deleted-flow"), card.Card.Agent);
    }

    [Fact]
    public async Task OnceACardHasRun_ItsAgentIsShownAsText_NotAsAPicker()
    {
        var vm = await OpenAsync(Catalog(), new BoardTargetRouter(FlowRunner()));
        vm.SelectedAgent = Choice(vm, "Flow: feature-team");
        vm.NewTaskText = "Task";
        vm.AddCardCommand.Execute(null);
        Pump();

        vm.Backlog.Single().StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        var card = vm.NeedsReview.Single();
        Assert.False(card.CanChooseAgent);
        Assert.True(card.ShowAgentText);
        Assert.Equal("Agent: Flow feature-team", card.AgentText);
    }

    // ------------------------------------------------------------------ running

    [Fact]
    public async Task ACardWithAFlow_RunsTheFlow_NotTheDefaultAgent()
    {
        string? taskSeen = null;
        var defaultAgent = new DefaultAgent();
        var vm = await OpenAsync(Catalog(), new BoardTargetRouter(FlowRunner(t => taskSeen = t)), defaultAgent);
        vm.SelectedAgent = Choice(vm, "Flow: feature-team");
        vm.NewTaskText = "Build the settings page";
        vm.AddCardCommand.Execute(null);
        Pump();

        vm.Backlog.Single().StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        var card = vm.NeedsReview.Single();
        Assert.Equal("Build the settings page", taskSeen);
        Assert.Equal("Flow report", card.SummaryText);
        Assert.Equal(CardOutcome.Completed, card.Card.Outcome);
        Assert.Equal(0, defaultAgent.Started);
    }

    [Fact]
    public async Task ACardWithAFlow_ThatTheBuildCannotRun_FailsWithAClearMessage()
    {
        var vm = await OpenAsync(Catalog(), targets: null);
        vm.SelectedAgent = Choice(vm, "Flow: feature-team");
        vm.NewTaskText = "Task";
        vm.AddCardCommand.Execute(null);
        Pump();

        vm.Backlog.Single().StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.NeedsReview.Count == 1);

        Assert.Equal(CardOutcome.Failed, vm.NeedsReview.Single().Card.Outcome);
        Assert.Contains("cannot run on the board in this build", vm.NeedsReview.Single().SummaryText);
    }
}
