using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

public partial class CheckpointBrowserView : UserControl
{
    public CheckpointBrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
