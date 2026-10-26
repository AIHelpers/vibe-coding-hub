using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// "Stick to the bottom" logic for the chat, kept out of the view so it is testable. The view reports scroll
/// positions and content growth; this decides whether to follow and when to show the "↓ New messages" pill.
/// </summary>
public sealed partial class ChatAutoScroll : ObservableObject
{
    /// <summary>Scrolled up further than this (in px) from the bottom, the view stops following.</summary>
    public const double FollowThreshold = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewMessagesPill))]
    private bool _isFollowing = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewMessagesPill))]
    private bool _hasUnseenContent;

    /// <summary>Visible when the user has scrolled up and something new arrived below.</summary>
    public bool ShowNewMessagesPill => !IsFollowing && HasUnseenContent;

    /// <summary>The view scrolls to the end when this fires.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>The user moved the scroll position; <paramref name="distanceFromBottom"/> is in px.</summary>
    public void OnUserScrolled(double distanceFromBottom)
    {
        if (distanceFromBottom > FollowThreshold)
        {
            IsFollowing = false;
        }
        else
        {
            IsFollowing = true;
            HasUnseenContent = false;
        }
    }

    /// <summary>A new item was added to the timeline.</summary>
    public void OnItemAdded(ChatItem item)
    {
        // The user's own message always brings the view back down.
        if (item is UserMessageItem) JumpToLatest();
        else OnContentChanged(isStreaming: true);
    }

    /// <summary>
    /// Content grew (a new item, streamed text, a taller card). While following, scroll down; otherwise, if it is
    /// new agent output (<paramref name="isStreaming"/>), show the pill. Layout-only growth (expanding a card) does not.
    /// </summary>
    public void OnContentChanged(bool isStreaming)
    {
        if (IsFollowing)
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        else if (isStreaming)
            HasUnseenContent = true;
    }

    [RelayCommand]
    public void JumpToLatest()
    {
        IsFollowing = true;
        HasUnseenContent = false;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        IsFollowing = true;
        HasUnseenContent = false;
    }
}
