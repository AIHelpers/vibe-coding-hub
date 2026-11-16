using Avalonia.Controls;
using Avalonia.Interactivity;
using AiCodeAgent.App.ViewModels;

namespace AiCodeAgent.App.Views;

public partial class MissionControlView : UserControl
{
    public MissionControlView()
    {
        InitializeComponent();
    }

    private async void OnCopyBranchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BoardCardViewModel { HasBranch: true } card }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(card.BranchText);
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
