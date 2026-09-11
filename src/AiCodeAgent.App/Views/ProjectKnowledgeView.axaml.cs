using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

public partial class ProjectKnowledgeView : UserControl
{
    public ProjectKnowledgeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
