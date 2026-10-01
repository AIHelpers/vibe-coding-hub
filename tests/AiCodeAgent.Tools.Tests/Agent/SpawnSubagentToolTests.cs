using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Agent;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiCodeAgent.Tools.Tests.Agent;

public class SpawnSubagentToolTests : TestHelpers.TempDirTestBase
{
    private readonly ISubagentRunner _runner;
    private readonly ILogger<SpawnSubagentTool> _logger;
    private readonly SpawnSubagentTool _tool;

    public SpawnSubagentToolTests()
    {
        _runner = Substitute.For<ISubagentRunner>();
        _logger = Substitute.For<ILogger<SpawnSubagentTool>>();
        _tool = new SpawnSubagentTool(_runner, _logger);
    }

    private static SubagentSummary Summary(string content = "result", bool forked = false) => new()
    {
        SubagentId = "sub_abc",
        Task = "task",
        Forked = forked,
        Content = content,
        ToolCallCount = 2,
        Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 20 },
        Duration = TimeSpan.FromMilliseconds(150),
        WasCancelled = false,
        Error = null,
    };

    [Fact]
    public void Name_ReturnsSpawnSubagent()
    {
        Assert.Equal("spawn_subagent", _tool.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.NotEmpty(_tool.Description);
    }

    [Fact]
    public void Risk_IsWrite()
    {
        Assert.Equal(RiskLevel.Write, _tool.Risk);
    }

    [Fact]
    public void Definition_HasRequiredTaskParameter()
    {
        var def = _tool.Definition;

        Assert.Equal("spawn_subagent", def.Name);
        Assert.NotNull(def.Parameters);
        Assert.Contains("task", def.Parameters.Properties);
        Assert.Contains("task", def.Parameters.Required);
    }

    [Fact]
    public void Definition_IncludesOptionalParameters()
    {
        var props = _tool.Definition.Parameters.Properties;

        Assert.Contains("fork", props);
        Assert.Contains("model", props);
        Assert.Contains("maxIterations", props);
        Assert.Contains("timeoutSeconds", props);
        Assert.Contains("enabledTools", props);
        Assert.Contains("role", props);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyTask_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(Call(new() { ["task"] = "" }), Context());

        Assert.True(result.IsError);
        Assert.Contains("task", result.Content, StringComparison.OrdinalIgnoreCase);
        await _runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingTask_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(Call(new()), Context());

        Assert.True(result.IsError);
        await _runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithForkFalse_CallsRunAsync()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>())
            .Returns(Summary("fresh result"));

        var result = await _tool.ExecuteAsync(
            Call(new() { ["task"] = "find usages", ["fork"] = false }),
            Context());

        Assert.False(result.IsError);
        Assert.Equal("fresh result", result.Content);
        await _runner.Received(1).RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>());
        await _runner.DidNotReceive().ForkAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithForkTrue_CallsForkAsync()
    {
        _runner.ForkAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>())
            .Returns(Summary("forked result", forked: true));

        var result = await _tool.ExecuteAsync(
            Call(new() { ["task"] = "continue work", ["fork"] = true }),
            Context());

        Assert.False(result.IsError);
        Assert.Equal("forked result", result.Content);
        await _runner.Received(1).ForkAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>());
        await _runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PassesTaskToRunner()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>())
            .Returns(Summary());

        await _tool.ExecuteAsync(Call(new() { ["task"] = "my specific task" }), Context());

        await _runner.Received(1).RunAsync(
            Arg.Is<string>(t => t == "my specific task"),
            Arg.Any<SubagentContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_BuildsSubagentContext_FromArguments()
    {
        SubagentContext? captured = null;
        _runner.RunAsync(Arg.Any<string>(), Arg.Do<SubagentContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(Summary());

        var ctx = Context(allowedPaths: new() { @"C:\allowed" });
        await _tool.ExecuteAsync(
            Call(new()
            {
                ["task"] = "task",
                ["model"] = "gpt-4o",
                ["maxIterations"] = 20,
                ["timeoutSeconds"] = 120,
                ["enabledTools"] = new[] { "read_file", "search_files" },
                ["role"] = "researcher",
            }),
            ctx);

        Assert.NotNull(captured);
        Assert.Equal(ctx.SessionId, captured!.ParentSessionId);
        Assert.Equal(ctx.WorkingDirectory, captured.WorkingDirectory);
        Assert.Equal("gpt-4o", captured.Model);
        Assert.Equal(20, captured.MaxIterations);
        Assert.Equal(TimeSpan.FromSeconds(120), captured.Timeout);
        Assert.Equal("researcher", captured.Role);
        Assert.NotNull(captured.EnabledTools);
        Assert.Equal(new[] { "read_file", "search_files" }, captured.EnabledTools);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoModel_LeavesModelNull()
    {
        SubagentContext? captured = null;
        _runner.RunAsync(Arg.Any<string>(), Arg.Do<SubagentContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(Summary());

        await _tool.ExecuteAsync(Call(new() { ["task"] = "task" }), Context());

        Assert.NotNull(captured);
        Assert.Null(captured!.Model);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoEnabledTools_LeavesToolsNull()
    {
        SubagentContext? captured = null;
        _runner.RunAsync(Arg.Any<string>(), Arg.Do<SubagentContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(Summary());

        await _tool.ExecuteAsync(Call(new() { ["task"] = "task" }), Context());

        Assert.NotNull(captured);
        Assert.Null(captured!.EnabledTools);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyEnabledTools_LeavesToolsNull()
    {
        SubagentContext? captured = null;
        _runner.RunAsync(Arg.Any<string>(), Arg.Do<SubagentContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(Summary());

        await _tool.ExecuteAsync(
            Call(new() { ["task"] = "task", ["enabledTools"] = Array.Empty<string>() }),
            Context());

        Assert.NotNull(captured);
        Assert.Null(captured!.EnabledTools);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSummaryContentAndPayload()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>())
            .Returns(Summary("the answer", forked: false));

        var result = await _tool.ExecuteAsync(Call(new() { ["task"] = "task" }), Context());

        Assert.Equal("the answer", result.Content);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunnerThrowsOperationCanceled_ReturnsCancellationError()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var result = await _tool.ExecuteAsync(Call(new() { ["task"] = "task" }), Context());

        Assert.True(result.IsError);
        Assert.Contains("cancelled", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunnerThrowsGeneric_ReturnsErrorWithMessage()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<SubagentContext>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        var result = await _tool.ExecuteAsync(Call(new() { ["task"] = "task" }), Context());

        Assert.True(result.IsError);
        Assert.Contains("boom", result.Content);
    }
}