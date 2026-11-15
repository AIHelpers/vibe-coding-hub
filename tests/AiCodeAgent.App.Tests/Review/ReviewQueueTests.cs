using System;
using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using Avalonia.Input;
using Xunit;

namespace AiCodeAgent.App.Tests.Review;

internal sealed class FakeSource : IReviewSource
{
    public string Name => "fake";
    public List<ReviewItem> Items { get; } = new();
    public event EventHandler? Changed;
    public IReadOnlyList<ReviewItem> Pending() => Items.ToList();
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public static ReviewItem Item(string id, ReviewKind kind = ReviewKind.Approval, DateTimeOffset? since = null,
        Func<ReviewResult>? approve = null, Func<string, ReviewResult>? changes = null, Func<ReviewResult>? decline = null) => new()
    {
        Id = id, Kind = kind, Title = id, Since = since ?? DateTimeOffset.UtcNow,
        Approve = approve ?? (() => ReviewResult.Ok()), RequestChanges = changes, Decline = decline
    };
}

public class ReviewQueueTests
{
    private static (ReviewQueue Queue, FakeSource Source) Create()
    {
        var q = new ReviewQueue();
        var s = new FakeSource();
        q.AddSource(s);
        return (q, s);
    }

    [Fact]
    public void Refresh_PutsBlockingItemsFirst_ThenOldestFirst()
    {
        var (q, s) = Create();
        var t = DateTimeOffset.UtcNow;
        s.Items.Add(FakeSource.Item("task", ReviewKind.FinishedTask, t.AddMinutes(-30)));
        s.Items.Add(FakeSource.Item("new", ReviewKind.Approval, t));
        s.Items.Add(FakeSource.Item("old", ReviewKind.Gate, t.AddMinutes(-5)));
        q.Refresh();

        Assert.Equal(new[] { "old", "new", "task" }, q.Items.Select(i => i.Id));
        Assert.Equal(3, q.Count);
        Assert.Equal(2, q.BlockingCount);
    }

    [Fact]
    public void Refresh_KeepsTheSameInstance_WhileNothingChanged()
    {
        var (q, s) = Create();
        s.Items.Add(FakeSource.Item("a"));
        q.Refresh();
        var first = q.Items[0];
        s.Items[0] = FakeSource.Item("a", since: first.Since);
        q.Refresh();
        Assert.Same(first, q.Items[0]);
    }

    [Fact]
    public void SourceChange_RefreshesTheList_AndAnsweredItemsLeave()
    {
        var (q, s) = Create();
        s.Items.Add(FakeSource.Item("a"));
        s.Raise();
        Assert.Single(q.Items);
        s.Items.Clear();
        s.Raise();
        Assert.Empty(q.Items);
    }

    [Fact]
    public void Approve_RunsTheItemAction_AndRefreshes()
    {
        var (q, s) = Create();
        var item = FakeSource.Item("a", approve: () => { s.Items.Clear(); return ReviewResult.Ok(); });
        s.Items.Add(item);
        q.Refresh();
        var result = q.Approve(q.Items[0]);
        Assert.True(result.Done);
        Assert.Empty(q.Items);
    }

    [Fact]
    public void RequestChanges_RefusesBlankComments_AndItemsWithoutSupport()
    {
        var (q, s) = Create();
        var sent = new List<string>();
        s.Items.Add(FakeSource.Item("with", changes: c => { sent.Add(c); return ReviewResult.Ok(); }));
        s.Items.Add(FakeSource.Item("without"));
        q.Refresh();
        var with = q.Items.First(i => i.Id == "with");
        var without = q.Items.First(i => i.Id == "without");

        Assert.False(q.RequestChanges(with, "  ").Done);
        Assert.False(q.RequestChanges(without, "fix it").Done);
        Assert.Empty(sent);
        Assert.True(q.RequestChanges(with, "rename it").Done);
        Assert.Equal(new[] { "rename it" }, sent);
    }

    [Fact]
    public void Decline_IsRefused_WhenTheItemCannotBeDeclined()
    {
        var (q, s) = Create();
        s.Items.Add(FakeSource.Item("a"));
        q.Refresh();
        Assert.False(q.Decline(q.Items[0]).Done);
    }
}

public class ReviewKeysTests
{
    [Theory]
    [InlineData(Key.J, ReviewKeyAction.Next)]
    [InlineData(Key.Down, ReviewKeyAction.Next)]
    [InlineData(Key.K, ReviewKeyAction.Previous)]
    [InlineData(Key.Up, ReviewKeyAction.Previous)]
    [InlineData(Key.A, ReviewKeyAction.Approve)]
    [InlineData(Key.Enter, ReviewKeyAction.Approve)]
    [InlineData(Key.R, ReviewKeyAction.RequestChanges)]
    [InlineData(Key.X, ReviewKeyAction.Decline)]
    [InlineData(Key.Delete, ReviewKeyAction.Decline)]
    public void Browsing_MapsKeysToActions(Key key, ReviewKeyAction expected) =>
        Assert.Equal(expected, ReviewKeys.Resolve(key, KeyModifiers.None, false, false, false));

    [Fact]
    public void Enter_OnAButton_IsLeftToTheButton() =>
        Assert.Equal(ReviewKeyAction.None, ReviewKeys.Resolve(Key.Enter, KeyModifiers.None, false, false, true));

    [Fact]
    public void WhileWritingAComment_OnlyEscAndCtrlEnterAct()
    {
        Assert.Equal(ReviewKeyAction.None, ReviewKeys.Resolve(Key.A, KeyModifiers.None, true, true, false));
        Assert.Equal(ReviewKeyAction.None, ReviewKeys.Resolve(Key.X, KeyModifiers.None, true, true, false));
        Assert.Equal(ReviewKeyAction.CancelComment, ReviewKeys.Resolve(Key.Escape, KeyModifiers.None, true, true, false));
        Assert.Equal(ReviewKeyAction.SubmitComment, ReviewKeys.Resolve(Key.Enter, KeyModifiers.Control, true, true, false));
    }
}

public class ReviewQueueViewModelTests
{
    private static (ReviewQueueViewModel Vm, FakeSource Source) Create(params ReviewItem[] items)
    {
        var q = new ReviewQueue();
        var s = new FakeSource();
        s.Items.AddRange(items);
        q.AddSource(s);
        q.Refresh();
        return (new ReviewQueueViewModel(q), s);
    }

    [Fact]
    public void AfterAnAnswer_TheNextItemIsSelected()
    {
        var t = DateTimeOffset.UtcNow;
        FakeSource? src = null;
        var a = FakeSource.Item("a", since: t.AddMinutes(-2), approve: () => { src!.Items.RemoveAll(i => i.Id == "a"); return ReviewResult.Ok(); });
        var b = FakeSource.Item("b", since: t.AddMinutes(-1));
        var (vm, s) = Create(a, b);
        src = s;
        vm.SelectedItem = vm.Queue.Items[0];

        vm.ApproveSelected();

        Assert.Equal("b", vm.SelectedItem?.Id);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact]
    public void NextAndPrevious_StayInsideTheList()
    {
        var t = DateTimeOffset.UtcNow;
        var (vm, _) = Create(FakeSource.Item("a", since: t.AddMinutes(-2)), FakeSource.Item("b", since: t.AddMinutes(-1)));
        vm.SelectedItem = vm.Queue.Items[0];
        vm.Next(); vm.Next();
        Assert.Equal("b", vm.SelectedItem?.Id);
        vm.Previous(); vm.Previous();
        Assert.Equal("a", vm.SelectedItem?.Id);
    }

    [Fact]
    public void Comment_IsSentWithTheItem_AndClearedAfterwards()
    {
        string? sent = null;
        var (vm, _) = Create(FakeSource.Item("a", changes: c => { sent = c; return ReviewResult.Ok(); }));
        vm.SelectedItem = vm.Queue.Items[0];

        vm.BeginRequestChanges();
        Assert.True(vm.IsEnteringComment);
        vm.SubmitComment();                       // blank: nothing goes out
        Assert.Null(sent);
        vm.Comment = "Use the existing helper";
        vm.SubmitComment();

        Assert.Equal("Use the existing helper", sent);
        Assert.False(vm.IsEnteringComment);
        Assert.Equal(string.Empty, vm.Comment);
    }

    [Fact]
    public void RefusedComment_StaysInTheBox()
    {
        var (vm, _) = Create(FakeSource.Item("a", changes: _ => ReviewResult.Refused("Busy")));
        vm.SelectedItem = vm.Queue.Items[0];
        vm.BeginRequestChanges();
        vm.Comment = "later";
        vm.SubmitComment();
        Assert.True(vm.IsEnteringComment);
        Assert.Equal("later", vm.Comment);
        Assert.Equal("Busy", vm.StatusText);
    }

    [Fact]
    public void ItemsWithoutComments_CannotEnterCommentMode()
    {
        var (vm, _) = Create(FakeSource.Item("a"));
        vm.SelectedItem = vm.Queue.Items[0];
        vm.BeginRequestChanges();
        Assert.False(vm.IsEnteringComment);
        Assert.False(vm.CanRequestChanges);
    }
}
