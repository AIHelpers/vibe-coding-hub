using System.Collections.Generic;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.Tests.Chat;

/// <summary>E2-S3: follow the newest content until the user scrolls up; the pill brings it back.</summary>
public class AutoScrollBehaviorTests
{
    private static (ChatTimeline Timeline, List<string> Scrolls) Create()
    {
        var t = new ChatTimeline();
        var scrolls = new List<string>();
        t.AutoScroll.ScrollToEndRequested += (_, _) => scrolls.Add("end");
        return (t, scrolls);
    }

    [Fact]
    public void Initially_Following_WithoutPill()
    {
        var auto = new ChatAutoScroll();
        Assert.True(auto.IsFollowing);
        Assert.False(auto.ShowNewMessagesPill);
    }

    [Fact]
    public void WhileFollowing_NewItemsAndStreamedText_ScrollToEnd()
    {
        var (t, scrolls) = Create();
        var run = t.BeginRun();
        run.AppendText("Hello");          // new bubble
        run.AppendText(" world");         // growth of the same bubble
        t.ToolStarted(new ToolCall { Name = "read_file" });

        Assert.Equal(3, scrolls.Count);
        Assert.False(t.AutoScroll.ShowNewMessagesPill);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(40, true)]
    [InlineData(80, true)]
    [InlineData(81, false)]
    [InlineData(500, false)]
    public void ScrollingUpMoreThan80Px_StopsFollowing(double distanceFromBottom, bool following)
    {
        var auto = new ChatAutoScroll();
        auto.OnUserScrolled(distanceFromBottom);
        Assert.Equal(following, auto.IsFollowing);
    }

    [Fact]
    public void AfterScrollingUp_NewContentDoesNotScroll_ButShowsThePill()
    {
        var (t, scrolls) = Create();
        var run = t.BeginRun();
        run.AppendText("step 1");
        scrolls.Clear();

        t.AutoScroll.OnUserScrolled(300);
        run.AppendText(" more");
        t.ToolStarted(new ToolCall { Name = "read_file" });

        Assert.Empty(scrolls);
        Assert.False(t.AutoScroll.IsFollowing);
        Assert.True(t.AutoScroll.ShowNewMessagesPill);
    }

    [Fact]
    public void ClickingThePill_JumpsDownAndFollowsAgain()
    {
        var (t, scrolls) = Create();
        t.AutoScroll.OnUserScrolled(300);
        t.BeginRun().AppendText("new");
        Assert.True(t.AutoScroll.ShowNewMessagesPill);

        t.AutoScroll.JumpToLatestCommand.Execute(null);

        Assert.Equal(new[] { "end" }, scrolls);
        Assert.True(t.AutoScroll.IsFollowing);
        Assert.False(t.AutoScroll.ShowNewMessagesPill);

        t.BeginRun().AppendText("next");
        Assert.Equal(2, scrolls.Count); // following again
    }

    [Fact]
    public void ScrollingBackNearTheBottom_ResumesFollowing_AndHidesThePill()
    {
        var (t, _) = Create();
        t.AutoScroll.OnUserScrolled(300);
        t.BeginRun().AppendText("new");

        t.AutoScroll.OnUserScrolled(10);

        Assert.True(t.AutoScroll.IsFollowing);
        Assert.False(t.AutoScroll.ShowNewMessagesPill);
    }

    [Fact]
    public void LayoutOnlyGrowth_WhileScrolledUp_DoesNotShowThePill()
    {
        var auto = new ChatAutoScroll();
        auto.OnUserScrolled(300);
        auto.OnContentChanged(isStreaming: false); // e.g. the user expanded a tool card
        Assert.False(auto.ShowNewMessagesPill);
    }

    [Fact]
    public void SendingAMessage_AlwaysJumpsToTheBottom()
    {
        var (t, scrolls) = Create();
        t.AutoScroll.OnUserScrolled(300);

        t.AddUser("next question");

        Assert.Equal(new[] { "end" }, scrolls);
        Assert.True(t.AutoScroll.IsFollowing);
    }

    [Fact]
    public void ClearingTheChat_ResetsToFollowing()
    {
        var (t, _) = Create();
        t.AutoScroll.OnUserScrolled(300);
        t.BeginRun().AppendText("x");

        t.Clear();

        Assert.True(t.AutoScroll.IsFollowing);
        Assert.False(t.AutoScroll.ShowNewMessagesPill);
    }
}
