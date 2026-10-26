using System;
using AiCodeAgent.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiCodeAgent.App.Views;

/// <summary>
/// Renders the <see cref="ChatTimeline"/> and feeds scroll positions to <see cref="ChatAutoScroll"/>,
/// which decides whether to follow the newest content.
/// </summary>
public partial class ChatTimelineView : UserControl
{
    private ChatTimeline? _timeline;

    public ChatTimelineView()
    {
        InitializeComponent();
        TimelineScroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_timeline != null) _timeline.AutoScroll.ScrollToEndRequested -= OnScrollToEndRequested;
        _timeline = DataContext as ChatTimeline;
        if (_timeline != null) _timeline.AutoScroll.ScrollToEndRequested += OnScrollToEndRequested;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_timeline != null) _timeline.AutoScroll.ScrollToEndRequested -= OnScrollToEndRequested;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_timeline != null)
        {
            _timeline.AutoScroll.ScrollToEndRequested -= OnScrollToEndRequested;
            _timeline.AutoScroll.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    /// <summary>Distance in px between the bottom of the viewport and the end of the content.</summary>
    public double DistanceFromBottom =>
        Math.Max(0, TimelineScroll.Extent.Height - TimelineScroll.Viewport.Height - TimelineScroll.Offset.Y);

    private void OnScrollToEndRequested(object? sender, EventArgs e) => TimelineScroll.ScrollToEnd();

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_timeline == null) return;
        var auto = _timeline.AutoScroll;

        // Only the user scrolls up; our own ScrollToEnd only ever moves down. So an upward move re-evaluates
        // following (and may stop it), and any move that lands near the bottom resumes it. A downward move that
        // stays far from the bottom changes nothing, which keeps a coalesced "content grew + we scrolled" event
        // from being mistaken for the user leaving the bottom.
        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            var distance = DistanceFromBottom;
            if (e.OffsetDelta.Y < 0 || distance <= ChatAutoScroll.FollowThreshold)
                auto.OnUserScrolled(distance);
        }

        // Content got taller (streamed text, a new item laid out): follow it if we are following.
        if (e.ExtentDelta.Y > 0.5)
            auto.OnContentChanged(isStreaming: _timeline.IsStreaming);
    }

    private async void CopyTextButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChatTextItem item } || string.IsNullOrEmpty(item.Content)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(item.Content);
    }
}
