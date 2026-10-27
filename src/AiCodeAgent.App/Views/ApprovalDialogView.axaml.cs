using System.ComponentModel;
using AiCodeAgent.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiCodeAgent.App.Views;

/// <summary>The approval dialog's content: preview of what will happen plus the decision buttons.</summary>
public partial class ApprovalDialogView : UserControl
{
    private ChatViewModel? _chat;

    public ApprovalDialogView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_chat != null) _chat.PropertyChanged -= OnChatPropertyChanged;
        _chat = DataContext as ChatViewModel;
        if (_chat != null) _chat.PropertyChanged += OnChatPropertyChanged;
    }

    // Opening "Deny with feedback…" puts the cursor in the box right away.
    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsEnteringApprovalFeedback) && _chat?.IsEnteringApprovalFeedback == true)
            Dispatcher.UIThread.Post(() => FeedbackBox.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
    }
}
