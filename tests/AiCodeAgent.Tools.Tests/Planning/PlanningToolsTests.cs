using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Tools.Planning;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Planning;

public class PlanningToolsTests : TempDirTestBase
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task TodoWrite_StoresAndRendersList()
    {
        var tool = new TodoWriteTool(NullLogger<TodoWriteTool>.Instance);
        var todos = Json("""[{"content":"Write code","status":"completed"},{"content":"Run tests","status":"in_progress"},{"content":"Ship","status":"pending"}]""");
        var ctx = Context();

        var result = await tool.ExecuteAsync(Call(new() { ["todos"] = todos }), ctx);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("1/3 done", result.Content);
        Assert.Contains("[x] Write code", result.Content);
        Assert.Contains("[~] Run tests", result.Content);
        Assert.Equal(3, TodoStore.Get(TodoStore.Key(ctx)).Count);
    }

    [Fact]
    public async Task TodoWrite_ReplacesPreviousList()
    {
        var tool = new TodoWriteTool(NullLogger<TodoWriteTool>.Instance);
        var ctx = Context();
        await tool.ExecuteAsync(Call(new() { ["todos"] = Json("""[{"content":"a","status":"pending"},{"content":"b","status":"pending"}]""") }), ctx);
        await tool.ExecuteAsync(Call(new() { ["todos"] = Json("""[{"content":"c","status":"pending"}]""") }), ctx);
        Assert.Single(TodoStore.Get(TodoStore.Key(ctx)));
    }

    [Fact]
    public async Task TodoWrite_RejectsBadStatus()
    {
        var tool = new TodoWriteTool(NullLogger<TodoWriteTool>.Instance);
        var result = await tool.ExecuteAsync(Call(new() { ["todos"] = Json("""[{"content":"a","status":"doing"}]""") }), Context());
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task AskUser_WithoutHandler_TellsModelToAssume()
    {
        var tool = new AskUserTool(NullLogger<AskUserTool>.Instance);
        var result = await tool.ExecuteAsync(Call(new() { ["question"] = "Which DB?" }), Context());
        Assert.True(result.IsError);
        Assert.Contains("assumption", result.Content);
    }

    [Fact]
    public async Task AskUser_ReturnsHandlerAnswer_AndPassesOptions()
    {
        var handler = Substitute.For<IUserQuestionHandler>();
        handler.AskAsync("Which DB?", Arg.Is<IReadOnlyList<string>>(o => o.Count == 2), Arg.Any<CancellationToken>())
               .Returns("Postgres");
        var tool = new AskUserTool(NullLogger<AskUserTool>.Instance, handler);

        var result = await tool.ExecuteAsync(
            Call(new() { ["question"] = "Which DB?", ["options"] = Json("""["Postgres","SQLite"]""") }), Context());

        Assert.False(result.IsError);
        Assert.Contains("Postgres", result.Content);
    }

    [Fact]
    public async Task AskUser_NoAnswer_IsNotAnError()
    {
        var handler = Substitute.For<IUserQuestionHandler>();
        handler.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var tool = new AskUserTool(NullLogger<AskUserTool>.Instance, handler);

        var result = await tool.ExecuteAsync(Call(new() { ["question"] = "Q?" }), Context());

        Assert.False(result.IsError);
        Assert.Contains("did not answer", result.Content);
    }
}
