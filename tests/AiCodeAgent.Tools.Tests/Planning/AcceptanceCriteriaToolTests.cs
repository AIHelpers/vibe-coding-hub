using System.Text.Json;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Planning;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Tools.Tests.Planning;

/// <summary>E10-S2: the model declares criteria, then marks them with proof; bad calls change nothing.</summary>
public class AcceptanceCriteriaToolTests : TempDirTestBase
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private readonly AcceptanceCriteriaTool _tool = new(NullLogger<AcceptanceCriteriaTool>.Instance);

    // The store is static and keyed by session, so every test uses its own session.
    private AgentExecutionContext Session() => new() { SessionId = "ac-" + Guid.NewGuid().ToString("N"), WorkingDirectory = WorkingDir };

    private Task<ToolResult> Run(AgentExecutionContext ctx, string action, string? criteria = null, string? marks = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = action };
        if (criteria != null) args["criteria"] = Json(criteria);
        if (marks != null) args["marks"] = Json(marks);
        return _tool.ExecuteAsync(Call(args), ctx);
    }

    [Fact]
    public async Task Declare_NumbersTheCriteria_AndReplacesAnEarlierList()
    {
        var ctx = Session();

        var first = await Run(ctx, "declare", """["Overdue items exclude completed ones","Today is not overdue"]""");
        Assert.False(first.IsError, first.Content);
        Assert.Contains("AC1", first.Content);
        Assert.Contains("AC2", first.Content);

        await Run(ctx, "declare", """["Only one now"]""");
        Assert.Single(AcceptanceStore.Criteria(ctx.SessionId));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""["ok",""]""")]
    [InlineData("""["ok","   "]""")]
    public async Task Declare_RejectsEmptyCriteria(string criteria)
    {
        var result = await Run(Session(), "declare", criteria);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Declare_RejectsTooManyAndTooLongCriteria()
    {
        var many = "[" + string.Join(",", Enumerable.Range(1, AcceptanceCriteriaTool.MaxCriteria + 1).Select(i => $"\"c{i}\"")) + "]";
        Assert.True((await Run(Session(), "declare", many)).IsError);
        Assert.True((await Run(Session(), "declare", $"[\"{new string('x', AcceptanceCriteriaTool.MaxCriterionChars + 1)}\"]")).IsError);
    }

    [Fact]
    public async Task Mark_BeforeDeclare_TellsTheModelToDeclareFirst()
    {
        var result = await Run(Session(), "mark", marks: """[{"id":"AC1","status":"met"}]""");
        Assert.True(result.IsError);
        Assert.Contains("declare", result.Content);
    }

    [Fact]
    public async Task Mark_RecordsStatus_IsCaseInsensitiveOnIds_AndListsWhatIsLeft()
    {
        var ctx = Session();
        await Run(ctx, "declare", """["one","two"]""");

        var result = await Run(ctx, "mark", marks: """[{"id":"ac1","status":"met","tests":["Overdue_Excludes"]}]""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("AC1 [x] met", result.Content);
        Assert.Contains("AC2 [ ] not marked", result.Content);
        Assert.Contains("Not marked yet: AC2", result.Content);
        Assert.NotNull(AcceptanceStore.MarkOf(ctx.SessionId, "AC1"));
    }

    [Fact]
    public async Task Mark_MetWithoutProof_WarnsThatItWillBeShownAsClaimed()
    {
        var ctx = Session();
        await Run(ctx, "declare", """["one"]""");

        var result = await Run(ctx, "mark", marks: """[{"id":"AC1","status":"met","note":"trust me"}]""");

        Assert.False(result.IsError);
        Assert.Contains("claimed", result.Content);
    }

    [Fact]
    public async Task Mark_NotMet_NeedsNoProof_AndGetsNoWarning()
    {
        var ctx = Session();
        await Run(ctx, "declare", """["one"]""");

        var result = await Run(ctx, "mark", marks: """[{"id":"AC1","status":"not_met","note":"needs a migration"}]""");

        Assert.False(result.IsError);
        Assert.Contains("[!] not met", result.Content);
        Assert.DoesNotContain("without proof", result.Content);
    }

    [Fact]
    public async Task Mark_WithOneBadEntry_ChangesNothing()
    {
        var ctx = Session();
        await Run(ctx, "declare", """["one","two"]""");

        var result = await Run(ctx, "mark", marks: """[{"id":"AC1","status":"met","files":["a.cs"]},{"id":"AC9","status":"met"}]""");

        Assert.True(result.IsError);
        Assert.Contains("AC9", result.Content);
        Assert.Null(AcceptanceStore.MarkOf(ctx.SessionId, "AC1"));
    }

    [Fact]
    public async Task Mark_RejectsAStatusOtherThanMetOrNotMet()
    {
        var ctx = Session();
        await Run(ctx, "declare", """["one"]""");
        Assert.True((await Run(ctx, "mark", marks: """[{"id":"AC1","status":"mostly"}]""")).IsError);
    }

    [Fact]
    public async Task Declare_AgainDropsEarlierMarks()
    {
        var ctx = Session();
        await Run(ctx, "declare", """["one"]""");
        await Run(ctx, "mark", marks: """[{"id":"AC1","status":"met","files":["a.cs"]}]""");

        await Run(ctx, "declare", """["one, reworded"]""");

        Assert.Null(AcceptanceStore.MarkOf(ctx.SessionId, "AC1"));
    }

    [Fact]
    public async Task Show_ListsTheState_OrSaysThereIsNone()
    {
        var ctx = Session();
        Assert.Contains("No acceptance criteria", (await Run(ctx, "show")).Content);

        await Run(ctx, "declare", """["one"]""");
        Assert.Contains("AC1 [ ] not marked: one", (await Run(ctx, "show")).Content);
    }

    [Fact]
    public async Task UnknownAction_IsAnError()
    {
        Assert.True((await Run(Session(), "finish")).IsError);
    }

    [Fact]
    public void TheToolIsKnown_ToPresetsAndCharacters()
    {
        Assert.Empty(AiCodeAgent.Core.Agent.KnownToolNames.Unknown(new[] { AcceptanceCriteriaTool.ToolName }));
    }
}
