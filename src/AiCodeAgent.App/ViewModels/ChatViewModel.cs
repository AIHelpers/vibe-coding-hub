using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Indexing;
using System.Collections.Generic;
using System.IO;

namespace AiCodeAgent.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AgentService _agentService;
    private readonly IAgentEventBus _eventBus;
    private readonly EditorPaneViewModel _editorPane;
    private readonly SharedChangeset _changeset;
    private readonly WorkspaceIndexQueryService? _indexQueryService;
    private readonly SdlcPipelineRunner? _pipelineRunner;
    private readonly SdlcPipelineLoader? _pipelineLoader;
    private readonly ConfigurationService? _configurationService;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// The working directory the agent operates in. Falls back to the
    /// configured <see cref="AgentConfiguration.WorkingDirectory"/>, then to
    /// <see cref="Directory.GetCurrentDirectory"/> if unset.
    /// </summary>
    public string WorkingDirectory
    {
        get
        {
            var configured = _configurationService?.Config.Agent?.WorkingDirectory;
            return !string.IsNullOrWhiteSpace(configured)
                ? configured!
                : Directory.GetCurrentDirectory();
        }
    }

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _isCancellable;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _modelName = "";

    [ObservableProperty]
    private string _tokenUsage = "";

    [ObservableProperty]
    private string _permissionMode = "Ask";

    [ObservableProperty]
    private bool _showApprovalDialog;

    [ObservableProperty]
    private string _approvalToolName = "";

    [ObservableProperty]
    private string _approvalArgs = "";

    [ObservableProperty]
    private string _approvalAgent = "";

    [ObservableProperty]
    private string _approvalRole = "";

    // Agent session sidebar
    [ObservableProperty]
    private bool _isAgentSidebarOpen = true;

    // @-mention system
    [ObservableProperty]
    private bool _showMentionPopup;

    [ObservableProperty]
    private string _mentionFilter = "";

    [ObservableProperty]
    private int _mentionCursorPosition;

    // Slash commands
    [ObservableProperty]
    private bool _showSlashCommands;

    [ObservableProperty]
    private string _slashFilter = "";

    // Token counter
    [ObservableProperty]
    private string _tokenCount = "~0 tokens";

    // Model selector
    [ObservableProperty]
    private string _selectedModel = "Auto";

    [ObservableProperty]
    private string _selectedPipeline = "full-sdlc";

    [ObservableProperty]
    private bool _isPipelineRunning;

    private TaskCompletionSource<bool>? _pendingApproval;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ToolCallCardViewModel> ToolCallCards { get; } = new();
    public ObservableCollection<MentionItem> MentionItems { get; } = new();
    public ObservableCollection<SlashCommandItem> SlashCommandItems { get; } = new();
    public ObservableCollection<AgentSessionItem> AgentSessions { get; } = new();
    public ObservableCollection<string> AvailablePipelines { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new()
    {
        "Auto", "Fast", "Smart"
    };

    public List<string> KnownSlashCommands { get; } = new()
    {
        "/edit", "/search", "/explain", "/test", "/fix", "/refactor", "/help"
    };

    public ChatViewModel(
        AgentService agentService,
        EditorPaneViewModel editorPane,
        SharedChangeset changeset,
        WorkspaceIndexQueryService? indexQueryService = null,
        SdlcPipelineRunner? pipelineRunner = null,
        SdlcPipelineLoader? pipelineLoader = null,
        ConfigurationService? configurationService = null)
    {
        _agentService = agentService;
        _eventBus = agentService.EventBus;
        _editorPane = editorPane;
        _changeset = changeset;
        _indexQueryService = indexQueryService;
        _pipelineRunner = pipelineRunner;
        _pipelineLoader = pipelineLoader;
        _configurationService = configurationService;

        if (_pipelineLoader != null)
        {
            foreach (var pipeline in _pipelineLoader.GetAllPipelines())
                AvailablePipelines.Add(pipeline.Name);
            if (AvailablePipelines.Count > 0 && !AvailablePipelines.Contains(SelectedPipeline))
                SelectedPipeline = AvailablePipelines[0];
        }

        // Initialize slash commands
        SlashCommandItems.Add(new SlashCommandItem { Name = "/edit", Description = "Edit a specific file", Icon = "✏️" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/search", Description = "Search the codebase", Icon = "🔍" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/explain", Description = "Explain code logic", Icon = "💡" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/test", Description = "Generate tests", Icon = "🧪" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/fix", Description = "Fix issues in code", Icon = "🔧" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/refactor", Description = "Refactor code", Icon = "🔄" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/help", Description = "Show available commands", Icon = "❓" });

        // Add welcome message
        Messages.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = "Hello! I'm your AI Code Assistant. I can help you read, write, and edit files, " +
                      "search code, run commands, work with git, and more.\n\n" +
                      "**Features:**\n" +
                      "- 🎯 Streaming responses in real-time\n" +
                      "- 🔒 Permission modes (Ask / AutoEdit / FullAuto / Plan)\n" +
                      "- 📝 Diff-based editing with checkpoints\n" +
                      "- ⚡ Cancel anytime with Esc or the Cancel button\n" +
                      "- 📁 File Explorer sidebar (click folder icon to toggle)\n" +
                      "- 💻 Integrated Terminal (bottom pane)\n" +
                      "- @-mention files to add context\n" +
                      "- /slash commands for quick actions",
            Timestamp = DateTime.Now
        });
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsProcessing)
            return;

        var userMessage = InputText.Trim();
        InputText = string.Empty;
        ShowMentionPopup = false;
        ShowSlashCommands = false;

        // Add user message
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = userMessage,
            Timestamp = DateTime.Now
        });

        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Processing...";

        // Create assistant message placeholder
        var assistantMessage = new ChatMessage
        {
            Role = "Assistant",
            Content = string.Empty,
            Timestamp = DateTime.Now
        };
        Messages.Add(assistantMessage);

        // Clear previous tool call cards
        ToolCallCards.Clear();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        try
        {
            // Start listening for events
            _ = Task.Run(() => ProcessEventsAsync(assistantMessage, token), token);

            // Start streaming
            await _agentService.StreamMessageAsync(
                userMessage,
                "default",
                new AgentOptions
                {
                    PermissionMode = ParsePermissionMode(PermissionMode),
                    WorkingDirectory = WorkingDirectory
                },
                token);
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Content += "\n\n*Cancelled*";
        }
        catch (Exception ex)
        {
            assistantMessage.Content += $"\n\n**Error:** {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            IsCancellable = false;
            StatusText = "Ready";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            // Ensure the assistant message has content
            if (string.IsNullOrEmpty(assistantMessage.Content))
            {
                assistantMessage.Content = "*(No response generated)*";
            }
        }
    }

    /// <summary>
    /// Runs the selected SDLC pipeline (e.g. full-sdlc: analyze -> implement -> review -> test -> deploy)
    /// against the current input text as a multi-agent session. Reuses the same event bus as SendAsync,
    /// so pipeline stages stream into the chat transcript and the AgentSessions sidebar exactly like a
    /// regular multi-agent turn.
    /// </summary>
    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsProcessing || _pipelineRunner == null || _pipelineLoader == null)
            return;

        var pipeline = _pipelineLoader.GetPipeline(SelectedPipeline);
        if (pipeline == null)
        {
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = $"*Unknown pipeline '{SelectedPipeline}'.*",
                Timestamp = DateTime.Now
            });
            return;
        }

        var task = InputText.Trim();
        InputText = string.Empty;
        ShowMentionPopup = false;
        ShowSlashCommands = false;

        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = $"**Run pipeline: {pipeline.Name}**\n{task}",
            Timestamp = DateTime.Now
        });

        IsProcessing = true;
        IsPipelineRunning = true;
        IsCancellable = true;
        StatusText = $"Running pipeline '{pipeline.Name}'...";

        var assistantMessage = new ChatMessage
        {
            Role = "Assistant",
            Content = string.Empty,
            Timestamp = DateTime.Now
        };
        Messages.Add(assistantMessage);

        ToolCallCards.Clear();
        AgentSessions.Clear();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        var pipelineSessionId = Guid.NewGuid().ToString();

        try
        {
            // Start listening for events (the coordinator publishes onto the same shared event bus).
            _ = Task.Run(() => ProcessEventsAsync(assistantMessage, token), token);

            // Drive the pipeline to completion; events are consumed via the event bus subscription above.
            await foreach (var _ in _pipelineRunner.RunAsync(
                pipeline,
                task,
                pipelineSessionId,
                WorkingDirectory,
                cancellationToken: token))
            {
                // no-op: ProcessEventsAsync handles rendering
            }
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Content += "\n\n*Cancelled*";
        }
        catch (Exception ex)
        {
            assistantMessage.Content += $"\n\n**Error:** {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            IsPipelineRunning = false;
            IsCancellable = false;
            StatusText = "Ready";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            if (string.IsNullOrEmpty(assistantMessage.Content))
            {
                assistantMessage.Content = "*(No response generated)*";
            }
        }
    }

    private async Task ProcessEventsAsync(ChatMessage assistantMessage, CancellationToken token)
    {
        try
        {
            await foreach (var evt in _eventBus.GetEventsAsync(token))
            {
                switch (evt)
                {
                    case TextDeltaEvent delta:
                        assistantMessage.Content += delta.Delta;
                        break;

                    case ToolCallStartEvent start:
                        var card = new ToolCallCardViewModel
                        {
                            ToolName = start.Call.Name,
                            Arguments = FormatArguments(start.Call.Arguments),
                            IsExpanded = false
                        };
                        ToolCallCards.Add(card);
                        break;

                    case ToolCallEndEvent end:
                        var existingCard = ToolCallCards.FirstOrDefault(c => c.ToolName == end.Call.Name);
                        if (existingCard != null)
                        {
                            existingCard.Output = TruncateOutput(end.Result.Content, 500);
                            existingCard.Duration = end.Duration;
                            existingCard.IsError = end.Result.IsError;
                        }
                        break;

                    case ApprovalRequestEvent approval:
                        ShowApprovalDialog = true;
                        ApprovalToolName = approval.Call.Name;
                        ApprovalArgs = FormatArguments(approval.Call.Arguments);
                        _pendingApproval = approval.Approval;
                        break;

                    case DiffProducedEvent diffProduced:
                        // Parse diff hunks and add to the shared changeset
                        var hunks = DiffParser.ParseSimpleDiff(
                            diffProduced.Diff.DiffText,
                            diffProduced.Diff.FilePath,
                            diffProduced.Diff.AgentId);
                        if (hunks.Length > 0)
                        {
                            _changeset.AddHunks(hunks);
                            _ = _editorPane.OpenFileAsync(diffProduced.Diff.FilePath).ContinueWith(
                                _ => _editorPane.NotifyHunksChanged(),
                                TaskScheduler.FromCurrentSynchronizationContext());
                        }
                        break;

                    case AgentTaggedEvent tagged:
                        // Handle agent-tagged events from multi-agent sessions
                        HandleAgentTaggedEvent(tagged, assistantMessage);
                        break;

                    case StatusUpdateEvent status:
                        StatusText = status.Status;
                        break;

                    case TokenUsageEvent usage:
                        TokenUsage = $"Tokens: {usage.Usage.TotalTokens}";
                        break;

                    case AgentFinishedEvent finished:
                        if (finished.Response.WasCancelled)
                        {
                            assistantMessage.Content += "\n\n*Cancelled*";
                        }
                        StatusText = "Done";
                        break;

                    case AgentErrorEvent error:
                        assistantMessage.Content += $"\n\n**Error:** {error.Error.Message}";
                        StatusText = "Error";
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        _agentService.Cancel();
        IsCancellable = false;
        StatusText = "Cancelling...";
    }

    [RelayCommand]
    private void ApproveTool()
    {
        _pendingApproval?.TrySetResult(true);
        _pendingApproval = null;
        ShowApprovalDialog = false;

        if (!string.IsNullOrEmpty(ApprovalToolName))
        {
            ToolCallCards.Add(new ToolCallCardViewModel
            {
                ToolName = ApprovalToolName,
                Arguments = ApprovalArgs,
                IsExpanded = true
            });
        }
    }

    [RelayCommand]
    private void DenyTool()
    {
        _pendingApproval?.TrySetResult(false);
        _pendingApproval = null;
        ShowApprovalDialog = false;
    }

    [RelayCommand]
    private void SetPermissionMode(string mode)
    {
        PermissionMode = mode;
    }

    [RelayCommand]
    private void ToggleAgentSidebar()
    {
        IsAgentSidebarOpen = !IsAgentSidebarOpen;
    }

    private void HandleAgentTaggedEvent(AgentTaggedEvent tagged, ChatMessage assistantMessage)
    {
        // Track active agents in the sidebar
        var existing = AgentSessions.FirstOrDefault(a => a.AgentId == tagged.AgentId);
        if (existing == null)
        {
            existing = new AgentSessionItem
            {
                AgentId = tagged.AgentId,
                Role = tagged.Role ?? "agent",
                Status = "Active"
            };
            AgentSessions.Add(existing);
        }
        else
        {
            existing.Role = tagged.Role ?? existing.Role;
            existing.Status = "Active";
        }

        // Handle inner event
        switch (tagged.Inner)
        {
            case TextDeltaEvent delta:
                assistantMessage.Content += delta.Delta;
                break;

            case ToolCallStartEvent start:
                var card = new ToolCallCardViewModel
                {
                    ToolName = start.Call.Name,
                    Arguments = FormatArguments(start.Call.Arguments),
                    IsExpanded = false,
                    AgentId = tagged.AgentId,
                    AgentRole = tagged.Role
                };
                ToolCallCards.Add(card);
                break;

            case ToolCallEndEvent end:
                var existingCard = ToolCallCards.FirstOrDefault(c => c.ToolName == end.Call.Name && c.AgentId == tagged.AgentId);
                if (existingCard != null)
                {
                    existingCard.Output = TruncateOutput(end.Result.Content, 500);
                    existingCard.Duration = end.Duration;
                    existingCard.IsError = end.Result.IsError;
                }
                break;

            case ApprovalRequestEvent approval:
                ShowApprovalDialog = true;
                ApprovalToolName = approval.Call.Name;
                ApprovalArgs = FormatArguments(approval.Call.Arguments);
                ApprovalAgent = tagged.AgentId;
                ApprovalRole = tagged.Role ?? "";
                _pendingApproval = approval.Approval;
                break;

            case StatusUpdateEvent status:
                StatusText = $"[{tagged.AgentId}] {status.Status}";
                break;

            case TokenUsageEvent usage:
                TokenUsage = $"Tokens: {usage.Usage.TotalTokens}";
                break;

            case AgentFinishedEvent finished:
                if (finished.Response.WasCancelled)
                {
                    assistantMessage.Content += "\n\n*Cancelled*";
                }
                if (existing != null)
                {
                    existing.Status = "Done";
                }
                StatusText = "Done";
                break;

            case AgentErrorEvent error:
                assistantMessage.Content += $"\n\n**Error ({tagged.AgentId}):** {error.Error.Message}";
                if (existing != null)
                {
                    existing.Status = "Error";
                }
                StatusText = "Error";
                break;
        }
    }

    // @-mention logic
    public void OnTextChanged(string text, int cursorPosition)
    {
        UpdateTokenCount(text);

        // Check for @-mention trigger
        if (cursorPosition > 0 && text.Length > 0)
        {
            var textBeforeCursor = text[..Math.Min(cursorPosition, text.Length)];
            var atIndex = textBeforeCursor.LastIndexOf('@');

            if (atIndex >= 0 && (atIndex == 0 || textBeforeCursor[atIndex - 1] == ' '))
            {
                var filter = textBeforeCursor[(atIndex + 1)..];
                if (!filter.Contains(' ') && !string.IsNullOrEmpty(filter))
                {
                    ShowMentionPopup = true;
                    MentionFilter = filter;
                    MentionCursorPosition = cursorPosition;
                    FilterMentions(filter);
                }
                else if (!filter.Contains(' ') && string.IsNullOrEmpty(filter))
                {
                    ShowMentionPopup = true;
                    MentionFilter = "";
                    ShowAllMentions();
                }
                else
                {
                    ShowMentionPopup = false;
                }
            }
            else
            {
                ShowMentionPopup = false;
            }

            // Check for slash command trigger
            if (textBeforeCursor.Length == 1 && textBeforeCursor == "/")
            {
                ShowSlashCommands = true;
                SlashFilter = "";
                ShowAllSlashCommands();
            }
            else if (textBeforeCursor.StartsWith("/") && !textBeforeCursor.Contains(' '))
            {
                ShowSlashCommands = true;
                SlashFilter = textBeforeCursor[1..];
                FilterSlashCommands(textBeforeCursor[1..]);
            }
            else
            {
                ShowSlashCommands = false;
            }
        }
        else
        {
            if (text.StartsWith("/"))
            {
                ShowSlashCommands = true;
                SlashFilter = text.Length > 1 ? text[1..] : "";
                FilterSlashCommands(SlashFilter);
            }
            else
            {
                ShowSlashCommands = false;
            }
            ShowMentionPopup = false;
        }
    }

    private void UpdateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            TokenCount = "~0 tokens";
            return;
        }

        var estimatedTokens = text.Length / 4;
        TokenCount = estimatedTokens switch
        {
            < 1000 => $"~{estimatedTokens} tokens",
            < 1000000 => $"~{estimatedTokens / 1000.0:F1}k tokens",
            _ => $"~{estimatedTokens / 1000000.0:F1}M tokens"
        };
    }

    public void InsertMention(MentionItem item)
    {
        if (string.IsNullOrEmpty(InputText)) return;

        var textBeforeCursor = InputText[..Math.Min(MentionCursorPosition, InputText.Length)];
        var atIndex = textBeforeCursor.LastIndexOf('@');

        if (atIndex >= 0)
        {
            var afterMention = MentionCursorPosition < InputText.Length
                ? InputText[MentionCursorPosition..]
                : string.Empty;

            InputText = InputText[..atIndex] + $"@{item.FilePath} " + afterMention;
        }

        ShowMentionPopup = false;
    }

    public void InsertSlashCommand(SlashCommandItem item)
    {
        InputText = item.Name + " ";
        ShowSlashCommands = false;
    }

    private void FilterMentions(string filter)
    {
        if (_indexQueryService != null)
        {
            _ = PopulateMentionsFromIndexAsync(filter);
            return;
        }

        var files = GetProjectFiles();
        MentionItems.Clear();

        foreach (var file in files.Where(f =>
            Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            f.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            MentionItems.Add(new MentionItem
            {
                FilePath = file,
                FileName = Path.GetFileName(file),
                Icon = GetFileIcon(file)
            });
        }
    }

    private void ShowAllMentions()
    {
        if (_indexQueryService != null)
        {
            _ = PopulateMentionsFromIndexAsync(string.Empty);
            return;
        }

        var files = GetProjectFiles();
        MentionItems.Clear();

        foreach (var file in files.Take(20))
        {
            MentionItems.Add(new MentionItem
            {
                FilePath = file,
                FileName = Path.GetFileName(file),
                Icon = GetFileIcon(file)
            });
        }
    }

    private async Task PopulateMentionsFromIndexAsync(string filter)
    {
        try
        {
            var matches = await _indexQueryService!.SearchFilesAsync(
                filter: filter,
                limit: 25).ConfigureAwait(true);

            MentionItems.Clear();
            foreach (var match in matches)
            {
                MentionItems.Add(new MentionItem
                {
                    FilePath = match.Path,
                    FileName = match.FileName,
                    Icon = GetFileIcon(match.Path)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Index mention query failed: {ex.Message}");
        }
    }

    private void FilterSlashCommands(string filter)
    {
        SlashCommandItems.Clear();

        foreach (var cmd in KnownSlashCommands.Where(c =>
            string.IsNullOrEmpty(filter) || c.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            var desc = cmd switch
            {
                "/edit" => "Edit a specific file",
                "/search" => "Search the codebase",
                "/explain" => "Explain code logic",
                "/test" => "Generate tests",
                "/fix" => "Fix issues in code",
                "/refactor" => "Refactor code",
                "/help" => "Show available commands",
                _ => ""
            };
            var icon = cmd switch
            {
                "/edit" => "✏️",
                "/search" => "🔍",
                "/explain" => "💡",
                "/test" => "🧪",
                "/fix" => "🔧",
                "/refactor" => "🔄",
                "/help" => "❓",
                _ => "📋"
            };

            SlashCommandItems.Add(new SlashCommandItem { Name = cmd, Description = desc, Icon = icon });
        }
    }

    private void ShowAllSlashCommands()
    {
        SlashCommandItems.Clear();
        foreach (var cmd in KnownSlashCommands)
        {
            var desc = cmd switch
            {
                "/edit" => "Edit a specific file",
                "/search" => "Search the codebase",
                "/explain" => "Explain code logic",
                "/test" => "Generate tests",
                "/fix" => "Fix issues in code",
                "/refactor" => "Refactor code",
                "/help" => "Show available commands",
                _ => ""
            };
            var icon = cmd switch
            {
                "/edit" => "✏️",
                "/search" => "🔍",
                "/explain" => "💡",
                "/test" => "🧪",
                "/fix" => "🔧",
                "/refactor" => "🔄",
                "/help" => "❓",
                _ => "📋"
            };

            SlashCommandItems.Add(new SlashCommandItem { Name = cmd, Description = desc, Icon = icon });
        }
    }

    private List<string> GetProjectFiles()
    {
        var files = new List<string>();
        var rootDir = WorkingDirectory;

        try
        {
            var searchDirs = new[] { "src", "tests" };
            foreach (var dir in searchDirs)
            {
                var fullPath = Path.Combine(rootDir, dir);
                if (Directory.Exists(fullPath))
                {
                    files.AddRange(Directory.GetFiles(fullPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).StartsWith('.'))
                        .Take(100));
                }
            }

            files.AddRange(Directory.GetFiles(rootDir)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Take(20));
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return files;
    }

    private static string GetFileIcon(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "🔷",
            ".xaml" or ".axaml" => "🟦",
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "📋",
            ".md" or ".txt" => "📝",
            ".csproj" or ".sln" or ".slnx" => "📦",
            ".js" or ".ts" or ".jsx" or ".tsx" => "🟨",
            ".py" => "🐍",
            ".html" or ".css" or ".scss" => "🌐",
            _ => "📄"
        };
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        ToolCallCards.Clear();
        Messages.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = "Chat cleared. How can I help you?",
            Timestamp = DateTime.Now
        });
    }

    public void ToggleToolCardExpand(ToolCallCardViewModel card)
    {
        card.IsExpanded = !card.IsExpanded;
    }

    private static string FormatArguments(Dictionary<string, object?> args)
    {
        if (args == null || args.Count == 0) return "{}";
        return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string TruncateOutput(string output, int maxLength)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= maxLength)
            return output;
        return output[..maxLength] + "\n...[truncated]";
    }

    private static Core.Models.PermissionMode ParsePermissionMode(string mode) => mode switch
    {
        "AutoEdit" => Core.Models.PermissionMode.AutoEdit,
        "FullAuto" => Core.Models.PermissionMode.FullAuto,
        "Plan" => Core.Models.PermissionMode.Plan,
        _ => Core.Models.PermissionMode.Ask
    };
}

public class ChatMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class ToolCallCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string? _output;

    [ObservableProperty]
    private TimeSpan? _duration;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private DateTime _timestamp = DateTime.UtcNow;

    [ObservableProperty]
    private bool _needsApproval;

    [ObservableProperty]
    private string? _approvalId;

    [ObservableProperty]
    private string? _agentId;

    [ObservableProperty]
    private string? _agentRole;

    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

public partial class AgentSessionItem : ObservableObject
{
    [ObservableProperty]
    private string _agentId = string.Empty;

    [ObservableProperty]
    private string _role = string.Empty;

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private string _permissionMode = "Ask";
}

public class MentionItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Icon { get; set; } = "📄";
}

public class SlashCommandItem
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "📋";
}