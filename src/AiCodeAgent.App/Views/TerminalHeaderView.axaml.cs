using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

/// <summary>Header row of the terminal pane: title, a "running" indicator while a command runs, and Clear.</summary>
public partial class TerminalHeaderView : UserControl
{
    public TerminalHeaderView()
    {
        InitializeComponent();
    }
}
