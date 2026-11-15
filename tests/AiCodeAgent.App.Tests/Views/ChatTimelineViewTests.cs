using System;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>E2-S3: the chat renders as one ItemsControl in event order, with the "↓ New messages" pill.</summary>
public class ChatTimelineViewTests
{
    /// <summary>
    /// Lays the window out until the timeline's scroll extent stops changing. The markdown viewers build their content over
    /// more than one dispatcher pass, so a single measure/arrange can leave the last streamed text unmeasured on a slow or
    /// busy machine (CI runs all test assemblies at once). Bounded, so a layout that never settles still fails fast.
    /// </summary>
    private static void LayoutWindow(Window window)
    {
        var scroll = (window.Content as ChatTimelineView)?.FindControl<ScrollViewer>("TimelineScroll");
        var previous = double.NaN;
        for (var pass = 0; pass < 20; pass++)
        {
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Dispatcher.UIThread.RunJobs();

            var extent = scroll?.Extent.Height ?? 0;
            if (pass > 0 && extent.Equals(previous)) break;
            previous = extent;
        }
    }

    [AvaloniaFact]
    public void Timeline_RendersEveryItemTypeInOrder_InOneItemsControl()
    {
        var timeline = new ChatTimeline();
        var view = new ChatTimelineView { DataContext = timeline };
        var window = new Window { Width = 700, Height = 900, Content = view };
        window.Show();

        var read = new ToolCall { Name = "read_file", Arguments = new() { ["path"] = "a.cs" } };
        timeline.AddUser("Add a filter");
        var run = timeline.BeginRun();
        run.AppendText("Reading first.");
        timeline.ToolStarted(read);
        timeline.ToolEnded(read, new ToolResult { Content = "class A {}" }, TimeSpan.FromSeconds(0.1));
        timeline.AddApproval(new ToolCall { Name = "edit_file", Arguments = new() { ["path"] = "a.cs" } }, "Write").Resolve(true);
        run.AppendText("Done.");
        run.Complete();
        timeline.AddNotice("Retrieved context");
        LayoutWindow(window);

        var itemsControls = view.GetVisualDescendants().OfType<ItemsControl>()
            .Where(i => ReferenceEquals(i.ItemsSource, timeline.Items)).ToList();
        var items = Assert.Single(itemsControls);

        var contexts = items.GetRealizedContainers().Select(c => c.DataContext).ToList();
        Assert.Equal(timeline.Items.Cast<object>().ToList(), contexts);
        Assert.Equal(new[] { typeof(UserMessageItem), typeof(AssistantTextItem), typeof(ToolCallItem), typeof(ApprovalItem), typeof(AssistantTextItem), typeof(SystemNoticeItem) },
            contexts.Select(c => c!.GetType()));

        Assert.Single(view.GetVisualDescendants().OfType<ToolCallCardView>());
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "✓ Approved");
    }

    [AvaloniaFact]
    public void Pill_IsShownOnlyWhenScrolledUpWithNewContent_AndJumpsBack()
    {
        var timeline = new ChatTimeline();
        var view = new ChatTimelineView { DataContext = timeline };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        LayoutWindow(window);

        var pill = view.FindControl<Button>("NewMessagesPill")!;
        Assert.False(pill.IsVisible);

        timeline.AutoScroll.OnUserScrolled(500);
        timeline.BeginRun().AppendText("new output");
        LayoutWindow(window);
        Assert.True(pill.IsVisible);

        pill.Command!.Execute(pill.CommandParameter);
        LayoutWindow(window);
        Assert.False(pill.IsVisible);
        Assert.True(timeline.AutoScroll.IsFollowing);
    }

    [AvaloniaFact]
    public void LongStream_WhileFollowing_KeepsTheViewAtTheBottom()
    {
        var timeline = new ChatTimeline();
        var view = new ChatTimelineView { DataContext = timeline };
        var window = new Window { Width = 600, Height = 300, Content = view };
        window.Show();
        LayoutWindow(window);

        var run = timeline.BeginRun();
        for (var i = 0; i < 40; i++)
        {
            run.AppendText($"Line {i}\n\n");
            LayoutWindow(window);
        }

        var scroll = view.FindControl<ScrollViewer>("TimelineScroll")!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "content should overflow the viewport");
        Assert.True(view.DistanceFromBottom <= ChatAutoScroll.FollowThreshold, $"distance from bottom was {view.DistanceFromBottom}");
        Assert.True(timeline.AutoScroll.IsFollowing);
    }
}
