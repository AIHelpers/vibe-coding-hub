using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E6-S6: a character with <c>runtime: acp</c> runs its flow step on an external ACP agent.</summary>
public class ExternalCharacterTests : IDisposable
{
    private static readonly RolePresetLoader Presets = new(NullLogger<RolePresetLoader>.Instance);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ext-char-" + Guid.NewGuid().ToString("N"));
    private readonly CharacterRegistry _characters;
    private readonly SkillRegistry _skills;

    public ExternalCharacterTests()
    {
        var charactersDir = Path.Combine(_root, "characters");
        var skillsDir = Path.Combine(_root, "skills");
        Directory.CreateDirectory(charactersDir);
        Directory.CreateDirectory(Path.Combine(skillsDir, "code-review-checklist"));
        File.WriteAllText(Path.Combine(skillsDir, "code-review-checklist", "SKILL.md"),
            "---\nname: code-review-checklist\ndescription: Review checklist\n---\nCheck naming and tests.\n");
        File.WriteAllText(Path.Combine(charactersDir, "claude-code-reviewer.md"), """
            ---
            id: claude-code-reviewer
            display-name: Claude Code — Reviewer
            description: External reviewer
            runtime: acp
            command: claude-code-acp
            args: [--stdio]
            skills: [code-review-checklist]
            permission-mode: ask
            ---
            You review code carefully.
            """.Replace("\n            ", "\n").TrimStart());
        _characters = new CharacterRegistry(Presets, NullLogger<CharacterRegistry>.Instance, charactersDir);
        _skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, skillsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeExternalAgent : IExternalAgentRunner
    {
        public ExternalAgentRequest? Request { get; private set; }
        public bool? UserAnswer { get; private set; }
        public string Answer { get; init; } = "Looks good.\nOUTCOME: approved";
        public async Task<ExternalAgentResult> RunAsync(ExternalAgentRequest request, Action<string>? onProgress, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Ask != null) UserAnswer = await request.Ask(new ExternalPermissionQuestion("Run dotnet test", "execute"), cancellationToken);
            if (request.BeforeWrite != null)
            {
                await request.BeforeWrite(Path.Combine(request.WorkingDirectory, "x.txt"));
                request.AfterWrite?.Invoke(Path.Combine(request.WorkingDirectory, "x.txt"), "old", "new");
            }
            return new ExternalAgentResult(Answer, "end_turn", 1, Array.Empty<ExternalAgentPermission>())
            {
                Usage = new TokenUsage { PromptTokens = 1200, CompletionTokens = 300 }
            };
        }
    }

    private static SdlcPipelineDefinition FeatureTeam() => new()
    {
        Name = "feature-team",
        Stages = new()
        {
            Stage("design", "planner"),
            Stage("implement"),
            Stage("review", "reviewer", null, "approved", "rejected")
        },
        Edges = new() { Edge("design", "implement"), Edge("implement", "review"), Edge("review", "implement", "rejected", 1) }
    };

    private async Task<(List<AgentEvent> Events, ScriptedOrchestrator Orchestrator)> RunAsync(
        FakeExternalAgent external, bool approveInDialog, SdlcPipelineDefinition? flow = null, PermissionMode? mode = null, List<AgentEvent>? seen = null)
    {
        var o = new ScriptedOrchestrator();
        var coordinator = new AgentSessionCoordinator(NullLogger<AgentSessionCoordinator>.Instance, presetLoader: Presets);
        var runner = new SdlcPipelineRunner(coordinator, Presets, o, NullLogger<SdlcPipelineRunner>.Instance,
            characters: _characters, externalAgents: external, skills: _skills);
        var events = seen ?? new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(flow ?? FeatureTeam(), "add pagination", "s1", Path.GetTempPath(),
                           cast: new Dictionary<string, string> { ["review"] = "claude-code-reviewer" }, permissionMode: mode))
        {
            events.Add(e);
            if (e is AgentTaggedEvent { Inner: ApprovalRequestEvent approval } && approval.Call.Name.StartsWith("acp_"))
                approval.Approval.SetResult(approveInDialog);
        }
        return (events, o);
    }

    private static FlowRunResult Result(List<AgentEvent> events) => Assert.Single(events.OfType<FlowFinishedEvent>()).Result;

    [Fact]
    public async Task FeatureTeam_ReviewRunsOnTheExternalAgent_EndToEnd()
    {
        var external = new FakeExternalAgent();
        var (events, o) = await RunAsync(external, approveInDialog: true);

        var result = Result(events);
        Assert.True(result.Succeeded);
        Assert.Equal("approved", result.Nodes.Single(n => n.NodeId == "review").Outcome);
        Assert.DoesNotContain(o.Calls, c => c.Node == "review"); // no built-in agent ran the review
        Assert.Contains(o.Calls, c => c.Node == "implement");

        var request = external.Request!;
        Assert.Equal("claude-code-acp", request.Command);
        Assert.Equal(new[] { "--stdio" }, request.Args);
        Assert.Equal(PermissionMode.Ask, request.PermissionMode);
        // The character's persona and skill reach the agent as instructions, followed by the step's prompt with {input:implement}.
        Assert.Contains("You review code carefully.", request.Prompt);
        Assert.Contains("Check naming and tests.", request.Prompt);
        Assert.Contains("implement done", request.Prompt);
    }

    [Fact]
    public async Task PermissionQuestions_AreApprovalRequests_AndTheAnswerIsPassedBack()
    {
        var approved = new FakeExternalAgent();
        var seen = new List<AgentEvent>();
        await RunAsync(approved, approveInDialog: true, seen: seen);

        var approval = seen.OfType<AgentTaggedEvent>().Select(t => t.Inner).OfType<ApprovalRequestEvent>().Single(a => a.Call.Name == "acp_execute");
        Assert.Equal(RiskLevel.Execute, approval.Risk);
        Assert.Equal("Run dotnet test", approval.Call.Arguments["action"]);
        Assert.True(approved.UserAnswer);

        var declined = new FakeExternalAgent();
        await RunAsync(declined, approveInDialog: false);
        Assert.False(declined.UserAnswer);
    }

    [Fact]
    public async Task FileWrites_GetACheckpointHook_AndShowTheirDiff_AndUsageIsRecorded()
    {
        var external = new FakeExternalAgent();
        var (events, _) = await RunAsync(external, approveInDialog: true);

        var diff = events.OfType<AgentTaggedEvent>().Select(t => t.Inner).OfType<DiffProducedEvent>().Single();
        Assert.EndsWith("x.txt", diff.Diff.FilePath);
        Assert.Equal("new", diff.Diff.ModifiedContent);
        Assert.Equal("review", diff.Diff.AgentId);
        Assert.NotNull(external.Request!.BeforeWrite);
    }

    [Fact]
    public async Task RunPermissionMode_OverridesTheCharacters()
    {
        var external = new FakeExternalAgent();
        await RunAsync(external, approveInDialog: true, mode: PermissionMode.FullAuto);
        Assert.Equal(PermissionMode.FullAuto, external.Request!.PermissionMode);
    }

    [Fact]
    public async Task ExternalCharacter_ParsesAndValidates()
    {
        var c = (await _characters.GetAsync("claude-code-reviewer"))!;
        Assert.True(c.IsValid);
        Assert.True(c.IsExternal);
        Assert.Equal("claude-code-acp", c.Command);
        Assert.Equal(new[] { "--stdio" }, c.Args);
        Assert.Equal(PermissionMode.Ask, c.PermissionMode);
        Assert.Contains("code-review-checklist", c.Skills!);
    }

    [Fact]
    public async Task AcpWithoutACommand_AndUnknownRuntime_AreInvalid()
    {
        var dir = _characters.GlobalCharactersDirectory;
        File.WriteAllText(Path.Combine(dir, "no-command.md"), "---\nid: no-command\ndescription: x\nruntime: acp\n---\nBody\n");
        File.WriteAllText(Path.Combine(dir, "odd-runtime.md"), "---\nid: odd-runtime\ndescription: x\nruntime: quantum\n---\nBody\n");
        _characters.Refresh();

        var noCommand = (await _characters.GetAsync("no-command"))!;
        Assert.False(noCommand.IsValid);
        Assert.Contains(noCommand.ValidationErrors, e => e.Contains("needs a command"));
        var odd = (await _characters.GetAsync("odd-runtime"))!;
        Assert.Contains(odd.ValidationErrors, e => e.Contains("Unknown runtime"));
    }

    [Fact]
    public async Task Command_IsInheritedFromATemplate()
    {
        var dir = _characters.GlobalCharactersDirectory;
        File.WriteAllText(Path.Combine(dir, "child.md"), "---\nid: child\ndescription: x\nextends: claude-code-reviewer\n---\nExtra\n");
        _characters.Refresh();

        var child = (await _characters.GetAsync("child"))!;
        Assert.True(child.IsExternal);
        Assert.Equal("claude-code-acp", child.Command);
        Assert.Equal(new[] { "--stdio" }, child.Args);
    }
}
