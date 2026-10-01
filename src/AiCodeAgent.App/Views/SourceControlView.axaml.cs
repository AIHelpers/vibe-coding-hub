using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

public partial class SourceControlView : UserControl
{
    public SourceControlView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
