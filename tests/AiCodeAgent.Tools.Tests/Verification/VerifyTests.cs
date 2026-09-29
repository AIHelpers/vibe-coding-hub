using AiCodeAgent.Tools.Tests.TestHelpers;
using AiCodeAgent.Tools.Verification;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Tools.Tests.Verification;

public class VerifyCommandDetectorTests : TempDirTestBase
{
    [Fact]
    public void ConfigFile_TakesPriority()
    {
        WriteFile(".aiagent/verify.json", """{"commands":[{"name":"check","command":"make check"}]}""");
        WriteFile("AGENTS.md", "Test command: `dotnet test`");
        WriteFile("app.csproj", "<Project/>");

        var stages = VerifyCommandDetector.Detect(WorkingDir);

        Assert.Equal(new[] { new VerifyStage("check", "make check") }, stages);
    }

    [Fact]
    public void AgentsMd_ProvidesBuildTestLint_InOrder()
    {
        WriteFile("AGENTS.md", "## Conventions\n- Test command: `dotnet test`\n- Build command: `dotnet build`\nLint command: npm run lint\n");

        var stages = VerifyCommandDetector.Detect(WorkingDir);

        Assert.Equal(new[] { "build", "test", "lint" }, stages.Select(s => s.Name));
        Assert.Equal("dotnet build", stages[0].Command);
        Assert.Equal("npm run lint", stages[2].Command);
    }

    [Fact]
    public void AutoDetect_DotnetProject()
    {
        WriteFile("app.csproj", "<Project/>");
        Assert.Equal(new[] { "build", "test" }, VerifyCommandDetector.Detect(WorkingDir).Select(s => s.Name));
    }

    [Fact]
    public void AutoDetect_NpmUsesOnlyExistingScripts()
    {
        WriteFile("package.json", """{"scripts":{"build":"tsc","test":"jest"}}""");
        var stages = VerifyCommandDetector.Detect(WorkingDir);
        Assert.Equal(new[] { "build", "test" }, stages.Select(s => s.Name));
        Assert.DoesNotContain(stages, s => s.Name == "lint");
    }

    [Fact]
    public void AutoDetect_GoAndCargo()
    {
        WriteFile("go.mod", "module x");
        Assert.Equal("go test ./...", VerifyCommandDetector.Detect(WorkingDir).Last().Command);
    }

    [Fact]
    public void EmptyDirectory_HasNoStages() =>
        Assert.Empty(VerifyCommandDetector.Detect(WorkingDir));

    [Fact]
    public void MalformedConfig_FallsBackInsteadOfThrowing()
    {
        WriteFile(".aiagent/verify.json", "{ not json");
        WriteFile("go.mod", "module x");
        Assert.NotEmpty(VerifyCommandDetector.Detect(WorkingDir));
    }
}

public class FailureSummarizerTests
{
    [Fact]
    public void KeepsErrorLines_DropsNoise()
    {
        var output = "Restoring...\nBuilding...\nProgram.cs(10,5): error CS1002: ; expected\nDone.\n";
        var summary = FailureSummarizer.Summarize(output);
        Assert.Contains("CS1002", summary);
        Assert.DoesNotContain("Restoring", summary);
    }

    [Fact]
    public void FallsBackToTail_WhenNothingLooksLikeAnError()
    {
        var summary = FailureSummarizer.Summarize(string.Join("\n", Enumerable.Range(1, 200).Select(i => $"row {i}")), maxLines: 5);
        Assert.Contains("row 200", summary);
        Assert.DoesNotContain("row 1\n", summary + "\n");
    }

    [Fact]
    public void CapsLength() =>
        Assert.True(FailureSummarizer.Summarize(new string('x', 50_000) + " error", maxChars: 500).Length < 600);
}

public class VerifyChangesToolTests : TempDirTestBase
{
    private readonly VerifyChangesTool _tool = new(NullLogger<VerifyChangesTool>.Instance);

    [Fact]
    public async Task NoCommands_ExplainsHowToConfigure()
    {
        var result = await _tool.ExecuteAsync(Call(new()), Context());
        Assert.True(result.IsError);
        Assert.Contains("verify.json", result.Content);
    }

    [Fact]
    public async Task PassingStages_Succeed()
    {
        WriteFile(".aiagent/verify.json", """{"commands":[{"name":"one","command":"echo ok"},{"name":"two","command":"echo ok"}]}""");
        var result = await _tool.ExecuteAsync(Call(new()), Context());
        Assert.False(result.IsError, result.Content);
        Assert.Contains("PASS  one", result.Content);
        Assert.Contains("All 2 stage(s) passed", result.Content);
    }

    [Fact]
    public async Task FailingStage_StopsAndCountsAttempts()
    {
        WriteFile(".aiagent/verify.json",
            """{"commands":[{"name":"build","command":"echo error: boom && exit 1"},{"name":"test","command":"echo never"}]}""");
        var ctx = Context();

        var first = await _tool.ExecuteAsync(Call(new()), ctx);
        var second = await _tool.ExecuteAsync(Call(new()), ctx);

        Assert.True(first.IsError);
        Assert.Contains("FAIL  build", first.Content);
        Assert.Contains("boom", first.Content);
        Assert.DoesNotContain("never", first.Content);
        Assert.Contains("Attempt 1/5", first.Content);
        Assert.Contains("Attempt 2/5", second.Content);
    }

    [Fact]
    public async Task RepeatedFailures_TellModelToStop()
    {
        WriteFile(".aiagent/verify.json", """{"commands":[{"name":"t","command":"exit 1"}]}""");
        var ctx = Context();
        ToolResultHolder last = default;
        for (var i = 0; i < VerifyChangesTool.MaxConsecutiveFailures; i++)
            last = new ToolResultHolder((await _tool.ExecuteAsync(Call(new()), ctx)).Content);
        Assert.Contains("limit reached", last.Content);
    }

    [Fact]
    public async Task StageFilter_RunsOnlyRequestedStages()
    {
        WriteFile(".aiagent/verify.json", """{"commands":[{"name":"a","command":"echo a"},{"name":"b","command":"echo b"}]}""");
        var stages = System.Text.Json.JsonDocument.Parse("""["b"]""").RootElement.Clone();
        var result = await _tool.ExecuteAsync(Call(new() { ["stages"] = stages }), Context());
        Assert.Contains("PASS  b", result.Content);
        Assert.DoesNotContain("PASS  a", result.Content);
    }

    private readonly record struct ToolResultHolder(string Content);
}
