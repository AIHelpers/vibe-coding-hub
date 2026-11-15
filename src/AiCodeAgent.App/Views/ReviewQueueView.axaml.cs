using System.ComponentModel;
using AiCodeAgent.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AiCodeAgent.App.Views;

/// <summary>E10-S3: the Review Queue panel. Keys act only while focus is inside it; see <see cref="ReviewKeys"/>.</summary>
public partial class ReviewQueueView : UserControl
{
    private ReviewQueueViewModel? _vm;

    public ReviewQueueView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Attach(DataContext as ReviewQueueViewModel);
    }

    private void Attach(ReviewQueueViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm != null) _vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        // Keys only reach the panel when it has focus: put it there when it opens, and move between list and comment box.
        if (e.PropertyName == nameof(ReviewQueueViewModel.IsVisible) && _vm.IsVisible)
            Dispatcher.UIThread.Post(() => ReviewList.Focus(), DispatcherPriority.Background);
        else if (e.PropertyName == nameof(ReviewQueueViewModel.IsEnteringComment))
            Dispatcher.UIThread.Post(() => (_vm.IsEnteringComment ? (Control)CommentBox : ReviewList).Focus(), DispatcherPriority.Background);
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var action = ReviewKeys.Resolve(e.Key, e.KeyModifiers, _vm.IsEnteringComment,
            focusInTextInput: focus is TextBox, focusIsOnButton: focus is Button);

        switch (action)
        {
            case ReviewKeyAction.Next: _vm.NextCommand.Execute(null); break;
            case ReviewKeyAction.Previous: _vm.PreviousCommand.Execute(null); break;
            case ReviewKeyAction.Approve: _vm.ApproveSelectedCommand.Execute(null); break;
            case ReviewKeyAction.RequestChanges: _vm.BeginRequestChangesCommand.Execute(null); break;
            case ReviewKeyAction.Decline: _vm.DeclineSelectedCommand.Execute(null); break;
            case ReviewKeyAction.SubmitComment: _vm.SubmitCommentCommand.Execute(null); break;
            case ReviewKeyAction.CancelComment: _vm.CancelCommentCommand.Execute(null); break;
            default: return;
        }
        // The list would otherwise also move on its own with the arrow keys.
        e.Handled = true;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
