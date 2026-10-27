using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Core.Tests.Samples;

/// <summary>E3-S3: the four ticks of the "Fix this test" checklist.</summary>
public class SampleTaskChecklistTests
{
    [Fact]
    public void StartsEmpty_WithTheFourSteps()
    {
        var list = new SampleTaskChecklist();

        Assert.Equal(0, list.Completed);
        Assert.False(list.IsComplete);
        Assert.Equal(
            new[] { "Send the task", "Approve the edit", "See tests pass", "Commit in Source Control" },
            SampleTaskChecklist.Labels);
    }

    [Fact]
    public void StepsTickInOrder_AndTheLastOneCompletesTheList()
    {
        var list = new SampleTaskChecklist();
        var changes = 0;
        list.Changed += () => changes++;

        Assert.True(list.TaskSent());
        Assert.True(list.EditApplied());
        Assert.True(list.TestsPassed());
        Assert.False(list.IsComplete);
        Assert.True(list.Committed());

        Assert.True(list.IsComplete);
        Assert.Equal(4, changes);
        Assert.True(list.IsDone(SampleStep.Commit));
    }

    [Fact]
    public void AStepCannotSkipAhead()
    {
        var list = new SampleTaskChecklist();

        Assert.False(list.EditApplied());
        Assert.False(list.TestsPassed());
        Assert.False(list.Committed());
        Assert.Equal(0, list.Completed);

        list.TaskSent();
        Assert.False(list.TestsPassed());
        Assert.False(list.Committed());
        Assert.Equal(1, list.Completed);
    }

    [Fact]
    public void ATickedStepDoesNotTickTwice()
    {
        var list = new SampleTaskChecklist();
        list.TaskSent();

        Assert.False(list.TaskSent());
        Assert.Equal(1, list.Completed);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(2, 2)]
    [InlineData(9, 4)]
    public void TheStartingProgressIsClamped(int given, int expected)
    {
        Assert.Equal(expected, new SampleTaskChecklist(given).Completed);
    }

    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("dotnet test TodoApp.slnx", true)]
    [InlineData("dotnet test TodoApp.slnx --filter \"FullyQualifiedName~Overdue\"", true)]
    [InlineData("cd src && dotnet build && dotnet test", true)]
    [InlineData("DOTNET TEST", true)]
    [InlineData("dotnet build", false)]
    [InlineData("dotnet testhost", false)]
    [InlineData("echo dotnettest", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTestCommand_RecognizesDotnetTest(string? command, bool expected)
    {
        Assert.Equal(expected, SampleTaskChecklist.IsTestCommand(command));
    }

    [Theory]
    [InlineData("git commit -m \"Fix overdue\"", true)]
    [InlineData("git add -A && git commit -m x", true)]
    [InlineData("git -c user.name=a commit -m x", true)]
    [InlineData("git status", false)]
    [InlineData("git commitment", false)]
    [InlineData(null, false)]
    public void IsCommitCommand_RecognizesGitCommit(string? command, bool expected)
    {
        Assert.Equal(expected, SampleTaskChecklist.IsCommitCommand(command));
    }

    [Fact]
    public void ToolFinished_AnEditThatWorked_TicksApproveTheEdit()
    {
        var list = new SampleTaskChecklist(1);

        Assert.True(list.ToolFinished(isEditTool: true, "edit_file", null, isError: false));

        Assert.Equal(2, list.Completed);
    }

    [Fact]
    public void ToolFinished_AFailedCallTicksNothing()
    {
        var list = new SampleTaskChecklist(1);
        Assert.False(list.ToolFinished(true, "edit_file", null, isError: true));

        // The sample's tests fail until the fix is in: a red run must not count.
        var afterEdit = new SampleTaskChecklist(2);
        Assert.False(afterEdit.ToolFinished(false, "execute_command", "dotnet test", isError: true));
        Assert.Equal(2, afterEdit.Completed);
    }

    [Fact]
    public void ToolFinished_AGreenTestRunAfterTheEdit_TicksSeeTestsPass()
    {
        var list = new SampleTaskChecklist(2);

        Assert.True(list.ToolFinished(false, "execute_command", "dotnet test TodoApp.slnx", isError: false));

        Assert.Equal(3, list.Completed);
    }

    [Fact]
    public void ToolFinished_AGreenTestRunBeforeTheEdit_IsIgnored()
    {
        var list = new SampleTaskChecklist(1);

        Assert.False(list.ToolFinished(false, "execute_command", "dotnet test", isError: false));

        Assert.Equal(1, list.Completed);
    }

    [Fact]
    public void ToolFinished_ACommitByTheAgentTicksCommit_OtherToolsDoNot()
    {
        var list = new SampleTaskChecklist(3);

        Assert.False(list.ToolFinished(false, "read_file", "git commit", isError: false));
        Assert.False(list.ToolFinished(false, "execute_command", "git status", isError: false));
        Assert.True(list.ToolFinished(false, "execute_command", "git commit -am fix", isError: false));

        Assert.True(list.IsComplete);
    }
}
