using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>The Review Queue (E10-S3): one list of everything waiting for a human, docked in the side-panel host.</summary>
public partial class MainViewModel
{
    /// <summary>Approvals, flow gates and decisions, and finished tasks that nobody has looked at.</summary>
    public ReviewQueueViewModel Review { get; private set; } = null!;

    private void InitializeReview()
    {
        Review = new ReviewQueueViewModel(new ReviewQueue(RunOnUi));

        var chat = GetOrCreateChatViewModel();
        Review.Queue.AddSource(new ChatReviewSource(chat));
        Review.Queue.AddSource(new FlowReviewSource(Flows));

        // While the panel is open approvals are answered there; the modal dialog stays away and comes back when it closes.
        Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReviewQueueViewModel.IsVisible))
                chat.ApprovalsInReviewQueue = Review.IsVisible;
        };
    }

    /// <summary>Opens or closes the Review Queue panel (Ctrl+Shift+R).</summary>
    [RelayCommand]
    private void ToggleReview()
    {
        if (Review.IsVisible)
        {
            Review.Close();
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        Review.IsVisible = true;
    }
}
