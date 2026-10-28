using System;
using AiCodeAgent.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AiCodeAgent.App.Views;

/// <summary>
/// The terminal panel: shell tabs, the Agent output tab, a few actions and a drag handle on the top edge.
/// The sessions live in <see cref="TerminalViewModel"/>, so hiding the panel does not end any shell.
/// </summary>
public partial class TerminalPanelView : UserControl
{
    private TerminalViewModel? _vm;
    private double? _dragStartY;

    public TerminalPanelView()
    {
        InitializeComponent();
        ResizeHandle.PointerPressed += OnHandlePressed;
        ResizeHandle.PointerMoved += OnHandleMoved;
        ResizeHandle.PointerReleased += OnHandleReleased;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.FocusRequested -= OnFocusRequested;
        _vm = DataContext as TerminalViewModel;
        if (_vm != null) _vm.FocusRequested += OnFocusRequested;
    }

    private void OnFocusRequested()
    {
        // After the panel became visible and the control was laid out.
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.IsShellSelected == true) ShellHost.Focus();
        }, DispatcherPriority.Input);
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TerminalTabViewModel tab }) _vm?.CloseTabCommand.Execute(tab);
        e.Handled = true;
    }

    private void OnSendSelection(object? sender, RoutedEventArgs e)
    {
        _vm?.SendSelectionToChat(ShellHost.SelectedText);
    }

    private void OnAskAgent(object? sender, RoutedEventArgs e)
    {
        _vm?.AskAgentAbout(ShellHost.SelectedText);
    }

    private double? CurrentY(PointerEventArgs e) => TopLevel.GetTopLevel(this) is { } top ? e.GetPosition(top).Y : null;

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartY = CurrentY(e);
        if (_dragStartY != null) e.Pointer.Capture(ResizeHandle);
        e.Handled = true;
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartY is not { } start || CurrentY(e) is not { } y) return;
        _vm?.ResizeBy(start - y); // dragging up makes the panel taller
        _dragStartY = y;
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartY = null;
        e.Pointer.Capture(null);
    }
}
