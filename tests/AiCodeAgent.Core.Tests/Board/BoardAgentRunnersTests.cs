using System.Collections.Concurrent;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Board;

/// <summary>E11-S2: running a card with a character, a flow or an external agent instead of the default agent.</summary>
public sealed class BoardAgentRunnersTests
{
    // ------------------------------------------------------------------ fakes and helpers

    private sealed class FakeExternal : IExternalAgentRunner
    {
        public ConcurrentQueue<ExternalAgentRequest> Requests { get; } = new();
        public Func<ExternalAgentRequest, Action<string>?, CancellationToken, Task<ExternalAgentResult>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(new ExternalAgentResult("Done by the external agent.", "end_turn", 0, Array.Empty<ExternalAgentPermission>()));

        public Task<ExternalAgentResult> RunAsync(ExternalAgentRequest request, Action<string>? onProgress, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Handler(request, onProgress, cancellationToken);
        }
    }

    private sealed class FakeDefaultAgent : IBoardAgentRunner
    {
        public ConcurrentQueue<(string Prompt, AgentOptions Options)> Calls { get; } = new();

        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Enqueue((prompt, options));
            await Task.Yield();
            yield return new AgentFinishedEvent(new AgentResponse { Content = "Default answer." });
        }
    }

    private sealed class FakeIsolation : IWorkspaceIsolation
    {
        private int _counter;
        public ConcurrentQueue<WorkspaceLease> Leases { get; } = new();

        public Task<WorkspaceLease?> AcquireAsync(string baseDirectory, string sessionId, string agentId, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _counter);
            var root = Path.Combine(Path.GetTempPath(), "wt-agents", n.ToString());
            var lease = new WorkspaceLease(agentId, $"agent/{sessionId}/{n}", root, root, baseDirectory);
            Leases.Enqueue(lease);
            return Task.FromResult<WorkspaceLease?>(lease);
        }

        public Task<WorkspaceMergeResult> CompleteAsync(WorkspaceLease lease, string summary, bool merge, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceMergeResult(WorkspaceMergeOutcome.Kept, lease.Branch, "ok", Array.Empty<string>(), lease.RootPath));
    }

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> stream)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in stream) events.Add(e);
        return events;
    }

    private static BoardRunRequest Request(string prompt = "Fix the bug", PermissionMode mode = PermissionMode.AutoEdit, string dir = "/wt/1") =>
        new(prompt, "card-x-1", new AgentOptions { WorkingDirectory = dir, PermissionMode = mode, NonInteractive = true });

    private static ICharacterRegistry Registry(params CharacterInfo[] characters)
    {
        var registry = Substitute.For<ICharacterRegistry>();
        registry.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<CharacterInfo?>(null));
        foreach (var c in characters)
            registry.GetAsync(c.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<CharacterInfo?>(c));
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CharacterInfo>>(characters));
        return registry;
    }

    private static AgentFinishedEvent Finished(IEnumerable<AgentEvent> events) => Assert.Single(events.OfType<AgentFinishedEvent>());

    // ------------------------------------------------------------------ external agents

    [Fact]
    public async Task AnExternalAgent_RunsInTheWorktree_AndItsAnswerIsTheResult()
    {
        var external = new FakeExternal
        {
            Handler = (_, progress, _) =>
            {
                progress!("ACP tool: Edit src/A.cs");
                progress("ACP tool: Edit src/B.cs");
                progress("Permission allowed: edit");
                return Task.FromResult(new ExternalAgentResult("Changed two files.", "end_turn", 2, Array.Empty<ExternalAgentPermission>())
                {
                    Usage = new TokenUsage { PromptTokens = 1200, CompletionTokens = 300 }
                });
            }
        };
        var runner = new ExternalBoardRunner(external, new ExternalAgentDetector(_ => "/bin/x"));

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForPreset(ExternalAgentPresets.Codex), Request("Add tests", dir: "/wt/7")));

        var request = Assert.Single(external.Requests);
        Assert.Equal("npx", request.Command);
        Assert.Equal(new[] { "-y", "@zed-industries/codex-acp" }, request.Args);
        Assert.Equal("/wt/7", request.WorkingDirectory);
        Assert.Equal("Add tests", request.Prompt);
        Assert.Equal(PermissionMode.AutoEdit, request.PermissionMode);
        Assert.Null(request.Ask); // nobody is at a card to answer

        var progress = events.OfType<BoardProgressEvent>().ToList();
        Assert.Equal(2, progress.Count(p => p.IsToolCall));
        Assert.Contains(progress, p => p.Action == "Edit src/B.cs" && p.IsToolCall);
        Assert.Contains(progress, p => p.Action == "Permission allowed: edit" && !p.IsToolCall);
        Assert.Contains(progress, p => p.Tokens == 1500);
        Assert.Equal("Changed two files.", Finished(events).Response.Content);
    }

    [Fact]
    public async Task AnExternalAgent_ThatWasRefusedSomething_SaysSo_InTheSummary()
    {
        var external = new FakeExternal
        {
            Handler = (_, _, _) => Task.FromResult(new ExternalAgentResult("Tests not run.", "end_turn", 1, new[]
            {
                new ExternalAgentPermission("Run dotnet test", "execute", false),
                new ExternalAgentPermission("Run dotnet test", "execute", false),
                new ExternalAgentPermission("Edit A.cs", "edit", true)
            }))
        };
        var runner = new ExternalBoardRunner(external, new ExternalAgentDetector(_ => "/bin/x"));

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForPreset(ExternalAgentPresets.ClaudeCode), Request()));

        Assert.Equal("Tests not run.\n\nNot allowed while running unattended: Run dotnet test.", Finished(events).Response.Content);
    }

    [Fact]
    public async Task AnExternalAgent_ThatRefuses_OrFails_EndsAsAnError()
    {
        var refusing = new FakeExternal { Handler = (_, _, _) => Task.FromResult(new ExternalAgentResult("", "refusal", 0, Array.Empty<ExternalAgentPermission>())) };
        var failing = new FakeExternal { Handler = (_, _, _) => throw new ExternalAgentException("The agent needs you to sign in first.") };
        var detector = new ExternalAgentDetector(_ => "/bin/x");

        var refused = await CollectAsync(new ExternalBoardRunner(refusing, detector).RunAsync(AgentTarget.ForPreset(ExternalAgentPresets.Codex), Request()));
        var failed = await CollectAsync(new ExternalBoardRunner(failing, detector).RunAsync(AgentTarget.ForPreset(ExternalAgentPresets.Codex), Request()));

        Assert.Contains("refused", Assert.Single(refused.OfType<AgentErrorEvent>()).Error.Message);
        Assert.Equal("The agent needs you to sign in first.", Assert.Single(failed.OfType<AgentErrorEvent>()).Error.Message);
    }

    [Fact]
    public async Task StoppingAnExternalCard_EndsWithACancelledFinish()
    {
        using var cts = new CancellationTokenSource();
        var external = new FakeExternal
        {
            Handler = async (_, _, token) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            }
        };
        var runner = new ExternalBoardRunner(external, new ExternalAgentDetector(_ => "/bin/x"));

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForPreset(ExternalAgentPresets.GeminiCli), Request(), cts.Token));

        Assert.True(Finished(events).Response.WasCancelled);
    }

    [Fact]
    public async Task AnExternalAgent_ThatIsNotInstalled_FailsTheCheck_WithWhatToDo()
    {
        var runner = new ExternalBoardRunner(new FakeExternal(), new ExternalAgentDetector(_ => null));

        var problem = await runner.CheckAsync(AgentTarget.ForPreset(ExternalAgentPresets.ClaudeCode), default);

        Assert.StartsWith("Claude Code is not ready:", problem);
        Assert.Contains("'claude' was not found on the PATH", problem);
        Assert.Contains("sign in", problem);
    }

    [Fact]
    public async Task ACustomExternalCommand_RunsAsGiven()
    {
        var external = new FakeExternal();
        var runner = new ExternalBoardRunner(external, new ExternalAgentDetector(_ => null));
        var target = AgentTarget.ForExternal("my-agent", new[] { "--acp" }, "My agent");

        Assert.Null(await runner.CheckAsync(target, default));
        await CollectAsync(runner.RunAsync(target, Request()));

        var request = Assert.Single(external.Requests);
        Assert.Equal("my-agent", request.Command);
        Assert.Equal(new[] { "--acp" }, request.Args);
    }

    // ------------------------------------------------------------------ characters

    private static CharacterInfo Builder(PermissionMode? mode = null) => new()
    {
        Id = "builder", DisplayName = "Builder", Persona = "You build things carefully.", BaseRole = "implementer", PermissionMode = mode
    };

    [Fact]
    public async Task ACharacter_ShapesTheRun_OfTheDefaultAgent()
    {
        var defaultAgent = new FakeDefaultAgent();
        var runner = new CharacterBoardRunner(Registry(Builder()), defaultAgent);

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForCharacter("builder"), Request("Do it", dir: "/wt/3")));

        var call = Assert.Single(defaultAgent.Calls);
        Assert.Equal("Do it", call.Prompt);
        Assert.Equal("builder", call.Options.CharacterId);
        Assert.Contains("You build things carefully.", call.Options.RoleSystemPrompt);
        Assert.Equal("/wt/3", call.Options.WorkingDirectory);
        Assert.True(call.Options.NonInteractive);
        Assert.Equal("Default answer.", Finished(events).Response.Content);
    }

    [Theory]
    [InlineData(PermissionMode.Ask, PermissionMode.AutoEdit)]   // asking would refuse every edit: nobody is there to answer
    [InlineData(PermissionMode.Plan, PermissionMode.Plan)]      // a read-only character stays read-only
    [InlineData(PermissionMode.FullAuto, PermissionMode.FullAuto)]
    public async Task ACharactersPermissionMode_IsKept_ExceptAskingWhichCannotWork(PermissionMode characterMode, PermissionMode expected)
    {
        var defaultAgent = new FakeDefaultAgent();
        var runner = new CharacterBoardRunner(Registry(Builder(characterMode)), defaultAgent);

        await CollectAsync(runner.RunAsync(AgentTarget.ForCharacter("builder"), Request()));

        Assert.Equal(expected, Assert.Single(defaultAgent.Calls).Options.PermissionMode);
    }

    [Fact]
    public async Task ACharacter_ThatCannotRun_FailsTheCheck()
    {
        var registry = Registry(
            Builder(),
            new CharacterInfo { Id = "base", DisplayName = "Base", IsTemplate = true },
            new CharacterInfo { Id = "broken", DisplayName = "Broken", ValidationErrors = new[] { "unknown tool 'x'." } },
            new CharacterInfo { Id = "claude", DisplayName = "Claude", Runtime = "acp", Command = "claude-acp" });
        var withoutExternal = new CharacterBoardRunner(registry, new FakeDefaultAgent());

        Assert.Null(await withoutExternal.CheckAsync(AgentTarget.ForCharacter("builder"), default));
        Assert.Equal("Character 'ghost' was not found.", await withoutExternal.CheckAsync(AgentTarget.ForCharacter("ghost"), default));
        Assert.Contains("template", await withoutExternal.CheckAsync(AgentTarget.ForCharacter("base"), default));
        Assert.Contains("unknown tool 'x'", await withoutExternal.CheckAsync(AgentTarget.ForCharacter("broken"), default));
        Assert.Contains("cannot start external agents", await withoutExternal.CheckAsync(AgentTarget.ForCharacter("claude"), default));
    }

    [Fact]
    public async Task AnExternalCharacter_IsStartedAsAProgram_WithItsInstructionsFirst()
    {
        var external = new FakeExternal();
        var character = new CharacterInfo
        {
            Id = "claude-reviewer", DisplayName = "Claude reviewer", Runtime = "acp", Command = "claude-acp",
            Args = new[] { "--x" }, Persona = "You review."
        };
        var runner = new CharacterBoardRunner(
            Registry(character), new FakeDefaultAgent(), new ExternalBoardRunner(external, new ExternalAgentDetector(_ => null)),
            externalTargetOf: id => new ExternalAgentTarget("claude-acp", new[] { "--x" }, id, null, "Instructions for your role: review."));

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForCharacter("claude-reviewer"), Request("Check the diff")));

        var request = Assert.Single(external.Requests);
        Assert.Equal("claude-acp", request.Command);
        Assert.Equal(new[] { "--x" }, request.Args);
        Assert.Equal("Instructions for your role: review.\n\nCheck the diff", request.Prompt);
        Assert.Equal("Done by the external agent.", Finished(events).Response.Content);
    }

    // ------------------------------------------------------------------ flows

    private static SdlcPipelineDefinition Flow(string name = "feature-team") => new()
    {
        Name = name,
        Edges = new List<FlowEdge> { new() { From = "a", To = "b" } }
    };

    private static FlowHeadlessOutcome Outcome(int code, string reason = "", params string[] denied)
    {
        var result = new FlowRunResult(code == 0 ? FlowRunStatus.Succeeded : FlowRunStatus.Failed, "Flow finished.", new[]
        {
            new FlowNodeRunSummary("a", null, FlowNodeState.Done, 1, null, TimeSpan.Zero, "Plan text", null),
            new FlowNodeRunSummary("b", null, FlowNodeState.Done, 1, null, TimeSpan.Zero, "Final report", null)
        })
        {
            StepRuns = new[] { new FlowStepRunUsage("a", 1, null, null, 100, 50, 0, 0, 0.42m, false, TimeSpan.Zero) }
        };
        return new FlowHeadlessOutcome(code, result, reason, denied);
    }

    [Fact]
    public async Task AFlow_RunsOnTheCardsWorktree_WithTheCardsTask()
    {
        SdlcPipelineDefinition? seenPipeline = null;
        string? seenTask = null, seenDir = null;
        FlowHeadlessOptions? seenOptions = null;
        var runner = new FlowBoardRunner(name => name == "feature-team" ? Flow() : null, (pipeline, task, dir, options, _) =>
        {
            seenPipeline = pipeline; seenTask = task; seenDir = dir; seenOptions = options;
            options.Log!("▶ plan");
            options.Log("✓ plan");
            return Task.FromResult(Outcome(0));
        });

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForFlow("feature-team"), Request("Add a login page", dir: "/wt/9")));

        Assert.Equal("feature-team", seenPipeline!.Name);
        Assert.Equal("Add a login page", seenTask);
        Assert.Equal("/wt/9", seenDir);
        Assert.Equal(PermissionMode.AutoEdit, seenOptions!.Permission);

        var progress = events.OfType<BoardProgressEvent>().ToList();
        Assert.Contains(progress, p => p.Action == "▶ plan");
        Assert.Contains(progress, p => p.CostUsd == 0.42m && p.Tokens == 150);
        Assert.Equal("Final report", Finished(events).Response.Content); // the last step's output
    }

    [Fact]
    public async Task AFlow_ThatFails_EndsAsAnError_WithTheReasonAndWhatWasDenied()
    {
        var runner = new FlowBoardRunner(_ => Flow(), (_, _, _, _, _) =>
            Task.FromResult(Outcome(1, "Step 'review' asks for confirmation, which a non-interactive run cannot give.", "review: run_command")));

        var events = await CollectAsync(runner.RunAsync(AgentTarget.ForFlow("feature-team"), Request()));

        var message = Assert.Single(events.OfType<AgentErrorEvent>()).Error.Message;
        Assert.StartsWith("Step 'review' asks for confirmation", message);
        Assert.Contains("Not allowed while running unattended: review: run_command.", message);
        Assert.Empty(events.OfType<AgentFinishedEvent>());
    }

    [Fact]
    public async Task AFlow_ThatIsMissingOrLinear_FailsTheCheck()
    {
        var runner = new FlowBoardRunner(
            name => name == "linear" ? new SdlcPipelineDefinition { Name = "linear" } : name == "ok" ? Flow("ok") : null,
            (_, _, _, _, _) => Task.FromResult(Outcome(0)));

        Assert.Null(await runner.CheckAsync(AgentTarget.ForFlow("ok"), default));
        Assert.Equal("Flow 'nope' was not found.", await runner.CheckAsync(AgentTarget.ForFlow("nope"), default));
        Assert.Contains("linear pipeline", await runner.CheckAsync(AgentTarget.ForFlow("linear"), default));
    }

    // ------------------------------------------------------------------ the picker's list

    [Fact]
    public async Task TheCatalog_ListsTheDefault_Characters_Flows_AndExternalAgents_WithAvailability()
    {
        var registry = Registry(
            Builder(),
            new CharacterInfo { Id = "base", DisplayName = "Base", IsTemplate = true },
            new CharacterInfo { Id = "broken", DisplayName = "Broken", ValidationErrors = new[] { "bad" } },
            new CharacterInfo { Id = "claude-reviewer", DisplayName = "Claude reviewer", Avatar = "🔍", Runtime = "acp", Command = "claude-acp" });
        var catalog = new BoardAgentCatalog(
            registry,
            () => new (string, string?)[] { ("zeta", null), ("feature-team", "Plan, build, review") },
            new ExternalAgentDetector(name => name is "codex" or "npx" ? "/bin/" + name : null));

        var choices = await catalog.ListAsync();

        Assert.Equal(AgentTarget.Default, choices[0].Target);
        Assert.Equal(
            new[] { "Default agent", "Builder", "🔍 Claude reviewer (external)", "Flow: feature-team", "Flow: zeta", "Claude Code (ACP) — not installed", "Codex (ACP)", "Gemini CLI (ACP) — not installed" },
            choices.Select(c => c.Label));
        Assert.DoesNotContain(choices, c => c.Label.Contains("Base") || c.Label.Contains("Broken"));
        Assert.Equal(new[] { true, true, true, true, true, false, true, false }, choices.Select(c => c.IsAvailable));
        Assert.Equal(AgentTarget.ForCharacter("builder", "Builder"), choices[1].Target);
        Assert.Equal("Plan, build, review", choices.Single(c => c.Label == "Flow: feature-team").Detail);
        Assert.Contains("claude", choices.Single(c => c.Label.StartsWith("Claude Code")).Detail);
    }

    [Fact]
    public async Task TheCatalog_WithoutAnySources_OffersOnlyTheDefaultAgent()
    {
        var choices = await new BoardAgentCatalog().ListAsync();

        Assert.Equal(AgentTarget.Default, Assert.Single(choices).Target);
    }

    // ------------------------------------------------------------------ on the board

    private static (TaskBoard Board, BoardRunner Runner, FakeIsolation Isolation, FakeDefaultAgent Default) Board(IBoardTargetRunner? targets)
    {
        var board = new TaskBoard();
        var isolation = new FakeIsolation();
        var defaultAgent = new FakeDefaultAgent();
        return (board, new BoardRunner(board, isolation, defaultAgent, "/repo", targets: targets), isolation, defaultAgent);
    }

    [Fact]
    public async Task ACardWithAnExternalAgent_RunsThroughIt_AndShowsItsProgress()
    {
        var external = new FakeExternal
        {
            Handler = (_, progress, _) =>
            {
                progress!("ACP tool: Edit src/A.cs");
                return Task.FromResult(new ExternalAgentResult("Edited.", "end_turn", 1, Array.Empty<ExternalAgentPermission>())
                {
                    Usage = new TokenUsage { PromptTokens = 400, CompletionTokens = 100 }
                });
            }
        };
        var router = new BoardTargetRouter(new ExternalBoardRunner(external, new ExternalAgentDetector(_ => "/bin/x")));
        var (board, runner, isolation, defaultAgent) = Board(router);
        var id = board.Add("Refactor the parser", agent: AgentTarget.ForPreset(ExternalAgentPresets.ClaudeCode)).Id;

        await runner.Start(id)!;

        var card = board.Get(id)!;
        Assert.Equal(CardOutcome.Completed, card.Outcome);
        Assert.Equal("Edited.", card.Summary);
        Assert.Equal(1, card.ToolCalls);
        Assert.Equal(500, card.Tokens);
        Assert.Equal("Edit src/A.cs", card.LastAction);
        Assert.NotNull(card.Branch);
        Assert.Equal(isolation.Leases.Single().WorkingDirectory, Assert.Single(external.Requests).WorkingDirectory);
        Assert.Empty(defaultAgent.Calls);
    }

    [Fact]
    public async Task ACardWithAFlow_ShowsTheFlowsCost()
    {
        var router = new BoardTargetRouter(new FlowBoardRunner(_ => Flow(), (_, _, _, _, _) => Task.FromResult(Outcome(0))));
        var (board, runner, _, _) = Board(router);
        var id = board.Add("Ship it", agent: AgentTarget.ForFlow("feature-team")).Id;

        await runner.Start(id)!;

        var card = board.Get(id)!;
        Assert.Equal(CardOutcome.Completed, card.Outcome);
        Assert.Equal(0.42m, card.CostUsd);
        Assert.Equal(150, card.Tokens);
        Assert.Equal("Final report", card.Summary);
    }

    [Fact]
    public async Task ACardWhoseAgentCannotRun_FailsBeforeAWorktreeIsMade()
    {
        var router = new BoardTargetRouter(new CharacterBoardRunner(Registry(), new FakeDefaultAgent()));
        var (board, runner, isolation, defaultAgent) = Board(router);
        var id = board.Add("Do it", agent: AgentTarget.ForCharacter("ghost")).Id;

        await runner.Start(id)!;

        var card = board.Get(id)!;
        Assert.Equal(BoardColumn.NeedsReview, card.Column);
        Assert.Equal(CardOutcome.Failed, card.Outcome);
        Assert.Equal("Character 'ghost' was not found.", card.Summary);
        Assert.Empty(isolation.Leases);
        Assert.Empty(defaultAgent.Calls);
    }

    [Fact]
    public async Task ACardWithAnAgentKindThisBuildCannotRun_SaysSo()
    {
        var (board, runner, isolation, _) = Board(targets: null);
        var id = board.Add("Do it", agent: AgentTarget.ForFlow("feature-team")).Id;

        await runner.Start(id)!;

        var card = board.Get(id)!;
        Assert.Equal(CardOutcome.Failed, card.Outcome);
        Assert.Contains("Flow feature-team cannot run on the board in this build", card.Summary);
        Assert.Empty(isolation.Leases);
    }

    [Fact]
    public async Task ACardWithTheDefaultAgent_NeverTouchesTheTargetRunners()
    {
        var targets = Substitute.For<IBoardTargetRunner>();
        var (board, runner, _, defaultAgent) = Board(targets);
        var id = board.Add("Plain task").Id;

        await runner.Start(id)!;

        Assert.Equal("Default answer.", board.Get(id)!.Summary);
        Assert.Single(defaultAgent.Calls);
        await targets.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
    }
}
