using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Agent;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeAgent.App.ViewModels;

/// <summary>Mission Control (E11-S1): the task board, shown over the main area like the Flows editor.</summary>
public partial class MainViewModel
{
    /// <summary>The board: Backlog, Running, Needs review and Done; one card per task, each in its own worktree.</summary>
    public MissionControlViewModel MissionControl { get; private set; } = null!;

    private void InitializeMissionControl()
    {
        var agent = _serviceProvider.GetService<AgentService>();
        MissionControl = new MissionControlViewModel(
            _serviceProvider.GetService<IWorkspaceIsolation>(),
            agent == null ? null : new AgentServiceBoardRunner(agent),
            marshal: RunOnUi);
        MissionControl.SetProject(WorkingDirectory);
    }

    /// <summary>Opens or closes Mission Control.</summary>
    [RelayCommand]
    private void ToggleMissionControl()
    {
        if (MissionControl.IsVisible)
        {
            MissionControl.IsVisible = false;
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        MissionControl.SetProject(WorkingDirectory);
        MissionControl.IsVisible = true;
    }
}
