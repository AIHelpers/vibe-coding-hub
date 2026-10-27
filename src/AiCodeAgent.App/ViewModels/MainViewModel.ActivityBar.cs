using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// The activity bar and the side-panel host (E4-S1). Source control, background tasks, checkpoints, session history,
/// the session dashboard and the autonomous plan dock into ONE host column next to the editor and chat; only one of
/// them is open at a time. Which one, and how wide, is remembered per workspace.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The labelled, grouped bar on the left edge. Replaces the old row of emoji buttons.</summary>
    public ActivityBarViewModel ActivityBar { get; private set; } = null!;

    private SidePanelMemory? _panelMemory;
    private bool _restoringPanels;
    private string? _lastActivePanel;
    private double _preferredSidePanelWidth = SidePanelLayout.DefaultWidth;
    private double _windowWidth = double.PositiveInfinity;

    /// <summary>Width of the docked panel in pixels (already limited to what the window can spare).</summary>
    [ObservableProperty]
    private double _sidePanelWidth = SidePanelLayout.DefaultWidth;

    /// <summary>The side panel that is open (see <see cref="SidePanelIds"/>), or null.</summary>
    public string? ActiveSidePanel =>
        SourceControl.IsVisible ? SidePanelIds.Changes
        : BackgroundTasks.IsVisible ? SidePanelIds.Tasks
        : IsCheckpointBrowserOpen ? SidePanelIds.Checkpoints
        : IsSessionHistoryOpen ? SidePanelIds.Sessions
        : IsSessionDashboardOpen ? SidePanelIds.Dashboard
        : _chatViewModel?.PlanVM.IsVisible == true ? SidePanelIds.Plan
        : null;

    public bool IsSidePanelOpen => ActiveSidePanel != null;

    // Panel visibility bindings used by the side-panel host (the plan panel is owned by the chat view-model).
    public bool IsPlanPanelOpen => _chatViewModel?.PlanVM.IsVisible == true;

    private void InitializeActivityBar()
    {
        var ui = _configurationService?.Config.Ui;
        if (ui != null) _panelMemory = new SidePanelMemory(ui);

        ActivityBar = new ActivityBarViewModel(
            new[]
            {
                new ActivityBarGroup("WORK", new[]
                {
                    new ActivityBarItem(ActivityBarIds.Chat, "Chat", ActivityBarIcons.Chat, ShowChatCommand, description: "back to the conversation"),
                    new ActivityBarItem(ActivityBarIds.Explorer, "Explorer", ActivityBarIcons.Explorer, ToggleFileExplorerCommand, "Ctrl+B", "files of the project"),
                    new ActivityBarItem(ActivityBarIds.Changes, "Changes", ActivityBarIcons.Changes, ToggleSourceControlCommand, "Ctrl+Shift+G", "stage, diff and commit"),
                    new ActivityBarItem(ActivityBarIds.Flows, "Flows", ActivityBarIcons.Flows, ToggleFlowsCommand, description: "draw and run flows of characters"),
                    new ActivityBarItem(ActivityBarIds.Tasks, "Tasks", ActivityBarIcons.Tasks, ToggleBackgroundTasksCommand, description: "delegate work, keep chatting", badgeColor: "#4A9EFF"),
                }),
                new ActivityBarGroup("TEAM", new[]
                {
                    new ActivityBarItem(ActivityBarIds.Characters, "Characters", ActivityBarIcons.Characters, ToggleCharactersCommand, description: "personas with their own skills"),
                    new ActivityBarItem(ActivityBarIds.Skills, "Skills", ActivityBarIcons.Skills, ToggleSkillsCommand, description: "reusable know-how"),
                    new ActivityBarItem(ActivityBarIds.AgentsMd, "AGENTS.md", ActivityBarIcons.AgentsMd, ToggleAgentsMdCommand, description: "project memory and checks"),
                }),
                new ActivityBarGroup("HISTORY", new[]
                {
                    new ActivityBarItem(ActivityBarIds.Checkpoints, "Checkpoints", ActivityBarIcons.Checkpoints, ToggleCheckpointBrowserCommand, description: "restore earlier file versions"),
                    new ActivityBarItem(ActivityBarIds.Sessions, "Sessions", ActivityBarIcons.Sessions, ToggleSessionHistoryCommand, description: "export, import and resume"),
                    new ActivityBarItem(ActivityBarIds.Dashboard, "Dashboard", ActivityBarIcons.Dashboard, ToggleSessionDashboardCommand, description: "multi-agent sessions"),
                }),
            },
            new[]
            {
                new ActivityBarItem(ActivityBarIds.Settings, "Settings", ActivityBarIcons.Settings, NavigateToSettingsCommand, "Ctrl+,"),
                new ActivityBarItem(ActivityBarIds.Theme, "Theme", ActivityBarIcons.Theme, ToggleThemeCommand, description: "switch light / dark"),
            },
            expanded: ui?.ActivityBarExpanded ?? false);

        ActivityBar.ExpandedChanged += expanded =>
        {
            if (_configurationService == null) return;
            _configurationService.Config.Ui ??= new UiConfiguration();
            _configurationService.Config.Ui.ActivityBarExpanded = expanded;
            SafeFireAndForget(SaveLayoutAsync(), "SaveActivityBar");
        };

        // Anything that changes which page or panel is showing re-evaluates the bar.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsSettingsMode) or nameof(IsCheckpointBrowserOpen) or nameof(IsSessionHistoryOpen)
                or nameof(IsSessionDashboardOpen) or nameof(IsExplorerPaneOpen) or nameof(IsFileExplorerOpen) or nameof(IsSlideMode)
                or nameof(ActiveScreen))
                RefreshNavigationState();
        };
        foreach (var source in new object[] { SourceControl, Flows, BackgroundTasks, ProjectKnowledge, DiffViewer })
        {
            // Some of these are ReactiveObjects, which implement the event explicitly.
            ((INotifyPropertyChanged)source).PropertyChanged += OnNavigationSourcePropertyChanged;
        }
        FileExplorer.Changes.CollectionChanged += OnGitChangesCollectionChanged;
        WatchPlanPanel(GetOrCreateChatViewModel());

        RefreshNavigationState();
        RestoreSidePanelForWorkspace();
    }

    private void OnNavigationSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsVisible" or "ActiveTabIndex" or "RunningCount")
            RunOnUi(RefreshNavigationState);
    }

    private void OnGitChangesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RunOnUi(RefreshNavigationState);

    /// <summary>Hooks the plan panel once the chat view-model exists (its visibility is part of the side-panel state).</summary>
    private void WatchPlanPanel(ChatViewModel chat)
    {
        ((INotifyPropertyChanged)chat.PlanVM).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlanViewModel.IsVisible)) return;
            RunOnUi(() =>
            {
                // The agent opened its plan: it takes the host, so no other panel stays open next to it.
                if (chat.PlanVM.IsVisible && (SourceControl.IsVisible || BackgroundTasks.IsVisible || IsCheckpointBrowserOpen
                                              || IsSessionHistoryOpen || IsSessionDashboardOpen))
                {
                    CloseOverlays(keepPlan: true);
                }
                RefreshNavigationState();
            });
        };
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ----- Activity bar commands -----

    /// <summary>"Chat": leaves settings, closes the open panel or page and shows the conversation.</summary>
    [RelayCommand]
    private void ShowChat()
    {
        CloseOverlays();
        NavigateToChat();
        if (IsSlideMode) GoToScreen(AppScreen.Chat);
    }

    [RelayCommand]
    private Task ToggleAgentsMdAsync() => ToggleProjectKnowledgeTabAsync(0);

    [RelayCommand]
    private Task ToggleSkillsAsync() => ToggleProjectKnowledgeTabAsync(1);

    [RelayCommand]
    private Task ToggleCharactersAsync() => ToggleProjectKnowledgeTabAsync(2);

    /// <summary>Opens Project Knowledge on a tab (0 = AGENTS.md, 1 = Skills, 2 = Characters); pressing the same entry again closes it.</summary>
    private async Task ToggleProjectKnowledgeTabAsync(int tab)
    {
        if (ProjectKnowledge.IsVisible && ProjectKnowledge.ActiveTabIndex == tab)
        {
            ProjectKnowledge.Close();
            return;
        }
        await OpenProjectKnowledgeTabAsync(tab, startNew: false);
    }

    /// <summary>Shows or hides the labels of the activity bar.</summary>
    [RelayCommand]
    private void ToggleActivityBarLabels() => ActivityBar.IsExpanded = !ActivityBar.IsExpanded;

    /// <summary>Palette: brings back the autonomous plan panel after another panel replaced it.</summary>
    [RelayCommand]
    private void ShowPlanPanel()
    {
        var chat = GetOrCreateChatViewModel();
        if (chat.PlanVM.Steps.Count == 0)
        {
            StatusText = "There is no plan yet. Start an autonomous run to get one.";
            return;
        }
        CloseOverlays(keepPlan: true);
        IsSettingsMode = false;
        chat.PlanVM.IsVisible = true;
    }

    // ----- State of the bar and the host -----

    private bool IsMainAreaOverlayOpen => ProjectKnowledge.IsVisible || Flows.IsVisible || DiffViewer.IsVisible;

    /// <summary>Recomputes which activity bar entries are active, the badges, and the host state; saves the panel choice.</summary>
    private void RefreshNavigationState()
    {
        if (ActivityBar == null) return;

        var active = new List<string>();
        var panel = ActiveSidePanel;

        if (IsSettingsMode) active.Add(ActivityBarIds.Settings);
        if (!IsSettingsMode && IsExplorerPaneOpen) active.Add(ActivityBarIds.Explorer);
        if (panel == SidePanelIds.Changes) active.Add(ActivityBarIds.Changes);
        if (panel == SidePanelIds.Tasks) active.Add(ActivityBarIds.Tasks);
        if (panel == SidePanelIds.Checkpoints) active.Add(ActivityBarIds.Checkpoints);
        if (panel == SidePanelIds.Sessions) active.Add(ActivityBarIds.Sessions);
        if (panel == SidePanelIds.Dashboard) active.Add(ActivityBarIds.Dashboard);
        if (Flows.IsVisible) active.Add(ActivityBarIds.Flows);
        if (ProjectKnowledge.IsVisible)
        {
            active.Add(ProjectKnowledge.ActiveTabIndex switch
            {
                1 => ActivityBarIds.Skills,
                2 => ActivityBarIds.Characters,
                _ => ActivityBarIds.AgentsMd
            });
        }
        if (!IsSettingsMode && panel == null && !IsMainAreaOverlayOpen) active.Add(ActivityBarIds.Chat);

        ActivityBar.SetActive(active);
        ActivityBar.SetBadge(ActivityBarIds.Changes, FileExplorer.Changes.Count);
        ActivityBar.SetBadge(ActivityBarIds.Tasks, BackgroundTasks.RunningCount);

        OnPropertyChanged(nameof(ActiveSidePanel));
        OnPropertyChanged(nameof(IsSidePanelOpen));
        OnPropertyChanged(nameof(IsPlanPanelOpen));
        PersistActiveSidePanel(panel);
    }

    // ----- Width -----

    /// <summary>The window width changed: the panel may not take more than the window can spare.</summary>
    public void UpdateWindowWidth(double width)
    {
        _windowWidth = width;
        SidePanelWidth = SidePanelLayout.ClampWidth(_preferredSidePanelWidth, _windowWidth);
    }

    /// <summary>Drag of the splitter at the panel's left edge: negative delta (to the left) widens the panel.</summary>
    public void ResizeSidePanel(double deltaX)
    {
        _preferredSidePanelWidth = SidePanelLayout.ClampWidth(SidePanelWidth - deltaX, _windowWidth);
        SidePanelWidth = _preferredSidePanelWidth;
    }

    /// <summary>The drag ended: remember the width for this workspace.</summary>
    public void CommitSidePanelWidth()
    {
        var panel = ActiveSidePanel;
        if (panel == null || panel == SidePanelIds.Plan) return;
        RememberPanel(panel);
    }

    // ----- Per-workspace memory -----

    private void PersistActiveSidePanel(string? panel)
    {
        var previous = _lastActivePanel;
        _lastActivePanel = panel;
        if (_restoringPanels || _panelMemory == null || previous == panel) return;
        // The plan is opened by the agent, not by the user: it is neither remembered nor does its closing forget the real choice.
        if (panel == SidePanelIds.Plan || (previous == SidePanelIds.Plan && panel == null)) return;
        RememberPanel(panel);
    }

    private void RememberPanel(string? panel)
    {
        if (_panelMemory == null || _configurationService == null) return;
        if (_panelMemory.Remember(WorkingDirectory, panel, _preferredSidePanelWidth))
            SafeFireAndForget(SaveLayoutAsync(), "SaveSidePanel");
    }

    private async Task SaveLayoutAsync()
    {
        if (_configurationService == null) return;
        await _configSaveLock.WaitAsync().ConfigureAwait(false);
        try { await _configurationService.SaveAsync().ConfigureAwait(false); }
        finally { _configSaveLock.Release(); }
    }

    /// <summary>On start and whenever the working folder changes: bring back this workspace's side panel and width.</summary>
    private void RestoreSidePanelForWorkspace()
    {
        if (_panelMemory == null || ActivityBar == null) return;
        var state = _panelMemory.Get(WorkingDirectory);
        if (state == null) return;

        _restoringPanels = true;
        try
        {
            _preferredSidePanelWidth = state.Width;
            SidePanelWidth = SidePanelLayout.ClampWidth(state.Width, _windowWidth);

            var current = ActiveSidePanel;
            if (state.ActivePanel == null)
            {
                if (current != null && current != SidePanelIds.Plan) CloseOverlays();
            }
            else if (current != state.ActivePanel)
            {
                OpenSidePanel(state.ActivePanel);
            }
        }
        finally
        {
            _restoringPanels = false;
            _lastActivePanel = ActiveSidePanel;
        }
    }

    /// <summary>Opens a side panel by id (the panel must be closed). Toggles already close whatever else is open.</summary>
    private void OpenSidePanel(string id)
    {
        switch (id)
        {
            case SidePanelIds.Changes: ToggleSourceControl(); break;
            case SidePanelIds.Tasks: ToggleBackgroundTasks(); break;
            case SidePanelIds.Checkpoints: ToggleCheckpointBrowser(); break;
            case SidePanelIds.Sessions: ToggleSessionHistory(); break;
            case SidePanelIds.Dashboard: ToggleSessionDashboard(); break;
        }
    }
}
