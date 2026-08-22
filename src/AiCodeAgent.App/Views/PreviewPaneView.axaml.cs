using System;
using AiCodeAgent.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AiCodeAgent.App.Views;

public partial class PreviewPaneView : UserControl
{
    public PreviewPaneView()
    {
        InitializeComponent();
    }

    private void OnElementClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag is PreviewElement element
            && DataContext is PreviewPaneViewModel vm)
        {
            vm.SelectElementCommand.Execute(element);
        }
    }
}