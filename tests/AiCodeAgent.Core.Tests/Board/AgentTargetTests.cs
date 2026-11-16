using AiCodeAgent.Core.Board;

namespace AiCodeAgent.Core.Tests.Board;

/// <summary>E11-S2: the agent a card is assigned to, how it is saved, and the external agents found on this machine.</summary>
public sealed class AgentTargetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-target-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ the target value

    [Fact]
    public void TwoTargets_WithTheSameArguments_AreEqual()
    {
        var a = AgentTarget.ForExternal("npx", new[] { "-y", "pkg" }, "Mine");
        var b = AgentTarget.ForExternal("npx", new List<string> { "-y", "pkg" }, "Mine");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, AgentTarget.ForExternal("npx", new[] { "-y", "other" }, "Mine"));
        Assert.NotEqual(AgentTarget.ForCharacter("x"), AgentTarget.ForFlow("x"));
    }

    [Fact]
    public void DisplayName_ReadsWellForEachKind()
    {
        Assert.Equal("Default agent", AgentTarget.Default.DisplayName);
        Assert.Equal("🛠 Builder", AgentTarget.ForCharacter("builder", "🛠 Builder").DisplayName);
        Assert.Equal("builder", AgentTarget.ForCharacter("builder").DisplayName);
        Assert.Equal("Flow feature-team", AgentTarget.ForFlow("feature-team").DisplayName);
        Assert.Equal("Claude Code", AgentTarget.ForPreset(ExternalAgentPresets.ClaudeCode).DisplayName);
        Assert.Equal("my-agent", AgentTarget.ForExternal("my-agent").DisplayName);
    }

    [Fact]
    public void ATarget_WithoutWhatItNeeds_ReportsAProblem()
    {
        Assert.Null(AgentTarget.Default.Problem);
        Assert.Null(AgentTarget.ForFlow("f").Problem);
        Assert.NotNull(new AgentTarget { Kind = AgentTargetKind.Character }.Problem);
        Assert.NotNull(new AgentTarget { Kind = AgentTargetKind.Flow, Id = " " }.Problem);
        Assert.NotNull(new AgentTarget { Kind = AgentTargetKind.External }.Problem);
    }

    // ------------------------------------------------------------------ on the board

    [Fact]
    public void ACard_KeepsItsAgent_ThroughTheBoardFile()
    {
        var store = new BoardStore(Path.Combine(_dir, "board.json"));
        var board = new TaskBoard(store);
        var external = AgentTarget.ForExternal("my-agent", new[] { "--acp", "--fast" }, "My agent");
        board.Add("one", agent: AgentTarget.ForFlow("feature-team"));
        board.Add("two", agent: external);
        board.Add("three");

        var cards = new TaskBoard(store).Cards;

        Assert.Equal(AgentTarget.ForFlow("feature-team"), cards[0].Agent);
        Assert.Equal(external, cards[1].Agent);
        Assert.Equal(AgentTarget.Default, cards[2].Agent);
    }

    [Fact]
    public void ABoardFile_FromBeforeAgentsExisted_LoadsWithTheDefaultAgent()
    {
        var path = Path.Combine(_dir, "board.json");
        File.WriteAllText(path, """
            { "version": 1, "cards": [ { "id": "abc12345", "title": "Old card", "prompt": "Old card", "column": "backlog" } ] }
            """);

        var card = Assert.Single(new TaskBoard(new BoardStore(path)).Cards);

        Assert.Equal(AgentTarget.Default, card.Agent);
        Assert.Equal("Old card", card.Title);
    }

    [Fact]
    public void TheAgent_CanBeChanged_UnlessTheCardIsRunning()
    {
        var board = new TaskBoard();
        var id = board.Add("task").Id;

        Assert.Equal(AgentTarget.ForFlow("f"), board.SetAgent(id, AgentTarget.ForFlow("f"))!.Agent);

        board.BeginRun(id);
        Assert.Null(board.SetAgent(id, AgentTarget.Default));
        Assert.Equal(AgentTarget.ForFlow("f"), board.Get(id)!.Agent);

        board.EndRun(id, CardOutcome.Completed);
        Assert.Equal(AgentTarget.Default, board.SetAgent(id, AgentTarget.Default)!.Agent);
    }

    [Fact]
    public void ARetry_KeepsTheAgent()
    {
        var board = new TaskBoard();
        var id = board.Add("task", agent: AgentTarget.ForCharacter("builder")).Id;
        board.BeginRun(id);
        board.EndRun(id, CardOutcome.Failed);
        board.ReturnToBacklog(id);

        Assert.Equal(AgentTarget.ForCharacter("builder"), board.BeginRun(id)!.Agent);
    }

    // ------------------------------------------------------------------ external agents on this machine

    [Fact]
    public void APreset_IsInstalled_WhenItsProgramsAreOnThePath()
    {
        var detector = new ExternalAgentDetector(name => name is "claude" or "npx" ? "/bin/" + name : null);

        var claude = detector.Detect(ExternalAgentPresets.ClaudeCode);
        var gemini = detector.Detect(ExternalAgentPresets.GeminiCli);

        Assert.True(claude.Installed);
        Assert.Contains("/bin/claude", claude.Detail);
        Assert.False(gemini.Installed);
        Assert.Contains("'gemini' was not found", gemini.Detail);
    }

    [Fact]
    public void ACliThatNeedsAnAdapter_IsNotReady_WithoutNode()
    {
        var detector = new ExternalAgentDetector(name => name == "codex" ? "/bin/codex" : null);

        var codex = detector.Detect(ExternalAgentPresets.Codex);

        Assert.False(codex.Installed);
        Assert.Contains("'npx' was not found", codex.Detail);
        Assert.Contains("Node.js", codex.Detail);
    }

    [Fact]
    public void DetectAll_ListsEveryPreset_InOrder()
    {
        var all = new ExternalAgentDetector(_ => null).DetectAll();

        Assert.Equal(new[] { "claude-code", "codex", "gemini-cli" }, all.Select(d => d.Preset.Id));
        Assert.All(all, d => Assert.False(d.Installed));
    }

    [Fact]
    public void FindOnPath_FindsAProgram_ByNameAndWithWindowsExtensions()
    {
        var tool = Path.Combine(_dir, "mytool.cmd");
        File.WriteAllText(tool, "echo hi");
        var plain = Path.Combine(_dir, "plain");
        File.WriteAllText(plain, "x");

        Assert.Equal(tool, ExternalAgentDetector.FindOnPath("mytool", _dir, ".EXE;.cmd", windows: true));
        Assert.Null(ExternalAgentDetector.FindOnPath("mytool", _dir, ".EXE", windows: true));
        Assert.Equal(plain, ExternalAgentDetector.FindOnPath("plain", _dir, windows: false));
        Assert.Null(ExternalAgentDetector.FindOnPath("missing", _dir, windows: false));
        Assert.Null(ExternalAgentDetector.FindOnPath("", _dir));
    }
}
