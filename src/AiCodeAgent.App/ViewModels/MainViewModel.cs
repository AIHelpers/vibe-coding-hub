using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.CommandPalette;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using AiCodeAgent.Indexing;
using AiCodeAgent.Indexing.Models;

namespace AiCodeAgent.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private bool _isFileExplorerOpen = true;

    [ObservableProperty]
    private string _fileExplorerStatus = string.Empty;

    [ObservableProperty]
    private bool _isSettingsMode;

    [ObservableProperty]
    private bool _isCheckpointBrowserOpen;

    [ObservableProperty]
    private bool _isSessionHistoryOpen;

    [ObservableProperty]
    private bool _isSessionDashboardOpen;

    private ChatViewModel? _chatViewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkspaceIndexQueryService? _indexQueryService;
    private readonly ConfigurationService? _configurationService;
    private Window? _hostWindow;

    public ChatViewModel? ChatViewModel => _chatViewModel;
    public FileExplorerViewModel FileExplorer { get; }
    public TerminalViewModel Terminal { get; }
    public EditorPaneViewModel EditorPane { get; }
    public PreviewPaneViewModel PreviewPane { get; }
    public CheckpointBrowserViewModel CheckpointBrowser { get; }
    public SessionManagerViewModel SessionManager { get; }
    public SessionDashboardViewModel SessionDashboard { get; }
    public CommandPaletteViewModel CommandPalette { get; }
    public DiffViewerViewModel DiffViewer { get; }
    public QuickOpenViewModel QuickOpen { get; }
    public ProjectKnowledgeViewModel ProjectKnowledge { get; }

    /// <summary>The Flows panel: draw and run flows of characters.</summary>
    public FlowEditorViewModel Flows { get; }
    public BackgroundTaskManagerViewModel BackgroundTasks { get; }
    public SourceControlViewModel SourceControl { get; }

    /// <summary>The diff overlay leaves room on the right while the Source Control panel is docked there.</summary>
    public Avalonia.Thickness DiffOverlayMargin => SourceControl.IsVisible
        ? new Avalonia.Thickness(40, 40, 404, 40)
        : new Avalonia.Thickness(40);

    [ObservableProperty]
    private bool _isPreviewPaneOpen;

    public MainViewModel(
        IServiceProvider serviceProvider,
        FileExplorerViewModel fileExplorer,
        TerminalViewModel terminal,
        EditorPaneViewModel editorPane,
        PreviewPaneViewModel previewPane,
        CheckpointBrowserViewModel checkpointBrowser,
        SessionManagerViewModel sessionManager,
        SessionDashboardViewModel sessionDashboard,
        CommandPaletteViewModel commandPalette,
        DiffViewerViewModel diffViewer,
        QuickOpenViewModel quickOpen,
        ProjectKnowledgeViewModel projectKnowledge,
        BackgroundTaskManagerViewModel backgroundTasks,
        SourceControlViewModel sourceControl,
        WorkspaceIndexQueryService? indexQueryService = null,
        FlowEditorViewModel? flows = null)
    {
        _serviceProvider = serviceProvider;
        FileExplorer = fileExplorer;
        Terminal = terminal;
        EditorPane = editorPane;
        PreviewPane = previewPane;
        CheckpointBrowser = checkpointBrowser;
        SessionManager = sessionManager;
        SessionDashboard = sessionDashboard;
        CommandPalette = commandPalette;
        DiffViewer = diffViewer;
        QuickOpen = quickOpen;
        ProjectKnowledge = projectKnowledge;
        Flows = flows ?? new FlowEditorViewModel();
        BackgroundTasks = backgroundTasks;
        BackgroundTasks.AddProjectRequested += OnBackgroundTaskAddProjectRequested;
        SourceControl = sourceControl;
        SourceControl.DiffRequested += OnSourceControlDiffRequested;
        SourceControl.OpenFileRequested += OnSourceControlOpenFileRequested;
        SourceControl.PropertyChanged += OnSourceControlPropertyChanged;
        _indexQueryService = indexQueryService;
        _configurationService = serviceProvider.GetService<ConfigurationService>();

        // Initialize working directory from persisted configuration, falling
        // back to the application base directory when unset.
        var configuredDir = _configurationService?.Config.Agent?.WorkingDirectory;
        WorkingDirectory = !string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir)
            ? configuredDir
            : AppDomain.CurrentDomain.BaseDirectory;

        // Sync the file explorer to the same root so it shows the configured
        // workspace immediately on startup.
        FileExplorer.RootPath = WorkingDirectory;
        FileExplorer.SetExtraFolders(_configurationService?.Config.Agent?.AdditionalFolders ?? new List<string>());

        // Default to Chat view (reuse the same instance, don't create a new one)
        _currentViewModel ??= GetOrCreateChatViewModel();

        // Screens mode (animated one-panel-at-a-time layout): track panel availability
        InitializeScreens();

        // Register command palette entries (navigation + slash commands + editor actions)
        RegisterPaletteEntries();

        // Load file explorer
        FileExplorer.LoadCommand.Execute(null);

        // Load checkpoints
        CheckpointBrowser.RefreshCommand.Execute(null);

        // Update status
        UpdateFileExplorerStatus();

        // Feature 9: Forward freehand visual annotations from the preview pane
        // to the chat view-model so the agent receives them as instructions.
        var chatForAnnotation = GetOrCreateChatViewModel();
        PreviewPane.AnnotationCommitted += annotation =>
        {
            _ = chatForAnnotation.SendAnnotationAsync(annotation);
        };

        // Route "view diff" requests from the checkpoint browser to the
        // standalone diff viewer panel.
        CheckpointBrowser.ViewDiffRequested += OnCheckpointViewDiffRequested;

        // React to configuration saves (e.g. when the user changes the working
        // directory in Settings) so the file explorer and status update live.
        if (_configurationService != null)
        {
            _configurationService.Saved += OnConfigurationSaved;
        }
    }

    /// <summary>
    /// Attaches the host window so view-models can access window-scoped
    /// services such as the <see cref="Avalonia.Platform.Storage.IStorageProvider"/>.
    /// </summary>
    public void AttachHostWindow(Window window) => _hostWindow = window;

    /// <summary>
    /// Handles <see cref="ConfigurationService.Saved"/> by refreshing the working
    /// directory from the persisted configuration and reloading the file explorer.
    /// </summary>
    private void OnConfigurationSaved(object? sender, EventArgs e)
    {
        var newDir = _configurationService?.Config.Agent?.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(newDir) && Directory.Exists(newDir) && newDir != WorkingDirectory)
        {
            WorkingDirectory = newDir;
            FileExplorer.SetRootPathCommand.Execute(newDir);
            UpdateFileExplorerStatus();
        }
    }

    /// <summary>
    /// Unsubscribes event handlers to prevent leaks when the view-model is no longer needed.
    /// </summary>
    public void Dispose()
    {
        if (_configurationService != null)
        {
            _configurationService.Saved -= OnConfigurationSaved;
        }
        CheckpointBrowser.ViewDiffRequested -= OnCheckpointViewDiffRequested;
        BackgroundTasks.AddProjectRequested -= OnBackgroundTaskAddProjectRequested;
        SourceControl.DiffRequested -= OnSourceControlDiffRequested;
        SourceControl.OpenFileRequested -= OnSourceControlOpenFileRequested;
        SourceControl.PropertyChanged -= OnSourceControlPropertyChanged;
        SourceControl.Dispose();
        FileExplorer.Dispose();
    }

    /// <summary>
    /// Opens a file chosen in Explorer. Preview tabs are reused on single-click;
    /// a non-preview open pins the tab (double-click / context menu Open).
    /// </summary>
    public async Task OpenExplorerItemAsync(FileExplorerItem? item, bool isPreview)
    {
        if (item == null || !item.CanOpen)
            return;

        CloseOverlays();
        await EditorPane.OpenFileAsync(item.FullPath, isPreview: isPreview);

        // Screens mode: picking a file in the Explorer slides over to the editor.
        if (IsSlideMode && EditorPane.HasOpenTabs)
        {
            GoToScreen(AppScreen.Editor);
        }
    }

    /// <summary>Explorer context menu: show this file's uncommitted changes as a diff against git HEAD.</summary>
    [RelayCommand]
    private Task ViewGitDiff(FileExplorerItem? item)
        => item == null ? Task.CompletedTask : OpenGitDiffAsync(item.FullPath);

    /// <summary>Changes list: show the clicked change as a diff against git HEAD.</summary>
    [RelayCommand]
    private Task ViewGitChange(AiCodeAgent.App.Services.GitFileChange? change)
        => change == null ? Task.CompletedTask : OpenGitDiffAsync(change.FullPath);

    /// <summary>
    /// Opens the diff viewer comparing the file at git HEAD (left) with the working-tree
    /// version (right, editable). New/untracked files diff against an empty baseline and
    /// deleted files against an empty current side.
    /// </summary>
    public async Task OpenGitDiffAsync(string fullPath)
    {
        var change = FileExplorer.FindGitChange(fullPath);
        if (change == null)
        {
            StatusText = $"No uncommitted git changes for {Path.GetFileName(fullPath)}";
            return;
        }

        var baseline = await FileExplorer.GetGitBaselineAsync(change);
        if (baseline == null)
        {
            StatusText = $"Could not read {change.RelativePath} from git";
            return;
        }

        if (baseline.Contains('\0') || IsBinaryFile(change.FullPath))
        {
            StatusText = $"{Path.GetFileName(change.FullPath)} is a binary file — no text diff to show";
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        DiffViewer.Load(change.FullPath, baseline, "Git HEAD");
        if (change.Kind == AiCodeAgent.App.Services.GitChangeKind.Deleted)
            DiffViewer.StatusText = "Deleted in working tree · " + DiffViewer.StatusText;
        else if (change.Kind == AiCodeAgent.App.Services.GitChangeKind.Untracked)
            DiffViewer.StatusText = "New file (untracked) · " + DiffViewer.StatusText;
    }

    private static bool IsBinaryFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var buffer = new byte[8000];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private Task OpenExplorerFile(FileExplorerItem? item)
        => OpenExplorerItemAsync(item, isPreview: false);

    [RelayCommand]
    private Task OpenExplorerFilePreview(FileExplorerItem? item)
        => OpenExplorerItemAsync(item, isPreview: true);

    [RelayCommand]
    private void NavigateToChat()
    {
        // Reuse the existing ChatViewModel instance instead of creating a new one,
        // so the conversation history is preserved across navigation.
        _chatViewModel ??= _serviceProvider.GetRequiredService<ChatViewModel>();
        CurrentViewModel = _chatViewModel;
        IsSettingsMode = false;
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        StatusText = "Chat Mode";
    }

    private void OnSettingsCloseRequested(object? sender, EventArgs e) => NavigateToChat();

    [RelayCommand]
    private void NavigateToSettings()
    {
        // The gear button toggles: pressing it again on the settings page returns to the chat.
        if (IsSettingsMode && CurrentViewModel is SettingsViewModel)
        {
            NavigateToChat();
            return;
        }
        var settings = _serviceProvider.GetRequiredService<SettingsViewModel>();
        // Wire up the host window's storage provider so the folder picker
        // button works (DI registers SettingsViewModel with a null provider).
        if (_hostWindow != null && settings.StorageProvider == null)
        {
            settings.StorageProvider = _hostWindow.StorageProvider;
        }
        settings.CloseRequested -= OnSettingsCloseRequested;
        settings.CloseRequested += OnSettingsCloseRequested;
        CurrentViewModel = settings;
        IsSettingsMode = true;
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        StatusText = "Settings Mode";
    }

    [RelayCommand]
    private void ToggleCommandPalette()
    {
        if (CommandPalette.IsOpen)
        {
            CommandPalette.Close();
        }
        else
        {
            CommandPalette.Open();
        }
    }

    [RelayCommand]
    private void ToggleFileExplorer()
    {
        if (IsSlideMode)
        {
            // In Screens mode Ctrl+B slides to the Explorer, or back to Chat.
            GoToScreen(ActiveScreen == AppScreen.Explorer ? AppScreen.Chat : AppScreen.Explorer);
            return;
        }

        IsFileExplorerOpen = !IsFileExplorerOpen;
    }

    /// <summary>Quick light/dark switch from the toolbar; the choice is saved to configuration.</summary>
    [RelayCommand]
    private async Task ToggleTheme()
    {
        var next = AiCodeAgent.App.Services.ThemeService.IsDark
            ? AiCodeAgent.App.Services.ThemeService.Light
            : AiCodeAgent.App.Services.ThemeService.Dark;
        AiCodeAgent.App.Services.ThemeService.Apply(next);

        if (_configurationService != null)
        {
            _configurationService.Config.Ui ??= new UiConfiguration();
            _configurationService.Config.Ui.Theme = next;
            await _configurationService.SaveAsync();
        }
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        Terminal.ToggleVisibilityCommand.Execute(null);
    }

    [RelayCommand]
    private void TogglePreviewPane()
    {
        IsPreviewPaneOpen = !IsPreviewPaneOpen;
    }

    [RelayCommand]
    private void ToggleCheckpointBrowser()
    {
        if (IsCheckpointBrowserOpen)
        {
            IsCheckpointBrowserOpen = false;
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        IsCheckpointBrowserOpen = true;
        CheckpointBrowser.RefreshCommand.Execute(null);
    }

    /// <summary>
    /// Closes every toolbar-driven panel so opening one never stacks on top of another.
    /// Each toolbar button is a toggle: click once to open its panel, click again to close it.
    /// </summary>
    private void CloseOverlays(bool keepSourceControl = false)
    {
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        SessionManager.CloseSessionHistory();
        IsSessionDashboardOpen = false;
        DiffViewer.Close();
        ProjectKnowledge.Close();
        Flows.Close();
        BackgroundTasks.Close();
        if (!keepSourceControl)
            SourceControl.Close();
    }

    // ----- Source Control panel (stage / diff / commit) -----

    /// <summary>Opens/closes the Source Control panel.</summary>
    [RelayCommand]
    private void ToggleSourceControl()
    {
        if (SourceControl.IsVisible)
        {
            SourceControl.Close();
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        SourceControl.Open();
    }

    private void OnSourceControlPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceControlViewModel.IsVisible))
            OnPropertyChanged(nameof(DiffOverlayMargin));
    }

    private void OnSourceControlDiffRequested(SourceControlEntry entry)
        => SafeFireAndForget(ShowSourceControlDiffAsync(entry), "ShowSourceControlDiff");

    /// <summary>
    /// Shows the clicked change in the diff viewer. The Source Control panel stays open next to it so
    /// the user can step through files. Working-tree rows are editable; staged rows are a read-only HEAD vs. index view.
    /// </summary>
    private async Task ShowSourceControlDiffAsync(SourceControlEntry entry)
    {
        var diff = await SourceControl.PrepareDiffAsync(entry);
        if (diff.Error != null)
        {
            SourceControl.StatusMessage = diff.Error;
            return;
        }

        CloseOverlays(keepSourceControl: true);
        IsSettingsMode = false;

        if (diff.Current == null)
            DiffViewer.Load(diff.FilePath, diff.Baseline, diff.BaselineLabel);
        else
            DiffViewer.LoadComparison(diff.FilePath, diff.Baseline, diff.Current, diff.BaselineLabel, diff.CurrentLabel);

        if (!string.IsNullOrEmpty(diff.Notice))
            DiffViewer.StatusText = diff.Notice + " · " + DiffViewer.StatusText;
    }

    private void OnSourceControlOpenFileRequested(string path)
        => SafeFireAndForget(OpenFileFromSourceControlAsync(path), "OpenFileFromSourceControl");

    private async Task OpenFileFromSourceControlAsync(string path)
    {
        // Keep the panel open; just bring the file up in the editor.
        DiffViewer.Close();
        await EditorPane.OpenFileAsync(path, isPreview: false);
        if (IsSlideMode && EditorPane.HasOpenTabs)
            GoToScreen(AppScreen.Editor);
    }

    [RelayCommand]
    private void QuickOpenFiles()
    {
        QuickOpen.Open();
    }

    [RelayCommand]
    private void QuickOpenSymbols()
    {
        QuickOpen.OpenForSymbols();
    }

    /// <summary>Opens the Project Knowledge panel (AGENTS.md + skills) for the current working directory.</summary>
    [RelayCommand]
    private async Task ToggleProjectKnowledgeAsync()
    {
        if (ProjectKnowledge.IsVisible)
        {
            ProjectKnowledge.Close();
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        await ProjectKnowledge.OpenAsync(WorkingDirectory);
    }

    /// <summary>Opens or closes the Flows panel (draw and run flows of characters).</summary>
    [RelayCommand]
    private async Task ToggleFlowsAsync()
    {
        if (Flows.IsVisible)
        {
            Flows.Close();
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        await Flows.OpenAsync(WorkingDirectory);
    }

    /// <summary>Opens the Flows panel on an empty canvas.</summary>
    private async Task NewFlowAsync()
    {
        if (!Flows.IsVisible)
        {
            CloseOverlays();
            IsSettingsMode = false;
            await Flows.OpenAsync(WorkingDirectory);
        }
        Flows.NewFlow();
    }

    /// <summary>Opens Project Knowledge directly on a tab (1 = Skills, 2 = Characters), optionally starting the "new" form.</summary>
    private async Task OpenProjectKnowledgeTabAsync(int tab, bool startNew)
    {
        if (!ProjectKnowledge.IsVisible)
        {
            CloseOverlays();
            IsSettingsMode = false;
            await ProjectKnowledge.OpenAsync(WorkingDirectory);
        }
        if (tab == 2)
        {
            await ProjectKnowledge.ShowCharactersTabCommand.ExecuteAsync(null);
            if (startNew) ProjectKnowledge.Characters.BeginCreate();
        }
        else
        {
            ProjectKnowledge.ShowSkillsTabCommand.Execute(null);
            if (startNew) ProjectKnowledge.BeginCreateSkill();
        }
    }

    /// <summary>
    /// Opens/closes the Background Tasks panel — Cowork-style delegated
    /// tasks that run independently of the main chat conversation.
    /// </summary>
    [RelayCommand]
    private void ToggleBackgroundTasks()
    {
        if (BackgroundTasks.IsVisible)
        {
            BackgroundTasks.Close();
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        BackgroundTasks.WorkingDirectory = WorkingDirectory;
        RefreshBackgroundTaskProjects();
        BackgroundTasks.IsVisible = true;
    }

    /// <summary>Feeds the Background Tasks panel's project picker: the main folder plus every folder added to the task.</summary>
    private void RefreshBackgroundTaskProjects() =>
        BackgroundTasks.SetProjects(WorkingDirectory, FileExplorer.ExtraFolders);

    /// <summary>The panel's "Add project…" button: reuses the add-folder flow, then refreshes the picker.</summary>
    private async void OnBackgroundTaskAddProjectRequested()
    {
        try
        {
            await AddFolderToTaskCommand.ExecuteAsync(null);
            RefreshBackgroundTaskProjects();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not add project: {ex.Message}";
        }
    }

    /// <summary>
    /// Starts <paramref name="prompt"/> as a detached background task using
    /// the same working directory/permission settings the main chat is
    /// currently using.
    /// </summary>
    public BackgroundTaskItemViewModel StartBackgroundTask(string prompt)
    {
        var options = new AgentOptions
        {
            WorkingDirectory = WorkingDirectory,
            // See BackgroundTaskManagerViewModel.StartNewTask for why AutoEdit
            // (not Ask) is the right default for unattended background work.
            PermissionMode = Core.Models.PermissionMode.AutoEdit
        };
        return BackgroundTasks.StartTask(prompt, options);
    }

    /// <summary>Opens the diff viewer comparing a checkpointed file against its current on-disk content.</summary>
    private void OnCheckpointViewDiffRequested(CheckpointItemViewModel item)
    {
        CloseOverlays();
        IsSettingsMode = false;
        DiffViewer.Load(
            item.FilePath,
            item.OriginalContent,
            $"Checkpoint @ {item.Timestamp:t}",
            item.CheckpointId);
    }

    /// <summary>
    /// Opens the diff viewer for the file in the active editor tab, comparing
    /// it against its own most recent checkpoint (if any) in the current
    /// session — the "what have I changed?" shortcut from the editor itself.
    /// </summary>
    [RelayCommand]
    private async Task ViewActiveFileDiffAsync()
    {
        // Toggle: clicking the diff button again closes the diff viewer.
        if (DiffViewer.IsVisible)
        {
            DiffViewer.Close();
            return;
        }

        var activeTab = EditorPane.ActiveTab;
        if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath))
        {
            StatusText = "Open a file to view its diff";
            return;
        }

        var checkpointManager = _serviceProvider.GetService<ICheckpointManager>();
        if (checkpointManager == null)
            return;

        var checkpoints = await checkpointManager.GetCheckpointsForSessionAsync("default");
        var latest = checkpoints
            .Where(c => string.Equals(c.FilePath, activeTab.FilePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Timestamp)
            .FirstOrDefault();

        if (latest == null)
        {
            StatusText = "No checkpoints for this file yet";
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        DiffViewer.Load(latest.FilePath, latest.OriginalContent, $"Checkpoint @ {latest.Timestamp:t}", latest.CheckpointId);
    }

    [RelayCommand]
    private void ToggleSessionHistory()
    {
        if (IsSessionHistoryOpen)
        {
            CloseOverlays();
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        IsSessionHistoryOpen = true;
        SessionManager.RefreshSessionHistoryCommand.Execute(null);
    }

    [RelayCommand]
    private void ToggleSessionDashboard()
    {
        if (IsSessionDashboardOpen)
        {
            IsSessionDashboardOpen = false;
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        IsSessionDashboardOpen = true;
        SessionDashboard.RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ChangeWorkingDirectoryAsync()
    {
        if (_hostWindow == null)
            return;

        var options = new FolderPickerOpenOptions
        {
            Title = "Select Working Directory",
            AllowMultiple = false
        };

        // Seed the picker with the current directory if it exists
        if (Directory.Exists(WorkingDirectory))
        {
            try
            {
                var folder = await _hostWindow.StorageProvider.TryGetFolderFromPathAsync(WorkingDirectory);
                if (folder != null)
                {
                    options.SuggestedStartLocation = folder;
                }
            }
            catch { /* ignore seeding errors */ }
        }

        var result = await _hostWindow.StorageProvider.OpenFolderPickerAsync(options);
        if (result.Count == 0)
            return; // user cancelled

        var selected = result[0];
        var newPath = selected.Path.LocalPath;

        await ApplyWorkingDirectoryAsync(newPath);
    }

    /// <summary>Lets the user pick one or more folders to add to the task next to the main folder.</summary>
    [RelayCommand]
    private async Task AddFolderToTaskAsync()
    {
        if (_hostWindow == null)
            return;

        var picked = await _hostWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add folders to this task",
            AllowMultiple = true
        });
        if (picked.Count == 0)
            return;

        var skipped = new List<string>();
        var added = 0;
        foreach (var folder in picked)
        {
            var path = folder.Path.LocalPath;
            var reason = FileExplorer.AddExtraFolder(path);
            if (reason == null) added++; else skipped.Add($"{path}: {reason}");
        }

        await PersistExtraFoldersAsync();
        FileExplorer.RefreshCommand.Execute(null);
        StatusText = added > 0
            ? $"Added {added} folder(s) to the task" + (skipped.Count > 0 ? $" ({skipped.Count} skipped: {skipped[0]})" : "")
            : skipped.Count > 0 ? $"Nothing added. {skipped[0]}" : StatusText;
    }

    /// <summary>Removes an extra folder from the task (the main folder cannot be removed here).</summary>
    [RelayCommand]
    private async Task RemoveFolderFromTaskAsync(FileExplorerItem? item)
    {
        if (item is not { IsExtraRoot: true })
            return;

        if (FileExplorer.RemoveExtraFolder(item.FullPath))
        {
            await PersistExtraFoldersAsync();
            FileExplorer.RefreshCommand.Execute(null);
            StatusText = $"Removed {item.Name} from the task";
        }
    }

    private async Task PersistExtraFoldersAsync()
    {
        if (_configurationService?.Config.Agent == null)
            return;
        _configurationService.Config.Agent.AdditionalFolders = FileExplorer.ExtraFolders.ToList();
        await _configurationService.SaveAsync();
    }

    /// <summary>
    /// Changes the working directory to the specified path and updates
    /// dependent services (file explorer, configuration).
    /// Called from the view when the user picks a folder via the header.
    /// </summary>
    public async Task SetWorkingDirectoryAsync(string newPath)
    {
        await ApplyWorkingDirectoryAsync(newPath);
    }

    private async Task ApplyWorkingDirectoryAsync(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath) || newPath == WorkingDirectory)
            return;

        // Update runtime state
        WorkingDirectory = newPath;
        FileExplorer.RootPath = newPath;
        FileExplorer.SetRootPathCommand.Execute(newPath);
        UpdateFileExplorerStatus();

        // Persist to configuration
        if (_configurationService?.Config.Agent != null)
        {
            _configurationService.Config.Agent.WorkingDirectory = newPath;
            await _configurationService.SaveAsync();
        }

        StatusText = $"Working directory: {newPath}";
    }

    [RelayCommand]
    private void RefreshFileExplorer()
    {
        FileExplorer.RefreshCommand.Execute(null);
        UpdateFileExplorerStatus();
    }

    // ---- Editor commands (wired to the Window.KeyBindings in MainWindow.axaml) ----

    /// <summary>Save the file currently active in the editor pane (Ctrl+S).</summary>
    [RelayCommand]
    private async Task SaveActiveFile()
    {
        await EditorPane.SaveActiveAsync();
    }

    /// <summary>Save the active editor file under a new path (Ctrl+Shift+S).</summary>
    [RelayCommand]
    private async Task SaveActiveFileAs()
    {
        await EditorPane.SaveActiveAsAsync();
    }

    /// <summary>Close the active editor tab (Ctrl+W).</summary>
    [RelayCommand]
    private async Task CloseActiveTab()
    {
        await EditorPane.CloseActiveTabAsync();
    }

    /// <summary>Switch to the next editor tab (Ctrl+Tab).</summary>
    [RelayCommand]
    private void NextTab()
    {
        EditorPane.NextTab();
    }

    /// <summary>Switch to the previous editor tab (Ctrl+Shift+Tab).</summary>
    [RelayCommand]
    private void PreviousTab()
    {
        EditorPane.PreviousTab();
    }

    /// <summary>Open an OS file picker and load the chosen file into the editor (Ctrl+O).</summary>
    [RelayCommand]
    private async Task OpenFilePicker()
    {
        if (_hostWindow == null)
            return;

        var options = new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = false
        };

        if (Directory.Exists(WorkingDirectory))
        {
            try
            {
                var folder = await _hostWindow.StorageProvider.TryGetFolderFromPathAsync(WorkingDirectory);
                if (folder != null)
                {
                    options.SuggestedStartLocation = folder;
                }
            }
            catch { /* ignore seeding errors */ }
        }

        var result = await _hostWindow.StorageProvider.OpenFilePickerAsync(options);
        if (result.Count == 0)
            return; // user cancelled

        await EditorPane.OpenFileAsync(result[0].Path.LocalPath);
    }

    private void UpdateFileExplorerStatus()
    {
        var fileCount = FileExplorer.RootItems.Count > 0
            ? CountFiles(FileExplorer.RootItems[0])
            : 0;
        FileExplorerStatus = $"📁 {fileCount} files";
    }

    private static int CountFiles(FileExplorerItem item)
    {
        int count = 0;
        foreach (var child in item.Children)
        {
            if (child.IsPlaceholder)
                continue;
            if (!child.IsDirectory)
                count++;
            else
                count += CountFiles(child);
        }
        return count;
    }

    /// <summary>
    /// Populates the command palette registry with entries from each feature area.
    /// Slash-command entries invoke the same handlers the chat slash-command UI uses.
    /// </summary>
    private void RegisterPaletteEntries()
    {
        var registry = _serviceProvider.GetRequiredService<CommandPaletteRegistry>();
        var chat = GetOrCreateChatViewModel();

        var entries = new List<ICommandPaletteEntry>
        {
            // ---- Navigation ----
            new CommandPaletteEntry
            {
                Id = "nav.chat",
                Title = "Go to Chat",
                Category = "Navigation",
                Keywords = new[] { "chat", "conversation", "messages" },
                KeybindingHint = "",
                Action = NavigateToChat
            },
            new CommandPaletteEntry
            {
                Id = "nav.settings",
                Title = "Open Settings",
                Category = "Navigation",
                Keywords = new[] { "settings", "options", "configuration", "preferences" },
                KeybindingHint = "Ctrl+,",
                Action = NavigateToSettings
            },
            new CommandPaletteEntry
            {
                Id = "nav.checkpointBrowser",
                Title = "Toggle Checkpoint Browser",
                Category = "Navigation",
                Keywords = new[] { "checkpoint", "restore", "history", "snapshots" },
                KeybindingHint = "",
                Action = ToggleCheckpointBrowser
            },
            new CommandPaletteEntry
            {
                Id = "nav.diffViewer",
                Title = "View Diff (active file vs. last checkpoint)",
                Category = "Navigation",
                Keywords = new[] { "diff", "compare", "changes", "hunk", "review" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(ViewActiveFileDiffAsync(), "ViewActiveFileDiff")
            },
            new CommandPaletteEntry
            {
                Id = "nav.projectKnowledge",
                Title = "Project Knowledge (AGENTS.md, Skills, Characters)",
                Category = "Navigation",
                Keywords = new[] { "agents.md", "agent.md", "aiagent.md", "memory", "skills", "skill.md", "doctor", "init" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(ToggleProjectKnowledgeAsync(), "ToggleProjectKnowledge")
            },
            new CommandPaletteEntry
            {
                Id = "nav.characters",
                Title = "Characters (personas with their own skills)",
                Category = "Navigation",
                Keywords = new[] { "character", "characters", "persona", "agent", "role", "skills", "assign" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(OpenProjectKnowledgeTabAsync(2, startNew: false), "OpenCharacters")
            },
            new CommandPaletteEntry
            {
                Id = "characters.new",
                Title = "New Character",
                Category = "Characters",
                Keywords = new[] { "character", "persona", "create", "new", "agent" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(OpenProjectKnowledgeTabAsync(2, startNew: true), "NewCharacter")
            },
            new CommandPaletteEntry
            {
                Id = "nav.flows",
                Title = "Flows (draw and run flows of characters)",
                Category = "Navigation",
                Keywords = new[] { "flow", "flows", "pipeline", "workflow", "graph", "parallel", "characters", "canvas", "drawio" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(ToggleFlowsAsync(), "ToggleFlows")
            },
            new CommandPaletteEntry
            {
                Id = "flows.new",
                Title = "New Flow",
                Category = "Flows",
                Keywords = new[] { "flow", "pipeline", "workflow", "create", "new", "canvas" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(NewFlowAsync(), "NewFlow")
            },
            new CommandPaletteEntry
            {
                Id = "skills.new",
                Title = "New Skill",
                Category = "Skills",
                Keywords = new[] { "skill", "skill.md", "create", "new", "registry" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(OpenProjectKnowledgeTabAsync(1, startNew: true), "NewSkill")
            },
            new CommandPaletteEntry
            {
                Id = "nav.sourceControl",
                Title = "Source Control (stage, diff, commit)",
                Category = "Navigation",
                Keywords = new[] { "git", "commit", "stage", "diff", "changes", "source", "control", "scm", "branch" },
                KeybindingHint = "Ctrl+Shift+G",
                Action = ToggleSourceControl
            },
            new CommandPaletteEntry
            {
                Id = "nav.backgroundTasks",
                Title = "Background Tasks (delegate work)",
                Category = "Navigation",
                Keywords = new[] { "background", "task", "delegate", "cowork", "async", "queue" },
                KeybindingHint = "",
                Action = ToggleBackgroundTasks
            },
            new CommandPaletteEntry
            {
                Id = "nav.slideMode",
                Title = "Toggle Screens Mode (animated panel switching)",
                Category = "Navigation",
                Keywords = new[] { "screens", "slide", "spaces", "panels", "fullscreen", "switch" },
                KeybindingHint = "Ctrl+Shift+M",
                Action = ToggleSlideMode
            },
            new CommandPaletteEntry
            {
                Id = "nav.fileExplorer",
                Title = "Toggle File Explorer",
                Category = "Navigation",
                Keywords = new[] { "files", "explorer", "sidebar", "tree" },
                KeybindingHint = "Ctrl+B",
                Action = ToggleFileExplorer
            },
            new CommandPaletteEntry
            {
                Id = "nav.terminal",
                Title = "Toggle Terminal",
                Category = "Navigation",
                Keywords = new[] { "terminal", "console", "shell", "command" },
                KeybindingHint = "Ctrl+`",
                Action = ToggleTerminal
            },

            // ---- Session Export/Import (Priority 5) ----
            new CommandPaletteEntry
            {
                Id = "nav.sessionHistory",
                Title = "Toggle Session History",
                Category = "Navigation",
                Keywords = new[] { "session", "history", "export", "import", "bundle" },
                KeybindingHint = "",
                Action = ToggleSessionHistory
            },
            new CommandPaletteEntry
            {
                Id = "nav.sessionDashboard",
                Title = "Toggle Session Dashboard",
                Category = "Navigation",
                Keywords = new[] { "session", "dashboard", "multi", "agent", "parallel", "isolated" },
                KeybindingHint = "",
                Action = ToggleSessionDashboard
            },
            new CommandPaletteEntry
            {
                Id = "session.export",
                Title = "Export Session...",
                Category = "Session",
                Keywords = new[] { "export", "session", "bundle", "save", "backup" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(
                    SessionManager.ExportSessionCommand.ExecuteAsync(null), "ExportSession")
            },
            new CommandPaletteEntry
            {
                Id = "session.import",
                Title = "Import Session...",
                Category = "Session",
                Keywords = new[] { "import", "session", "bundle", "open", "restore" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(
                    SessionManager.ImportSessionCommand.ExecuteAsync(null), "ImportSession")
            },

            // ---- Chat Slash Commands (invoke the same handlers as the input popup) ----
            CreateSlashCommandEntry("slash.edit", "/edit", "Edit a specific file", chat, "editing"),
            CreateSlashCommandEntry("slash.search", "/search", "Search the codebase", chat, "searching"),
            CreateSlashCommandEntry("slash.explain", "/explain", "Explain code logic", chat, "explanation"),
            CreateSlashCommandEntry("slash.test", "/test", "Generate tests", chat, "testing"),
            CreateSlashCommandEntry("slash.fix", "/fix", "Fix issues in code", chat, "fixing"),
            CreateSlashCommandEntry("slash.refactor", "/refactor", "Refactor code", chat, "refactoring"),
            CreateSlashCommandEntry("slash.help", "/help", "Show available commands", chat, "help", "commands"),

            // ---- Workspace Index (Go to file / Go to symbol) ----
            new CommandPaletteEntry
            {
                Id = "index.goToFile",
                Title = "Go to File...",
                Category = "Workspace",
                Keywords = new[] { "file", "open", "goto", "navigate", "index", "quick open" },
                KeybindingHint = "Ctrl+P",
                Action = () => QuickOpen.Open()
            },
            new CommandPaletteEntry
            {
                Id = "index.goToSymbol",
                Title = "Go to Symbol...",
                Category = "Workspace",
                Keywords = new[] { "symbol", "goto", "navigate", "index", "class", "method" },
                KeybindingHint = "Ctrl+T",
                Action = () => QuickOpen.OpenForSymbols()
            },

            // ---- Editor ----
            new CommandPaletteEntry
            {
                Id = "editor.openFile",
                Title = "Open File in Editor...",
                Category = "Editor",
                Keywords = new[] { "open", "file", "editor", "picker", "browse" },
                KeybindingHint = "Ctrl+O",
                Action = () => SafeFireAndForget(OpenFilePickerCommand.ExecuteAsync(null), "OpenFilePicker")
            },
            new CommandPaletteEntry
            {
                Id = "editor.save",
                Title = "Save File",
                Category = "Editor",
                Keywords = new[] { "save", "write", "persist" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.SaveActiveAsync(), "SaveActive")
            },
            new CommandPaletteEntry
            {
                Id = "editor.saveAs",
                Title = "Save File As...",
                Category = "Editor",
                Keywords = new[] { "save", "saveas", "write", "rename", "persist" },
                KeybindingHint = "Ctrl+Shift+S",
                Action = () => SafeFireAndForget(EditorPane.SaveActiveAsAsync(), "SaveActiveAs")
            },
            new CommandPaletteEntry
            {
                Id = "editor.saveAll",
                Title = "Save All Files",
                Category = "Editor",
                Keywords = new[] { "save", "write", "all", "persist" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.SaveAllAsync(), "SaveAll")
            },
            new CommandPaletteEntry
            {
                Id = "editor.nextTab",
                Title = "Next Tab",
                Category = "Editor",
                Keywords = new[] { "tab", "next", "navigate", "switch" },
                KeybindingHint = "Ctrl+Tab",
                Action = () => EditorPane.NextTab()
            },
            new CommandPaletteEntry
            {
                Id = "editor.previousTab",
                Title = "Previous Tab",
                Category = "Editor",
                Keywords = new[] { "tab", "previous", "navigate", "switch" },
                KeybindingHint = "Ctrl+Shift+Tab",
                Action = () => EditorPane.PreviousTab()
            },
            new CommandPaletteEntry
            {
                Id = "editor.acceptAllHunks",
                Title = "Accept All Diff Hunks",
                Category = "Editor",
                Keywords = new[] { "diff", "accept", "hunks", "apply", "changes" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.AcceptAllAsync(), "AcceptAll")
            },
            new CommandPaletteEntry
            {
                Id = "editor.rejectAllHunks",
                Title = "Reject All Diff Hunks",
                Category = "Editor",
                Keywords = new[] { "diff", "reject", "hunks", "discard", "changes" },
                KeybindingHint = "",
                Action = () => EditorPane.RejectAll()
            },
            new CommandPaletteEntry
            {
                Id = "editor.closeActiveTab",
                Title = "Close Current File",
                Category = "Editor",
                Keywords = new[] { "close", "tab", "file" },
                KeybindingHint = "Ctrl+W",
                Action = () => SafeFireAndForget(EditorPane.CloseActiveTabAsync(), "CloseActiveTab")
            }
        };

        registry.Register(entries);
    }

    /// <summary>
    /// Creates a command palette entry that navigates to the chat view and
    /// inserts the given slash command into the chat input — mirroring the
    /// behaviour of the in-chat slash-command popup.
    /// </summary>
    private CommandPaletteEntry CreateSlashCommandEntry(
        string id,
        string command,
        string description,
        ChatViewModel chat,
        params string[] keywords)
    {
        return new CommandPaletteEntry
        {
            Id = id,
            Title = $"Run {command}",
            Category = "Commands",
            Keywords = new[] { command[1..], description }
                .Concat(keywords)
                .ToArray(),
            KeybindingHint = command,
            Action = () =>
            {
                // Navigate to chat view first so the user sees the command.
                CurrentViewModel = chat;
                IsSettingsMode = false;
                IsCheckpointBrowserOpen = false;
                IsSessionHistoryOpen = false;
                StatusText = "Chat Mode";

                // Insert the slash command into the input, exactly as the
                // chat slash-command popup does.
                chat.InputText = command + " ";
            }
        };
    }

    private ChatViewModel GetOrCreateChatViewModel()
    {
        if (_chatViewModel == null)
        {
            _chatViewModel = _serviceProvider.GetRequiredService<ChatViewModel>();

            // Point the checkpoint browser (a singleton that otherwise
            // defaults to the literal session id "default") at this chat's
            // real session id, so checkpoints created while chatting are
            // actually findable there instead of the browser querying a
            // session id nothing was ever tagged with.
            if (CheckpointBrowser.SetSessionCommand.CanExecute(_chatViewModel.SessionId))
            {
                CheckpointBrowser.SetSessionCommand.Execute(_chatViewModel.SessionId);
            }

            // A fork moves the chat onto a new session id: keep the checkpoint browser pointed at it.
            _chatViewModel.SessionIdChanged += (_, _) =>
            {
                var id = _chatViewModel.SessionId;
                if (CheckpointBrowser.SetSessionCommand.CanExecute(id))
                    CheckpointBrowser.SetSessionCommand.Execute(id);
            };
        }
        return _chatViewModel;
    }

    /// <summary>
    /// Awaits a fire-and-forget task, logging any unhandled exceptions so they
    /// are not silently swallowed by the task scheduler.
    /// </summary>
    private static void SafeFireAndForget(Task task, string operation)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Fire-and-forget operation '{operation}' failed: {t.Exception.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);
    }
}
