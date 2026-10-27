using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.Chat;

/// <summary>E2-S8: flow step and background task output is readable Markdown without internal status lines.</summary>
public class AgentOutputTextTests
{
    [Theory]
    [InlineData("Done")]
    [InlineData("Processing")]
    [InlineData("Executing read_file")]
    [InlineData("Requesting approval for edit_file")]
    public void InternalStatuses_ProduceNoNote(string status)
    {
        Assert.Null(AgentOutputText.StatusNote(new StatusUpdateEvent(status, "whatever")));
    }

    [Fact]
    public void ImportantStatuses_BecomeAQuotedNote()
    {
        var note = AgentOutputText.StatusNote(new StatusUpdateEvent("Limit", "Reached the step limit"));

        Assert.NotNull(note);
        Assert.Contains("> **Limit** Reached the step limit", note);
        Assert.DoesNotContain("[Limit]", note);
    }

    [Fact]
    public void StripInternalLines_RemovesDoneLine_AndKeepsTheRest()
    {
        var raw = "## TodoApp 1.2\n\n**Shipped.**\n[Done] Agent completed\n";

        var clean = AgentOutputText.StripInternalLines(raw);

        Assert.DoesNotContain("[Done]", clean);
        Assert.Contains("## TodoApp 1.2", clean);
        Assert.Contains("**Shipped.**", clean);
    }

    [Fact]
    public void StripInternalLines_KeepsBracketsInsideNormalText()
    {
        var raw = "Use a list like [Done] in prose, and arr[0] too.";

        Assert.Equal(raw, AgentOutputText.StripInternalLines(raw));
    }

    [Fact]
    public void Notes_AreMarkdown_NotBracketedLabels()
    {
        Assert.Contains("`read_file`", AgentOutputText.ToolNote("read_file"));
        Assert.DoesNotContain("[tool]", AgentOutputText.ToolNote("read_file"));
        Assert.Contains("> ⚠ **Error:** boom", AgentOutputText.ErrorNote("boom"));
        Assert.Contains("> ⚠ **Failed:** no build", AgentOutputText.FailureNote("Failed", "no build"));
        Assert.StartsWith("*— round 2 —*", AgentOutputText.RoundHeader(2));
    }

    [Fact]
    public void MultiLineDetail_StaysInsideTheQuote()
    {
        var note = AgentOutputText.StatusNote(new StatusUpdateEvent("Hook", "line one\nline two"));

        Assert.Contains("> **Hook** line one\n> line two", note);
    }

    [Fact]
    public void BackgroundTaskOutput_HidesInternalLines_AndHasNoOutputWhenOnlyThoseExist()
    {
        var task = new BackgroundTaskItemViewModel { SessionId = "s1", Prompt = "p" };

        task.Output = "[Done] Agent completed\n";
        Assert.False(task.HasOutput);

        task.Output = "## Result\n\ntext\n[Done] Agent completed\n";
        Assert.True(task.HasOutput);
        Assert.DoesNotContain("[Done]", task.OutputMarkdown);
        Assert.Contains("## Result", task.OutputMarkdown);
    }
}
