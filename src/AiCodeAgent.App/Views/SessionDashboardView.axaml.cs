using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using Avalonia.Controls;
using Avalonia.Input;

namespace AiCodeAgent.App.Views;

public partial class SessionDashboardView : UserControl
{
    public SessionDashboardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Clicking a session card selects it as the active session,
    /// switching the main chat/editor panes to its isolated state.
    /// </summary>
    private void Session_Clicked(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { DataContext: AgentSession session } &&
            DataContext is SessionDashboardViewModel vm)
        {
            vm.SelectSessionCommand.Execute(session.Id);
        }
    }
}