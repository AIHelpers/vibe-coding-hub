using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

public partial class BackgroundTaskManagerView : UserControl
{
    public BackgroundTaskManagerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
