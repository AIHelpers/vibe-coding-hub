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
    public BackgroundTaskManagerViewModel BackgroundTasks { get; }

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
        WorkspaceIndexQueryService? indexQueryService = null)
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
        BackgroundTasks = backgroundTasks;
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

        // Default to Chat view (reuse the same instance, don't create a new one)
        _currentViewModel ??= GetOrCreateChatViewModel();

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
    }

    /// <summary>
    /// Opens a file chosen in Explorer. Preview tabs are reused on single-click;
    /// a non-preview open pins the tab (double-click / context menu Open).
    /// </summary>
    public async Task OpenExplorerItemAsync(FileExplorerItem? item, bool isPreview)
    {
        if (item == null || !item.CanOpen)
            return;

        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        await EditorPane.OpenFileAsync(item.FullPath, isPreview: isPreview);
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

    [RelayCommand]
    private void NavigateToSettings()
    {
        var settings = _serviceProvider.GetRequiredService<SettingsViewModel>();
        // Wire up the host window's storage provider so the folder picker
        // button works (DI registers SettingsViewModel with a null provider).
        if (_hostWindow != null && settings.StorageProvider == null)
        {
            settings.StorageProvider = _hostWindow.StorageProvider;
        }
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
        IsFileExplorerOpen = !IsFileExplorerOpen;
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
        IsCheckpointBrowserOpen = !IsCheckpointBrowserOpen;
        if (IsCheckpointBrowserOpen)
        {
            IsSettingsMode = false;
            DiffViewer.Close();
            ProjectKnowledge.Close();
            CheckpointBrowser.RefreshCommand.Execute(null);
        }
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

        IsSettingsMode = false;
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        IsSessionDashboardOpen = false;
        DiffViewer.Close();
        await ProjectKnowledge.OpenAsync(WorkingDirectory);
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

        IsSettingsMode = false;
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        IsSessionDashboardOpen = false;
        DiffViewer.Close();
        ProjectKnowledge.Close();
        BackgroundTasks.WorkingDirectory = WorkingDirectory;
        BackgroundTasks.IsVisible = true;
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
        IsSettingsMode = false;
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        IsSessionDashboardOpen = false;
        ProjectKnowledge.Close();
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

        IsSettingsMode = false;
        IsCheckpointBrowserOpen = false;
        IsSessionHistoryOpen = false;
        IsSessionDashboardOpen = false;
        ProjectKnowledge.Close();
        DiffViewer.Load(latest.FilePath, latest.OriginalContent, $"Checkpoint @ {latest.Timestamp:t}", latest.CheckpointId);
    }

    [RelayCommand]
    private void ToggleSessionHistory()
    {
        IsSessionHistoryOpen = !IsSessionHistoryOpen;
        if (IsSessionHistoryOpen)
        {
            IsSettingsMode = false;
            IsCheckpointBrowserOpen = false;
            SessionManager.RefreshSessionHistoryCommand.Execute(null);
        }
        else
        {
            SessionManager.CloseSessionHistory();
        }
    }

    [RelayCommand]
    private void ToggleSessionDashboard()
    {
        IsSessionDashboardOpen = !IsSessionDashboardOpen;
        if (IsSessionDashboardOpen)
        {
            IsSettingsMode = false;
            IsCheckpointBrowserOpen = false;
            IsSessionHistoryOpen = false;
            SessionDashboard.RefreshCommand.Execute(null);
        }
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
                Title = "Project Knowledge (AGENTS.md + Skills)",
                Category = "Navigation",
                Keywords = new[] { "agents.md", "agent.md", "aiagent.md", "memory", "skills", "skill.md", "doctor", "init" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(ToggleProjectKnowledgeAsync(), "ToggleProjectKnowledge")
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
