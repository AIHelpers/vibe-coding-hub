using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.CommandPalette;
using AiCodeAgent.Core.Configuration;
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

    private ChatViewModel? _chatViewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkspaceIndexQueryService? _indexQueryService;
    private readonly ConfigurationService? _configurationService;
    private Window? _hostWindow;

    public ChatViewModel? ChatViewModel => _chatViewModel;
    public FileExplorerViewModel FileExplorer { get; }
    public TerminalViewModel Terminal { get; }
    public EditorPaneViewModel EditorPane { get; }
    public CheckpointBrowserViewModel CheckpointBrowser { get; }
    public SessionManagerViewModel SessionManager { get; }
    public CommandPaletteViewModel CommandPalette { get; }

    public MainViewModel(
        IServiceProvider serviceProvider,
        FileExplorerViewModel fileExplorer,
        TerminalViewModel terminal,
        EditorPaneViewModel editorPane,
        CheckpointBrowserViewModel checkpointBrowser,
        SessionManagerViewModel sessionManager,
        CommandPaletteViewModel commandPalette,
        WorkspaceIndexQueryService? indexQueryService = null)
    {
        _serviceProvider = serviceProvider;
        FileExplorer = fileExplorer;
        Terminal = terminal;
        EditorPane = editorPane;
        CheckpointBrowser = checkpointBrowser;
        SessionManager = sessionManager;
        CommandPalette = commandPalette;
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

        // Wire file explorer file-click to editor
        FileExplorer.PropertyChanged += OnFileExplorerPropertyChanged;

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
        FileExplorer.PropertyChanged -= OnFileExplorerPropertyChanged;
    }

    private void OnFileExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileExplorerViewModel.SelectedItem))
        {
            var selected = FileExplorer.SelectedItem;
            if (selected != null && !selected.IsDirectory)
            {
                SafeFireAndForget(EditorPane.OpenFileAsync(selected.FullPath), "OpenFile");
                IsCheckpointBrowserOpen = false;
                IsSessionHistoryOpen = false;
            }
        }
    }

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
    private void ToggleCheckpointBrowser()
    {
        IsCheckpointBrowserOpen = !IsCheckpointBrowserOpen;
        if (IsCheckpointBrowserOpen)
        {
            IsSettingsMode = false;
            CheckpointBrowser.RefreshCommand.Execute(null);
        }
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
                Keywords = new[] { "file", "open", "goto", "navigate", "index" },
                KeybindingHint = "Ctrl+P",
                Action = () => SafeFireAndForget(OpenIndexedFileAsync(), "OpenIndexedFile")
            },
            new CommandPaletteEntry
            {
                Id = "index.goToSymbol",
                Title = "Go to Symbol...",
                Category = "Workspace",
                Keywords = new[] { "symbol", "goto", "navigate", "index", "class", "method" },
                KeybindingHint = "Ctrl+Shift+O",
                Action = () => SafeFireAndForget(OpenIndexedSymbolAsync(), "OpenIndexedSymbol")
            },

            // ---- Editor ----
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
                Id = "editor.saveAll",
                Title = "Save All Files",
                Category = "Editor",
                Keywords = new[] { "save", "write", "all", "persist" },
                KeybindingHint = "",
                Action = () => SafeFireAndForget(EditorPane.SaveAllAsync(), "SaveAll")
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
                KeybindingHint = "",
                Action = () =>
                {
                    var tab = EditorPane.ActiveTab;
                    if (tab != null)
                    {
                        SafeFireAndForget(EditorPane.CloseTabAsync(tab), "CloseTab");
                    }
                }
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
    /// Opens a lightweight "Go to file" flow: queries the workspace index and
    /// opens the first matching file in the editor.
    /// </summary>
    private async Task OpenIndexedFileAsync()
    {
        if (_indexQueryService == null)
            return;

        try
        {
            var matches = await _indexQueryService.SearchFilesAsync(limit: 1);
            if (matches.Count > 0)
            {
                await EditorPane.OpenFileAsync(matches[0].Path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Go to file failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a lightweight "Go to symbol" flow: queries the workspace index for
    /// symbols and opens the first matching file at the symbol's line.
    /// </summary>
    private async Task OpenIndexedSymbolAsync()
    {
        if (_indexQueryService == null)
            return;

        try
        {
            var matches = await _indexQueryService.SearchSymbolsAsync(limit: 1);
            if (matches.Count > 0)
            {
                await EditorPane.OpenFileAsync(matches[0].FilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Go to symbol failed: {ex.Message}");
        }
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
