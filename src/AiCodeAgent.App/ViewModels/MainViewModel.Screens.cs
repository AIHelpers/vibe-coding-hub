using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// The application's top-level "screens" (panels). In Screens mode only one of
/// them is shown at a time, full width, and switching slides between them —
/// like swiping between Spaces on macOS. The declaration order is the
/// left-to-right order of the screens and drives the slide direction.
/// </summary>
public enum AppScreen
{
    Explorer = 0,
    Editor = 1,
    Chat = 2,
    Agents = 3,
    Preview = 4
}

public partial class MainViewModel
{
    /// <summary>When true, one panel fills the window and switching between panels slides.</summary>
    [ObservableProperty]
    private bool _isSlideMode;

    /// <summary>The panel currently shown in Screens mode.</summary>
    [ObservableProperty]
    private AppScreen _activeScreen = AppScreen.Chat;

    // ----- Visibility / sizing consumed by MainWindow.axaml -----

    public bool ShowEditorPanel => EditorPane.HasOpenTabs && (!IsSlideMode || ActiveScreen == AppScreen.Editor);
    public bool ShowChatPanel => !IsSlideMode || ActiveScreen == AppScreen.Chat;
    public bool ShowAgentsPanel => (ChatViewModel?.IsAgentSidebarOpen ?? false) && (!IsSlideMode || ActiveScreen == AppScreen.Agents);
    public bool ShowPreviewPanel => IsPreviewPaneOpen && (!IsSlideMode || ActiveScreen == AppScreen.Preview);

    public GridLength EditorColumnWidth => ShowEditorPanel ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    public GridLength ChatColumnWidth => ShowChatPanel ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    public GridLength AgentsColumnWidth => !ShowAgentsPanel ? new GridLength(0)
        : IsSlideMode ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    public GridLength PreviewColumnWidth => !ShowPreviewPanel ? new GridLength(0)
        : IsSlideMode ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

    public double AgentsMaxWidth => IsSlideMode ? double.PositiveInfinity : 220;
    public double PreviewMaxWidth => IsSlideMode ? double.PositiveInfinity : 400;

    /// <summary>Draggable editor/chat splitter only makes sense when both are visible side by side.</summary>
    public bool ShowEditorChatSplitter => !IsSlideMode && EditorPane.HasOpenTabs;

    /// <summary>Explorer is a SplitView pane: in Screens mode it opens only on its own screen.</summary>
    public bool IsExplorerPaneOpen => IsSlideMode ? ActiveScreen == AppScreen.Explorer : IsFileExplorerOpen;

    /// <summary>Width of the collapsed pane strip. None: a closed Explorer is fully hidden
    /// (a 40px sliver only showed a clipped, unusable fragment of the tree).</summary>
    public double ExplorerCompactLength => 0;

    // ----- Pager state (which pager button is highlighted) -----

    public bool IsExplorerScreen => ActiveScreen == AppScreen.Explorer;
    public bool IsEditorScreen => ActiveScreen == AppScreen.Editor;
    public bool IsChatScreen => ActiveScreen == AppScreen.Chat;
    public bool IsAgentsScreen => ActiveScreen == AppScreen.Agents;
    public bool IsPreviewScreen => ActiveScreen == AppScreen.Preview;

    private void InitializeScreens()
    {
        EditorPane.PropertyChanged += OnScreenDependencyChanged;
        if (ChatViewModel != null)
        {
            ChatViewModel.PropertyChanged += OnScreenDependencyChanged;
        }
    }

    private void OnScreenDependencyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPaneViewModel.HasOpenTabs) ||
            e.PropertyName == nameof(ChatViewModel.IsAgentSidebarOpen))
        {
            EnsureActiveScreenAvailable();
            RaiseScreenProperties();
        }
    }

    partial void OnIsSlideModeChanged(bool value)
    {
        EnsureActiveScreenAvailable();
        RaiseScreenProperties();
    }

    partial void OnActiveScreenChanged(AppScreen value) => RaiseScreenProperties();

    partial void OnIsFileExplorerOpenChanged(bool value) => OnPropertyChanged(nameof(IsExplorerPaneOpen));

    partial void OnIsPreviewPaneOpenChanged(bool value)
    {
        EnsureActiveScreenAvailable();
        RaiseScreenProperties();
    }

    private bool IsScreenAvailable(AppScreen screen) => screen switch
    {
        AppScreen.Explorer => true,
        AppScreen.Editor => EditorPane.HasOpenTabs,
        AppScreen.Chat => true,
        AppScreen.Agents => ChatViewModel?.IsAgentSidebarOpen ?? false,
        AppScreen.Preview => IsPreviewPaneOpen,
        _ => false
    };

    /// <summary>If the current screen disappeared (last tab closed, sidebar closed), fall back to Chat.</summary>
    private void EnsureActiveScreenAvailable()
    {
        if (IsSlideMode && !IsScreenAvailable(ActiveScreen))
        {
            ActiveScreen = AppScreen.Chat;
        }
    }

    private void RaiseScreenProperties()
    {
        OnPropertyChanged(nameof(ShowEditorPanel));
        OnPropertyChanged(nameof(ShowChatPanel));
        OnPropertyChanged(nameof(ShowAgentsPanel));
        OnPropertyChanged(nameof(ShowPreviewPanel));
        OnPropertyChanged(nameof(EditorColumnWidth));
        OnPropertyChanged(nameof(ChatColumnWidth));
        OnPropertyChanged(nameof(AgentsColumnWidth));
        OnPropertyChanged(nameof(PreviewColumnWidth));
        OnPropertyChanged(nameof(AgentsMaxWidth));
        OnPropertyChanged(nameof(PreviewMaxWidth));
        OnPropertyChanged(nameof(ShowEditorChatSplitter));
        OnPropertyChanged(nameof(IsExplorerPaneOpen));
        OnPropertyChanged(nameof(ExplorerCompactLength));
        OnPropertyChanged(nameof(IsExplorerScreen));
        OnPropertyChanged(nameof(IsEditorScreen));
        OnPropertyChanged(nameof(IsChatScreen));
        OnPropertyChanged(nameof(IsAgentsScreen));
        OnPropertyChanged(nameof(IsPreviewScreen));
    }

    [RelayCommand]
    private void ToggleSlideMode()
    {
        IsSlideMode = !IsSlideMode;
    }

    /// <summary>Switch to <paramref name="screen"/> (Screens mode only; ignored if the panel isn't available).</summary>
    [RelayCommand]
    private void GoToScreen(AppScreen screen)
    {
        if (!IsSlideMode)
            return;

        if (IsScreenAvailable(screen))
        {
            ActiveScreen = screen;
        }

        // Always re-raise so a pager ToggleButton that flipped itself visually snaps back.
        RaiseScreenProperties();
    }

    [RelayCommand]
    private void NextScreen() => StepScreen(+1);

    [RelayCommand]
    private void PreviousScreen() => StepScreen(-1);

    private void StepScreen(int direction)
    {
        if (!IsSlideMode)
            return;

        var count = Enum.GetValues<AppScreen>().Length;
        var index = (int)ActiveScreen;
        for (var i = 0; i < count; i++)
        {
            index += direction;
            if (index < 0 || index >= count)
                return; // no wrap-around: stay on the edge screen

            if (IsScreenAvailable((AppScreen)index))
            {
                ActiveScreen = (AppScreen)index;
                return;
            }
        }
    }
}
