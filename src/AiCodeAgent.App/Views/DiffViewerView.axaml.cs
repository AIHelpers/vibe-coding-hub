using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

public partial class DiffViewerView : UserControl
{
    public DiffViewerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
