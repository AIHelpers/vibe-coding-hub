using System;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// The Review Queue panel (E10-S3): the list, the item in front of the reviewer, and the three answers: approve, request
/// changes with a comment, decline. After an answer the next item is selected, so a keyboard-only pass through the queue
/// is J/K to look, A to accept, R and a comment to send something back.
/// </summary>
public sealed partial class ReviewQueueViewModel : ObservableObject
{
    private string? _selectedId;
    private int _selectedIndex;
    private bool _keepComment;

    public ReviewQueueViewModel(ReviewQueue queue)
    {
        Queue = queue;
        Queue.Items.CollectionChanged += OnItemsChanged;
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReviewQueue.Count))
            {
                OnPropertyChanged(nameof(PendingCount));
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(SummaryText));
            }
            else if (e.PropertyName is nameof(ReviewQueue.BlockingCount))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        };
        SelectedItem = Queue.Items.FirstOrDefault();
    }

    public ReviewQueue Queue { get; }

    /// <summary>The panel is open. While it is, approvals are answered here and the modal dialog stays away.</summary>
    [ObservableProperty] private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanRequestChanges))]
    [NotifyPropertyChangedFor(nameof(CanDecline))]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private ReviewItem? _selectedItem;

    /// <summary>The comment box is open.</summary>
    [ObservableProperty] private bool _isEnteringComment;

    [ObservableProperty] private string _comment = string.Empty;

    /// <summary>The outcome of the last action, or why it did not happen.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>What the badge on the activity bar shows.</summary>
    public int PendingCount => Queue.Count;
    public bool HasItems => Queue.Count > 0;
    public bool IsEmpty => Queue.Count == 0;
    public bool HasSelection => SelectedItem != null;
    public bool CanRequestChanges => SelectedItem?.CanRequestChanges == true;
    public bool CanDecline => SelectedItem?.CanDecline == true;
    public bool HasPreview => SelectedItem?.Preview != null;

    public string SummaryText
    {
        get
        {
            if (Queue.Count == 0) return "Nothing is waiting for you.";
            var blocking = Queue.BlockingCount;
            var text = $"{Queue.Count} waiting";
            return blocking > 0 ? text + $" · {blocking} blocking an agent" : text;
        }
    }

    partial void OnSelectedItemChanged(ReviewItem? value)
    {
        if (value != null)
        {
            _selectedId = value.Id;
            _selectedIndex = Math.Max(0, Queue.Items.IndexOf(value));
        }
        // A comment belongs to the item it was typed for.
        if (!_keepComment)
        {
            IsEnteringComment = false;
            Comment = string.Empty;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));

        if (SelectedItem != null && Queue.Items.Contains(SelectedItem)) return;

        if (Queue.Items.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        // The item is gone or was replaced by a fresh snapshot of itself: stay on the same one, else on its neighbour.
        var same = _selectedId == null ? null : Queue.Items.FirstOrDefault(i => i.Id == _selectedId);
        _keepComment = same != null;
        try { SelectedItem = same ?? Queue.Items[Math.Min(_selectedIndex, Queue.Items.Count - 1)]; }
        finally { _keepComment = false; }
    }

    [RelayCommand]
    public void Next() => Move(+1);

    [RelayCommand]
    public void Previous() => Move(-1);

    private void Move(int delta)
    {
        if (Queue.Items.Count == 0) return;
        var index = SelectedItem == null ? -1 : Queue.Items.IndexOf(SelectedItem);
        SelectedItem = Queue.Items[Math.Clamp(index + delta, 0, Queue.Items.Count - 1)];
    }

    [RelayCommand]
    public void ApproveSelected()
    {
        if (SelectedItem is not { } item) return;
        Report(Queue.Approve(item), $"Approved: {item.Title}");
    }

    [RelayCommand]
    public void DeclineSelected()
    {
        if (SelectedItem is not { } item) return;
        if (!item.CanDecline) { StatusText = "This item cannot be declined."; return; }
        Report(Queue.Decline(item), $"{item.DeclineLabel}: {item.Title}");
    }

    [RelayCommand]
    public void BeginRequestChanges()
    {
        if (SelectedItem is not { } item) return;
        if (!item.CanRequestChanges)
        {
            StatusText = "This item has no way to send a comment. Approve or decline it.";
            return;
        }
        StatusText = string.Empty;
        IsEnteringComment = true;
    }

    [RelayCommand]
    public void CancelComment()
    {
        IsEnteringComment = false;
        Comment = string.Empty;
    }

    [RelayCommand]
    public void SubmitComment()
    {
        if (SelectedItem is not { } item || !IsEnteringComment) return;
        if (string.IsNullOrWhiteSpace(Comment)) { StatusText = "Write what should change."; return; }

        var result = Queue.RequestChanges(item, Comment);
        // A refused comment stays in the box, so nothing typed is lost.
        if (result.Done)
        {
            IsEnteringComment = false;
            Comment = string.Empty;
        }
        Report(result, $"Sent back: {item.Title}");
    }

    private void Report(ReviewResult result, string done)
    {
        StatusText = result.Done ? result.Message ?? done : result.Message ?? "Nothing changed.";
    }

    [RelayCommand]
    public void Close() => IsVisible = false;
}
