using AiCodeAgent.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiCodeAgent.App.Views;

public partial class BackgroundTaskManagerView : UserControl
{
    public BackgroundTaskManagerView()
    {
        InitializeComponent();
    }

    private async void OnCopyOutputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BackgroundTaskItemViewModel { HasOutput: true } task }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(task.OutputMarkdown.Trim());
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
