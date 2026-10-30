namespace AiCodeAgent.Bench.Tests;

public class AttemptRunnerTests
{
    private static async Task<Attempt> Run(BenchSandbox box, FakeAgent agent, BenchConfig config, int seed = 1)
    {
        var suite = box.Suite();
        var runner = new AttemptRunner(box.MakeOptions(), agent, suite);
        return await runner.RunAsync(suite.Tasks[0], config, seed, CancellationToken.None);
    }

    private static readonly BenchConfig Single = new("single", null);
    private static readonly BenchConfig Flow = new("bug-fix-flow", "bug-fix");

    [Fact]
    public async Task PassingTest_IsAPass_WithUsageFromTheAgent()
    {
        using var box = new BenchSandbox(("t1", "exit 0"));
        var agent = new FakeAgent();
        var a = await Run(box, agent, Single);
        Assert.Equal(AttemptStatus.Pass, a.Status);
        Assert.Equal(0, a.TestExitCode);
        Assert.Equal(1000, a.Tokens);
        Assert.Equal(0.10m, a.CostUsd);
        Assert.Equal(new[] { "test-model" }, a.Models);
        Assert.Equal("run-1", a.RunId);
    }

    [Fact]
    public async Task FailingTest_IsAFail()
    {
        using var box = new BenchSandbox(("t1", "exit 1"));
        var a = await Run(box, new FakeAgent(), Flow);
        Assert.Equal(AttemptStatus.Fail, a.Status);
        Assert.Equal(1, a.TestExitCode);
    }

    [Fact]
    public async Task TheVerdict_ReadsTheWorkspaceTheAgentLeftBehind()
    {
        using var box = new BenchSandbox(("t1", BenchSandbox.FileExistsTest("fixed.txt")));
        var lazy = await Run(box, new FakeAgent(), Single);
        Assert.Equal(AttemptStatus.Fail, lazy.Status);

        var agent = new FakeAgent
        {
            Behaviour = inv =>
            {
                File.WriteAllText(Path.Combine(inv.WorkDirectory, "fixed.txt"), "done");
                return FakeAgent.Ok(inv);
            }
        };
        var diligent = await Run(box, agent, Single);
        Assert.Equal(AttemptStatus.Pass, diligent.Status);
        Assert.False(File.Exists(Path.Combine(box.Snapshot, "fixed.txt")), "the snapshot itself must never be touched");
    }

    [Fact]
    public async Task EveryWorkspace_IsAFreshGitRepository()
    {
        using var box = new BenchSandbox(("t1", "exit 0"));
        var agent = new FakeAgent();
        await Run(box, agent, Single, seed: 1);
        Assert.True(agent.WorkspaceWasGitRepo);
        Assert.False(Directory.Exists(agent.Calls[0].WorkDirectory), "workspaces are deleted unless --keep-workspaces");
    }

    [Fact]
    public async Task ATimeout_IsAFailure_WithoutRunningTheTest()
    {
        using var box = new BenchSandbox(("t1", "exit 0"));
        var agent = new FakeAgent { Behaviour = _ => new AgentRun(-1, true, 1800, UsageInfo.Unknown, "agent timed out") };
        var a = await Run(box, agent, Single);
        Assert.Equal(AttemptStatus.Timeout, a.Status);
        Assert.Null(a.TestExitCode);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AFlowThatNeverStarted_IsAnError_NotAFailedFix(int exitCode)
    {
        using var box = new BenchSandbox(("t1", "exit 0"));
        var agent = new FakeAgent { Behaviour = _ => new AgentRun(exitCode, false, 1, UsageInfo.Unknown, "configuration error") };
        var a = await Run(box, agent, Flow);
        Assert.Equal(AttemptStatus.Error, a.Status);
    }

    [Fact]
    public async Task AFlowThatFailedAStep_IsStillJudgedByTheTest()
    {
        using var box = new BenchSandbox(("t1", "exit 0"));
        var agent = new FakeAgent { Behaviour = inv => FakeAgent.Ok(inv, exit: 1) };
        var a = await Run(box, agent, Flow);
        Assert.Equal(AttemptStatus.Pass, a.Status);
        Assert.Equal(1, a.AgentExitCode);
    }

    [Fact]
    public async Task AMissingSnapshot_IsAnError()
    {
        using var box = new BenchSandbox(("t1", "exit 0"));
        Directory.Delete(box.Snapshot, true);
        var a = await Run(box, new FakeAgent(), Single);
        Assert.Equal(AttemptStatus.Error, a.Status);
        Assert.Contains("snapshot", a.Note);
    }

    [Fact]
    public async Task Validate_RequiresTheTestToFailOnTheUntouchedSnapshot()
    {
        using var box = new BenchSandbox(("passes", "exit 0"), ("fails", "exit 1"));
        var suite = box.Suite();
        var runner = new AttemptRunner(box.MakeOptions(), new FakeAgent(), suite);
        var (okValid, okMessage) = await runner.ValidateAsync(suite.Tasks[1], CancellationToken.None);
        var (badValid, badMessage) = await runner.ValidateAsync(suite.Tasks[0], CancellationToken.None);
        Assert.True(okValid, okMessage);
        Assert.False(badValid);
        Assert.Contains("PASSES", badMessage);
    }
}
