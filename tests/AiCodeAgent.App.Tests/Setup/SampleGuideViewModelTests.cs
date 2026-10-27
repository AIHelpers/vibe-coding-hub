using AiCodeAgent.App.ViewModels;

namespace AiCodeAgent.App.Tests.Setup;

/// <summary>E3-S3: the checklist overlay of the sample project.</summary>
public class SampleGuideViewModelTests
{
    private const string Project = "/docs/TodoApp";

    [Fact]
    public void StartsWithFourSteps_TheFirstOneIsTheCurrentStep()
    {
        var guide = new SampleGuideViewModel(Project);

        Assert.Equal(
            new[] { "Send the task", "Approve the edit", "See tests pass", "Commit in Source Control" },
            guide.Items.Select(i => i.Label));
        Assert.True(guide.Items[0].IsCurrent);
        Assert.All(guide.Items, i => Assert.False(i.IsDone));
        Assert.Equal("0 of 4", guide.Progress);
        Assert.Contains("Enter", guide.Hint);
        Assert.False(guide.IsComplete);
    }

    [Fact]
    public void ATick_MovesTheCurrentStep_UpdatesTheMarks_AndRaisesChanged()
    {
        var guide = new SampleGuideViewModel(Project);
        var changed = 0;
        guide.Changed += (_, _) => changed++;
        var progressChanged = false;
        guide.PropertyChanged += (_, e) => progressChanged |= e.PropertyName == nameof(SampleGuideViewModel.Progress);

        guide.Checklist.TaskSent();

        Assert.Equal(1, changed);
        Assert.True(progressChanged);
        Assert.Equal("1 of 4", guide.Progress);
        Assert.True(guide.Items[0].IsDone);
        Assert.Equal("✓", guide.Items[0].Mark);
        Assert.Equal("2", guide.Items[1].Mark);
        Assert.True(guide.Items[1].IsCurrent);
        Assert.False(guide.Items[0].IsCurrent);
        Assert.Contains("approve", guide.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restoring_ShowsTheStepsThatWereAlreadyDone()
    {
        var guide = new SampleGuideViewModel(Project, completed: 2);

        Assert.True(guide.Items[0].IsDone);
        Assert.True(guide.Items[1].IsDone);
        Assert.True(guide.Items[2].IsCurrent);
        Assert.Equal("2 of 4", guide.Progress);
    }

    [Fact]
    public void FinishingTheLastStep_ShowsTheDoneMessage_ThenDismissesForGood()
    {
        var guide = new SampleGuideViewModel(Project, completed: 3, autoHideAfter: TimeSpan.Zero);
        var order = new List<string>();
        guide.Changed += (_, _) => order.Add("changed");
        guide.Dismissed += (_, _) => order.Add("dismissed");

        guide.Checklist.Committed();

        Assert.True(guide.IsComplete);
        Assert.Contains("All done", guide.Hint);
        Assert.Equal(new[] { "changed", "dismissed" }, order);
    }

    [Fact]
    public async Task TheDoneMessage_StaysForAMoment_BeforeItGoesAway()
    {
        var guide = new SampleGuideViewModel(Project, completed: 3, autoHideAfter: TimeSpan.FromMilliseconds(40));
        var dismissed = new TaskCompletionSource();
        guide.Dismissed += (_, _) => dismissed.TrySetResult();

        guide.Checklist.Committed();

        Assert.False(dismissed.Task.IsCompleted);
        await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Hide_DismissesOnce_EvenWhenPressedTwice()
    {
        var guide = new SampleGuideViewModel(Project);
        var dismissed = 0;
        guide.Dismissed += (_, _) => dismissed++;

        guide.HideCommand.Execute(null);
        guide.HideCommand.Execute(null);

        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void SamePath_IgnoresATrailingSlash()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TodoApp");

        Assert.True(MainViewModel.SamePath(folder, folder + Path.DirectorySeparatorChar));
        Assert.False(MainViewModel.SamePath(folder, folder + "-2"));
        Assert.False(MainViewModel.SamePath(folder, null));
        Assert.False(MainViewModel.SamePath("", folder));
    }
}
