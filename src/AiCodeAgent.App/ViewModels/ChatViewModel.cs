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
using AiCodeAgent.Indexing.Semantic;
using AiCodeAgent.Tools.Git;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    private readonly Retriever? _retriever;
    private readonly SemanticIndex? _semanticIndex;
    private readonly IPlanGenerator? _planGenerator;
    private readonly IAutonomousAgentRunner? _autonomousRunner;
    // Feature 6: In-Chat Branch / PR Workflow
    private readonly GitService? _gitService;
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
    // Granular rights toggles (independent of PermissionMode)
    [ObservableProperty]
    private bool _allowRead = true;
    [ObservableProperty]
    private bool _allowEdit;
    [ObservableProperty]
    private bool _allowExecute;
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
    [ObservableProperty]
    private string _approvalRisk = "Write";
    // Agent session sidebar
    [ObservableProperty]
    private bool _isAgentSidebarOpen = true;
    // Autonomous plan panel (Feature 4)
    public PlanViewModel PlanVM { get; } = new();
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
    // Feature 8: Conversational Requirements Clarification
    private readonly IRequirementsClarifier? _requirementsClarifier;
    private TaskCompletionSource<string?>? _pendingClarificationAnswer;
    [ObservableProperty]
    private bool _isAwaitingClarification;
    [ObservableProperty]
    private AppRequirements? _currentRequirements;
    [ObservableProperty]
    private string _currentClarificationQuestion = "";
    [ObservableProperty]
    private string _currentClarificationDimension = "";
    public ObservableCollection<RequirementGap> ClarificationQuestions { get; } = new();
    /// <summary>
    /// Set by <see cref="ProcessEventsAsync"/> when it has finished draining the
    /// terminal event (<see cref="AgentFinishedEvent"/> or
    /// <see cref="AgentErrorEvent"/>) for the current turn. Awaited by
    /// <see cref="SendAsync"/>/<see cref="RunPipelineAsync"/> before the
    /// "*(No response generated)*" fallback so the check doesn't race ahead of
    /// in-flight UI-thread event dispatch.
    /// </summary>
    private TaskCompletionSource? _eventProcessingComplete;
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
        "/edit", "/search", "/explain", "/test", "/fix", "/refactor", "/plan", "/help",
        // Feature 6: In-Chat Branch / PR Workflow
        "/branch", "/commit", "/pr"
    };
    public ChatViewModel(
        AgentService agentService,
        EditorPaneViewModel editorPane,
        SharedChangeset changeset,
        WorkspaceIndexQueryService? indexQueryService = null,
        SdlcPipelineRunner? pipelineRunner = null,
        SdlcPipelineLoader? pipelineLoader = null,
        ConfigurationService? configurationService = null,
        Retriever? retriever = null,
        SemanticIndex? semanticIndex = null,
        IPlanGenerator? planGenerator = null,
        IAutonomousAgentRunner? autonomousRunner = null,
        GitService? gitService = null,
        IRequirementsClarifier? requirementsClarifier = null)
    {
        _agentService = agentService;
        _eventBus = agentService.EventBus;
        _editorPane = editorPane;
        _changeset = changeset;
        _indexQueryService = indexQueryService;
        _pipelineRunner = pipelineRunner;
        _pipelineLoader = pipelineLoader;
        _configurationService = configurationService;
        _retriever = retriever;
        _semanticIndex = semanticIndex;
        _planGenerator = planGenerator;
        _autonomousRunner = autonomousRunner;
        _gitService = gitService;
        _requirementsClarifier = requirementsClarifier;
        if (_pipelineLoader != null)
        {
            foreach (var pipeline in _pipelineLoader.GetAllPipelines())
                AvailablePipelines.Add(pipeline.Name);
            if (AvailablePipelines.Count > 0 && !AvailablePipelines.Contains(SelectedPipeline))
                SelectedPipeline = AvailablePipelines[0];
        }
        // Restore the last selected model from persisted UI settings so the
        // user's choice survives application restarts. Fall back to "Auto"
        // when unset or no longer in the available list.
        var savedModel = _configurationService?.Config.Ui?.SelectedModel;
        if (!string.IsNullOrWhiteSpace(savedModel))
        {
            SelectedModel = AvailableModels.Contains(savedModel)
                ? savedModel
                : savedModel; // keep even if not in the static list (may be a provider model id)
        }
        // Initialize slash commands
        SlashCommandItems.Add(new SlashCommandItem { Name = "/edit", Description = "Edit a specific file", Icon = "вњЏпёЏ" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/search", Description = "Search the codebase", Icon = "рџ”Ќ" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/explain", Description = "Explain code logic", Icon = "рџ’Ў" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/test", Description = "Generate tests", Icon = "рџ§Є" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/fix", Description = "Fix issues in code", Icon = "рџ”§" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/refactor", Description = "Refactor code", Icon = "рџ”„" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/help", Description = "Show available commands", Icon = "вќ“" });
        // Add welcome message
        Messages.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = "Hello! I'm your AI Code Assistant. I can help you read, write, and edit files, " +
                      "search code, run commands, work with git, and more.\n\n" +
                      "**Features:**\n" +
                      "- рџЋЇ Streaming responses in real-time\n" +
                      "- рџ”’ Permission modes (Ask / AutoEdit / FullAuto / Plan)\n" +
                      "- рџ“ќ Diff-based editing with checkpoints\n" +
                      "- вљЎ Cancel anytime with Esc or the Cancel button\n" +
                      "- рџ“Ѓ File Explorer sidebar (click folder icon to toggle)\n" +
                      "- рџ’» Integrated Terminal (bottom pane)\n" +
                      "- @-mention files to add context\n" +
                      "- /slash commands for quick actions",
            Timestamp = DateTime.Now
        });
    }
    /// <summary>
    /// Accepts a freehand visual annotation (Feature 9) and forwards it to the
    /// agent as a chat instruction. The annotation's vector strokes are
    /// serialized to SVG and a human-readable description, and the user's note
    /// is prepended so the agent understands the requested change.
    /// </summary>
    public async Task SendAnnotationAsync(AnnotationMessage annotation)
    {
        if (annotation == null || IsProcessing)
            return;

        var note = string.IsNullOrWhiteSpace(annotation.Note) ? "" : annotation.Note.Trim();
        var description = annotation.ToDescription();
        var svg = annotation.ToSvg();
        var bounds = annotation.Bounds;
        var hasScreenshot = !string.IsNullOrEmpty(annotation.CroppedPreviewBase64);
        var content = $"[Visual Annotation] {note}\n\n" +
                      $"Annotated region: ({bounds.X:F0},{bounds.Y:F0}) " +
                      $"{bounds.Width:F0}×{bounds.Height:F0}px\n" +
                      $"Strokes: {description}\n\n" +
                      $"SVG:\n{svg}\n\n" +
                      (hasScreenshot
                          ? "An image of the annotated region is attached — use it to see exactly what the " +
                            "user marked up, then identify the target element and make the requested change."
                          : "Please identify the target element in the annotated region and make the requested change.");

        // Attach the actual screenshot pixels (not just the vector/SVG
        // description) so vision-capable models can see what the user is
        // pointing at, the same way Cursor/Claude Code/Antigravity feed
        // screenshots back to the model.
        var annotationImages = hasScreenshot
            ? new List<ImageAttachment>
              {
                  new()
                  {
                      MediaType = annotation.CroppedPreviewMimeType,
                      Base64Data = annotation.CroppedPreviewBase64!,
                      SourceDescription = "Preview pane annotation"
                  }
              }
            : null;

        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = content,
            Timestamp = DateTime.Now
        });

        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Processing annotation...";

        var assistantMessage = new ChatMessage
        {
            Role = "Assistant",
            Content = string.Empty,
            Timestamp = DateTime.Now
        };
        Messages.Add(assistantMessage);
        ToolCallCards.Clear();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processingTask = ProcessEventsAsync(assistantMessage, token);
        try
        {
            await _agentService.StreamMessageAsync(
                content,
                "default",
                new AgentOptions
                {
                    PermissionMode = ParsePermissionMode(PermissionMode),
                    WorkingDirectory = WorkingDirectory,
                    Images = annotationImages,
                    Rights = new GranularRights
                    {
                        AllowRead = AllowRead,
                        AllowEdit = AllowEdit,
                        AllowExecute = AllowExecute
                    }
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
            _eventProcessingComplete?.TrySetResult();
            try
            {
                await processingTask;
            }
            catch (OperationCanceledException)
            {
                // Processing loop cancelled; no-op.
            }
            catch (Exception ex)
            {
                assistantMessage.Content += $"\n\n**Error:** {ex.Message}";
            }
        }

        IsProcessing = false;
        IsCancellable = false;
        StatusText = "Ready";
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        if (string.IsNullOrEmpty(assistantMessage.Content))
        {
            assistantMessage.Content = "*(No response generated)*";
        }
    }

    /// <summary>
    /// Persists the user's model selection to the UI configuration so it is
    /// restored on the next application launch. Fire-and-forget: failures are
    /// logged to the debug output and never crash the UI.
    /// </summary>
    partial void OnSelectedModelChanged(string value)
    {
        if (_configurationService == null || string.IsNullOrEmpty(value))
            return;
        _configurationService.Config.Ui ??= new UiConfiguration();
        _configurationService.Config.Ui.SelectedModel = value;
        _ = _configurationService.SaveAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to persist SelectedModel: {t.Exception.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);
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
        // Intercept @codebase <question> for whole-codebase semantic Q&A.
        if (TryParseCodebaseCommand(userMessage, out var codebaseQuestion))
        {
            await SendCodebaseQueryAsync(codebaseQuestion).ConfigureAwait(true);
            return;
        }
        // Intercept /plan <task> for the autonomous multi-file agent (Feature 4).
        if (userMessage.StartsWith("/plan ", StringComparison.OrdinalIgnoreCase))
        {
            await RunPlanAsync(userMessage.Substring(6).Trim()).ConfigureAwait(true);
            return;
        }
        // Intercept /edit <file_path> to open a file in the editor pane (VS Code-style).
        if (userMessage.StartsWith("/edit", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEditCommandAsync(userMessage).ConfigureAwait(true);
            return;
        }
        // Feature 6: In-Chat Branch / PR Workflow — intercept git slash commands.
        if (await TryHandleGitCommandAsync(userMessage).ConfigureAwait(true))
            return;
        // Feature 8: If awaiting a clarification answer, route the input to the
        // pending TaskCompletionSource instead of starting a new agent turn.
        if (IsAwaitingClarification && _pendingClarificationAnswer != null)
        {
            var answer = userMessage;
            _pendingClarificationAnswer.TrySetResult(answer);
            _pendingClarificationAnswer = null;
            IsAwaitingClarification = false;
            CurrentClarificationQuestion = "";
            CurrentClarificationDimension = "";
            return;
        }
        // Feature 8: Conversational Requirements Clarification — run the loop
        // before kicking off the orchestrator when the prompt looks vague and
        // a requirements clarifier is available.
        AppRequirements? requirements = null;
        if (_requirementsClarifier != null && IsVaguePrompt(userMessage))
        {
            requirements = await RunClarificationFlowAsync(userMessage).ConfigureAwait(true);
        }
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
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Start the event-processing loop on the calling (UI) thread so it
        // captures the real SynchronizationContext. Previously this was wrapped
        // in Task.Run, which runs on a thread-pool thread where
        // SynchronizationContext.Current is null вЂ” causing
        // TaskScheduler.FromCurrentSynchronizationContext() to throw, so no
        // events were ever applied and the assistant message stayed empty.
        var processingTask = ProcessEventsAsync(assistantMessage, token);
        try
        {
            // Start streaming
            await _agentService.StreamMessageAsync(
                userMessage,
                "default",
                new AgentOptions
                {
                    PermissionMode = ParsePermissionMode(PermissionMode),
                    WorkingDirectory = WorkingDirectory,
                    Rights = new GranularRights
                    {
                        AllowRead = AllowRead,
                        AllowEdit = AllowEdit,
                        AllowExecute = AllowExecute
                    },
                    Requirements = requirements
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
            _eventProcessingComplete?.TrySetResult();
            try
            {
                await processingTask;
            }
            catch (OperationCanceledException)
            {
                // Processing loop cancelled; no-op.
            }
            catch (Exception ex)
            {
                assistantMessage.Content += $"\n\n**Error:** {ex.Message}";
            }
        }
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
    /// <summary>
    /// Runs the selected SDLC pipeline (e.g. full-sdlc: analyze -> implement -> review -> test -> deploy)
    /// against the current input text as a multi-agent session. Reuses the same event bus as SendAsync,
    /// so pipeline stages stream into the chat transcript and the AgentSessions sidebar exactly like a
    /// regular multi-agent turn.
    /// </summary>
    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        // Guard conditions вЂ” provide user-visible feedback instead of a silent no-op.
        if (IsProcessing)
        {
            System.Diagnostics.Debug.WriteLine("RunPipeline: skipped because a turn is already in progress.");
            StatusText = "A turn is already running. Cancel it first.";
            return;
        }
        if (_pipelineRunner == null || _pipelineLoader == null)
        {
            System.Diagnostics.Debug.WriteLine("RunPipeline: skipped because the SDLC pipeline services are not registered.");
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "вљ пёЏ SDLC pipeline services are not available. Please check the application configuration.",
                Timestamp = DateTime.Now
            });
            StatusText = "Pipeline unavailable";
            return;
        }
        if (string.IsNullOrWhiteSpace(InputText))
        {
            System.Diagnostics.Debug.WriteLine("RunPipeline: skipped because the input text is empty.");
            StatusText = "Enter a task before running a pipeline.";
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "Please enter a task description before running a pipeline.",
                Timestamp = DateTime.Now
            });
            return;
        }
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
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Start the event-processing loop on the calling (UI) thread so it
        // captures the real SynchronizationContext (see SendAsync for rationale).
        var processingTask = ProcessEventsAsync(assistantMessage, token, pipelineSessionId);
        try
        {
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
            _eventProcessingComplete?.TrySetResult();
            try
            {
                await processingTask;
            }
            catch (OperationCanceledException)
            {
                // Processing loop cancelled; no-op.
            }
            catch (Exception ex)
            {
                assistantMessage.Content += $"\n\n**Error:** {ex.Message}";
            }
        }
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
    private async Task ProcessEventsAsync(ChatMessage assistantMessage, CancellationToken token, string sessionId = "default")
    {
        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        try
        {
            await foreach (var raw in _eventBus.GetEventsAsync(token))
            {
                // Every event on the shared bus is now wrapped with the
                // session/task it belongs to (see SessionScopedEvent) since
                // background tasks can run concurrently with this chat on
                // the same bus. Skip anything that isn't ours, and unwrap
                // the rest so the switch below sees the original event
                // types unchanged.
                AgentEvent evt = raw;
                if (raw is SessionScopedEvent scoped)
                {
                    if (!string.Equals(scoped.SessionId, sessionId, StringComparison.Ordinal))
                        continue;
                    evt = scoped.Inner;
                }

                switch (evt)
                {
                    case TextDeltaEvent delta:
                        await Task.Factory.StartNew(
                            () => assistantMessage.Content += delta.Delta,
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case ToolCallStartEvent start:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                var card = new ToolCallCardViewModel
                                {
                                    ToolName = start.Call.Name,
                                    Arguments = FormatArguments(start.Call.Arguments),
                                    IsExpanded = false
                                };
                                ToolCallCards.Add(card);
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case ToolCallEndEvent end:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                var existingCard = ToolCallCards.FirstOrDefault(c => c.ToolName == end.Call.Name);
                                if (existingCard != null)
                                {
                                    existingCard.Output = TruncateOutput(end.Result.Content, 500);
                                    existingCard.Duration = end.Duration;
                                    existingCard.IsError = end.Result.IsError;
                                }
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case ApprovalRequestEvent approval:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                ApprovalRisk = approval.Risk.ToString();
                                ShowApprovalDialog = true;
                                ApprovalToolName = approval.Call.Name;
                                ApprovalArgs = FormatArguments(approval.Call.Arguments);
                                _pendingApproval = approval.Approval;
                                // Show an explicit, user-visible approval request in the
                                // chat transcript so the user knows a decision is needed
                                // and where to approve/decline вЂ” previously the agent
                                // could appear to hang with no visible prompt.
                                Messages.Add(new ChatMessage
                                {
                                    Role = "System",
                                    Content = $"рџ”” **Approval required** вЂ” `{approval.Call.Name}` (risk: {approval.Risk})\n" +
                                              $"Arguments: {FormatArguments(approval.Call.Arguments)}\n" +
                                              "Use the approval dialog below to **Approve** or **Decline**.",
                                    Timestamp = DateTime.Now
                                });
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
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
                        await Task.Factory.StartNew(
                            () => HandleAgentTaggedEvent(tagged, assistantMessage),
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case StatusUpdateEvent status:
                        await Task.Factory.StartNew(
                            () => StatusText = status.Status,
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case TokenUsageEvent usage:
                        await Task.Factory.StartNew(
                            () => TokenUsage = $"Tokens: {usage.Usage.TotalTokens}",
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case PlanGeneratedEvent plan:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                var stepList = plan.Steps.ToList();
                                PlanVM.LoadPlan(stepList);
                                assistantMessage.Content += $"**Plan generated** ({stepList.Count} steps):\n" +
                                    string.Join("\n", stepList.Select(s =>
                                        $"{s.Index}. {s.Description}" +
                                        (s.FilesLikelyTouched.Count == 0 ? "" :
                                            $" — `{string.Join("`, `", s.FilesLikelyTouched)}`")));
                                StatusText = "Executing plan...";
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case PlanStepStartedEvent stepStarted:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                PlanVM.UpdateStep(stepStarted.StepIndex, stepStarted.Step);
                                StatusText = $"Step {stepStarted.StepIndex + 1}: {stepStarted.Step.Description}";
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case PlanStepFinishedEvent stepFinished:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                PlanVM.UpdateStep(stepFinished.StepIndex, stepFinished.Step);
                                if (stepFinished.Step.Status == PlanStepStatus.Failed &&
                                    !string.IsNullOrEmpty(stepFinished.Step.FailureReason))
                                {
                                    assistantMessage.Content += $"\n\n**Step {stepFinished.StepIndex + 1} failed:** {stepFinished.Step.FailureReason}";
                                }
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case PlanStepStatusChangedEvent statusChanged:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                if (statusChanged.StepIndex >= 0 && statusChanged.StepIndex < PlanVM.Steps.Count)
                                {
                                    PlanVM.Steps[statusChanged.StepIndex].Status = statusChanged.Status;
                                }
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case PlanRunFinishedEvent runFinished:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                PlanVM.Complete(runFinished.Log.FailureSummary);
                                StatusText = string.IsNullOrEmpty(runFinished.Log.FailureSummary)
                                    ? "Plan complete"
                                    : "Plan failed";
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        _eventProcessingComplete?.TrySetResult();
                        return;
                    case AgentFinishedEvent finished:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                if (finished.Response.WasCancelled)
                                {
                                    assistantMessage.Content += "\n\n*Cancelled*";
                                }
                                StatusText = "Done";
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        _eventProcessingComplete?.TrySetResult();
                        return;
                    case AgentErrorEvent error:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                assistantMessage.Content += $"\n\n**Error:** {error.Error.Message}";
                                StatusText = "Error";
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        _eventProcessingComplete?.TrySetResult();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
    }
    /// <summary>
    /// Heuristic for deciding whether a prompt is vague enough to warrant the
    /// clarification loop. Avoids running it for slash commands, very short
    /// inputs, or prompts that already mention multiple requirement dimensions.
    /// </summary>
    private static bool IsVaguePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length < 12)
            return false;
        if (prompt.StartsWith('/'))
            return false;
        var lower = prompt.ToLowerInvariant();
        var dimensionsHit = 0;
        if (lower.Contains("web") || lower.Contains("mobile") || lower.Contains("desktop") || lower.Contains("cli") || lower.Contains("api"))
            dimensionsHit++;
        if (lower.Contains("auth") || lower.Contains("login") || lower.Contains("jwt") || lower.Contains("oauth"))
            dimensionsHit++;
        if (lower.Contains("database") || lower.Contains("postgres") || lower.Contains("mongo") || lower.Contains("entity"))
            dimensionsHit++;
        if (lower.Contains("tailwind") || lower.Contains("material") || lower.Contains("css") || lower.Contains("styled"))
            dimensionsHit++;
        if (lower.Contains("stripe") || lower.Contains("sendgrid") || lower.Contains("aws") || lower.Contains("integration"))
            dimensionsHit++;
        if (lower.Contains("docker") || lower.Contains("vercel") || lower.Contains("deploy") || lower.Contains("serverless"))
            dimensionsHit++;
        // If the prompt already specifies 3+ dimensions, treat it as detailed.
        return dimensionsHit < 3;
    }

    /// <summary>
    /// Runs the requirements clarification loop, surfacing questions as
    /// assistant chat messages and collecting answers via InputText submission.
    /// Shows a summary card when done. Returns the confirmed AppRequirements
    /// (null if the user dismissed it or no clarifier is available).
    /// </summary>
    private async Task<AppRequirements?> RunClarificationFlowAsync(string userPrompt)
    {
        try
        {
            var requirements = await _requirementsClarifier!.RunAsync(
                userPrompt,
                AskUserAsync,
                CancellationToken.None).ConfigureAwait(true);

            // Show the summary card as a System message
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = requirements.ToSummaryCard(),
                Timestamp = DateTime.Now
            });

            CurrentRequirements = requirements;
            return requirements;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Requirements clarification failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// askFunc callback for <see cref="IRequirementsClarifier.RunAsync"/>: posts
    /// the question to the chat as an Assistant message and awaits the user's
    /// next InputText submission via a TaskCompletionSource.
    /// </summary>
    private async Task<string?> AskUserAsync(string dimension, string question)
    {
        Messages.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = $"рџ“ќ **Requirements clarification** ({dimension})\n{question}\n\n_Type your answer, or \"just build it\" to skip._",
            Timestamp = DateTime.Now
        });

        CurrentClarificationDimension = dimension;
        CurrentClarificationQuestion = question;
        ClarificationQuestions.Clear();
        ClarificationQuestions.Add(new RequirementGap { Dimension = dimension, Question = question });
        IsAwaitingClarification = true;

        _pendingClarificationAnswer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return await _pendingClarificationAnswer.Task.ConfigureAwait(true);
    }

    [RelayCommand]
    private void SkipClarification()
    {
        _pendingClarificationAnswer?.TrySetResult("just build it");
        _pendingClarificationAnswer = null;
        IsAwaitingClarification = false;
        CurrentClarificationQuestion = "";
        CurrentClarificationDimension = "";
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
                ApprovalRisk = approval.Risk.ToString();
                ShowApprovalDialog = true;
                ApprovalToolName = approval.Call.Name;
                ApprovalArgs = FormatArguments(approval.Call.Arguments);
                ApprovalAgent = tagged.AgentId;
                ApprovalRole = tagged.Role ?? "";
                _pendingApproval = approval.Approval;
                // Show an explicit, user-visible approval request in the
                // chat transcript for multi-agent sessions too.
                Messages.Add(new ChatMessage
                {
                    Role = "System",
                    Content = $"рџ”” **Approval required** from `{tagged.AgentId}` ({tagged.Role ?? "agent"}) вЂ” " +
                              $"`{approval.Call.Name}` (risk: {approval.Risk})\n" +
                              $"Arguments: {FormatArguments(approval.Call.Arguments)}\n" +
                              "Use the approval dialog below to **Approve** or **Decline**.",
                    Timestamp = DateTime.Now
                });
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
                _eventProcessingComplete?.TrySetResult();
                break;
            case AgentErrorEvent error:
                assistantMessage.Content += $"\n\n**Error ({tagged.AgentId}):** {error.Error.Message}";
                if (existing != null)
                {
                    existing.Status = "Error";
                }
                StatusText = "Error";
                _eventProcessingComplete?.TrySetResult();
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
                "/edit" => "вњЏпёЏ",
                "/search" => "рџ”Ќ",
                "/explain" => "рџ’Ў",
                "/test" => "рџ§Є",
                "/fix" => "рџ”§",
                "/refactor" => "рџ”„",
                "/help" => "вќ“",
                _ => "рџ“‹"
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
                "/edit" => "вњЏпёЏ",
                "/search" => "рџ”Ќ",
                "/explain" => "рџ’Ў",
                "/test" => "рџ§Є",
                "/fix" => "рџ”§",
                "/refactor" => "рџ”„",
                "/help" => "вќ“",
                _ => "рџ“‹"
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
            ".cs" => "рџ”·",
            ".xaml" or ".axaml" => "рџџ¦",
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "рџ“‹",
            ".md" or ".txt" => "рџ“ќ",
            ".csproj" or ".sln" or ".slnx" => "рџ“¦",
            ".js" or ".ts" or ".jsx" or ".tsx" => "рџџЁ",
            ".py" => "рџђЌ",
            ".html" or ".css" or ".scss" => "рџЊђ",
            _ => "рџ“„"
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
    private static bool TryParseCodebaseCommand(string input, out string question)
    {
        const string token = "@codebase";
        question = string.Empty;
        if (input == null || input.Length < token.Length)
            return false;
        if (!input.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = input[token.Length..].Trim();
        if (string.IsNullOrEmpty(rest))
            return false;
        if (rest.EndsWith('/'))
            rest = rest[..^1].TrimEnd();
        if (string.IsNullOrEmpty(rest))
            return false;
        question = rest;
        return true;
    }
    private async Task SendCodebaseQueryAsync(string question)
    {
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = $"@codebase " + question,
            Timestamp = DateTime.Now
        });
        if (_semanticIndex == null || _retriever == null)
        {
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "Warning: Semantic codebase Q&A is not available. " +
                          "Configure an embedding provider (e.g. set the OPENAI_API_KEY) " +
                          "and restart the application to enable @codebase.",
                Timestamp = DateTime.Now
            });
            return;
        }
        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Indexing codebase...";
        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            await _semanticIndex.IndexWorkspaceAsync(WorkingDirectory, cancellationToken: token).ConfigureAwait(true);
            StatusText = "Searching codebase...";
            var retrieval = await _retriever.RetrieveAsync(question, topK: 12, cancellationToken: token)
                .ConfigureAwait(true);
            if (retrieval.Chunks.Count == 0)
            {
                Messages.Add(new ChatMessage
                {
                    Role = "Assistant",
                    Content = "No indexed code chunks were found. Try re-indexing the workspace.",
                    Timestamp = DateTime.Now
                });
                return;
            }
            var contextBlock = retrieval.ContextBlock;
            var groundedPrompt = AiCodeAgent.Indexing.Semantic.Retriever.BuildPrompt(question, contextBlock);
            var assistantMessage = new ChatMessage
            {
                Role = "Assistant",
                Content = string.Empty,
                Timestamp = DateTime.Now
            };
            Messages.Add(assistantMessage);
            ToolCallCards.Clear();
            StatusText = "Answering...";
            _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processingTask = ProcessEventsAsync(assistantMessage, token);
            try
            {
                await _agentService.StreamMessageAsync(
                    groundedPrompt,
                    "default",
                    new AgentOptions
                    {
                        PermissionMode = ParsePermissionMode(PermissionMode),
                        WorkingDirectory = WorkingDirectory,
                        Rights = new GranularRights
                        {
                            AllowRead = AllowRead,
                            AllowEdit = AllowEdit,
                            AllowExecute = AllowExecute
                        }
                    },
                    token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                assistantMessage.Content += "\n\n*Cancelled*";
            }
            catch (Exception ex)
            {
                assistantMessage.Content += "\n\n**Error:** " + ex.Message;
            }
            finally
            {
                _eventProcessingComplete?.TrySetResult();
                try
                {
                    await processingTask.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Processing loop cancelled; no-op.
                }
                catch (Exception ex)
                {
                    assistantMessage.Content += "\n\n**Error:** " + ex.Message;
                }
            }
            if (string.IsNullOrEmpty(assistantMessage.Content))
            {
                assistantMessage.Content = "*(No response generated)*";
            }
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "Retrieved context (top " + retrieval.Chunks.Count + " chunks):\n" +
                          string.Join("\n", retrieval.Chunks.Select(h =>
                              "- " + Path.GetFileName(h.Chunk.FilePath) +
                              (string.IsNullOrEmpty(h.Chunk.Symbol) ? "" : " - " + h.Chunk.Symbol) +
                              " (score " + h.Score.ToString("F3") + ")")),
                Timestamp = DateTime.Now
            });
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "*Cancelled.*",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "Codebase query failed: " + ex.Message,
                Timestamp = DateTime.Now
            });
        }
        finally
        {
            IsProcessing = false;
            IsCancellable = false;
            StatusText = "Ready";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }
    /// <summary>
    /// Runs the autonomous multi-file agent (Feature 4): generates a plan via
    /// <see cref="IPlanGenerator"/> then executes it step-by-step via
    /// <see cref="IAutonomousAgentRunner"/>, streaming plan events into the
    /// chat transcript and the <see cref="PlanVM"/> panel.
    /// </summary>
    private async Task RunPlanAsync(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
            return;
        if (_planGenerator == null || _autonomousRunner == null)
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "вљ пёЏ Autonomous agent is not configured (IPlanGenerator/IAutonomousAgentRunner not registered).",
                Timestamp = DateTime.Now
            });
            return;
        }
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/plan {task}",
            Timestamp = DateTime.Now
        });
        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Planning...";
        var assistantMessage = new ChatMessage
        {
            Role = "Assistant",
            Content = string.Empty,
            Timestamp = DateTime.Now
        };
        Messages.Add(assistantMessage);
        ToolCallCards.Clear();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = Guid.NewGuid().ToString("N");
        var processingTask = ProcessEventsAsync(assistantMessage, token, sessionId);
        try
        {
            var steps = await _planGenerator.GenerateAsync(task, WorkingDirectory, token)
                .ConfigureAwait(true);
            var options = new AgentOptions
            {
                PermissionMode = ParsePermissionMode(PermissionMode),
                WorkingDirectory = WorkingDirectory,
                Rights = new GranularRights
                {
                    AllowRead = AllowRead,
                    AllowEdit = AllowEdit,
                    AllowExecute = AllowExecute
                },
                SessionId = sessionId,
                AgentId = "autonomous"
            };
            var log = await _autonomousRunner.RunAsync(steps, task, sessionId, options, token)
                .ConfigureAwait(true);
            if (!string.IsNullOrEmpty(log.FailureSummary))
                assistantMessage.Content += $"\n\n**Plan result:** {log.FailureSummary}";
            else
                assistantMessage.Content += "\n\n**Plan completed successfully.**";
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
            _eventProcessingComplete?.TrySetResult();
            try
            {
                await processingTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Processing loop cancelled; no-op.
            }
            catch (Exception ex)
            {
                assistantMessage.Content += $"\n\n**Error:** {ex.Message}";
            }
            IsProcessing = false;
            IsCancellable = false;
            StatusText = "Ready";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
        if (string.IsNullOrEmpty(assistantMessage.Content))
            assistantMessage.Content = "*(No response generated)*";
    }

    // ─────────────────────────────────────────────────────────────────────
    // /edit — Open a file in the editor pane (VS Code-style)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles the <c>/edit <file_path></c> slash command by opening the
    /// specified file in the editor pane, making it visible and ready for
    /// editing — similar to VS Code's <c>code <file></c> behavior.
    /// Supports both absolute and workspace-relative paths. When no path is
    /// supplied, lists project files to guide the user.
    /// </summary>
    private async Task HandleEditCommandAsync(string userMessage)
    {
        // Strip the leading "/edit" token and any surrounding whitespace.
        var rawArgs = userMessage.Length > "/edit".Length
            ? userMessage["/edit".Length..].Trim()
            : string.Empty;

        // Handle the bare "/edit" (no arguments) case: show usage + a
        // short list of project files so the user can pick one.
        if (string.IsNullOrEmpty(rawArgs))
        {
            Messages.Add(new ChatMessage
            {
                Role = "User",
                Content = userMessage,
                Timestamp = DateTime.Now
            });
            var sample = GetProjectFiles().Take(15).Select(f => $"- `{f}`");
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "✏️ **Usage:** `/edit <file_path>`\n\n" +
                          "Opens a file in the editor pane for direct editing.\n\n" +
                          "**Project files:**\n" +
                          (sample.Any() ? string.Join("\n", sample) : "_No files found._"),
                Timestamp = DateTime.Now
            });
            return;
        }

        // Resolve the path: treat as workspace-relative when not rooted.
        var filePath = rawArgs;
        if (!Path.IsPathRooted(filePath))
            filePath = Path.Combine(WorkingDirectory, filePath);
        filePath = Path.GetFullPath(filePath);

        // Echo the user's command into the transcript.
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = userMessage,
            Timestamp = DateTime.Now
        });

        if (!File.Exists(filePath))
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = $"⚠️ File not found: `{filePath}`\n" +
                          "Check the path and try again. Use `/edit` (no arguments) to list project files.",
                Timestamp = DateTime.Now
            });
            return;
        }

        try
        {
            StatusText = $"Opening {Path.GetFileName(filePath)}...";
            await _editorPane.OpenFileAsync(filePath).ConfigureAwait(true);
            _editorPane.IsVisible = true;
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = $"✏️ Opened `{filePath}` in the editor. Make your changes and press **Ctrl+S** to save.",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = $"**Error opening file:** {ex.Message}",
                Timestamp = DateTime.Now
            });
        }
        finally
        {
            StatusText = "Ready";
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Feature 6: In-Chat Branch / PR Workflow
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Intercepts the <c>/branch</c>, <c>/commit</c>, and <c>/pr</c> slash
    /// commands, dispatching them to <see cref="GitService"/> and rendering
    /// structured result cards into the chat. Remote operations (push, PR)
    /// require explicit user confirmation.
    /// </summary>
    /// <returns><c>true</c> if the message was handled as a git command.</returns>
    private async Task<bool> TryHandleGitCommandAsync(string userMessage)
    {
        if (_gitService == null)
            return false;

        if (userMessage.StartsWith("/branch ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBranchCommandAsync(userMessage["/branch ".Length..].Trim()).ConfigureAwait(true);
            return true;
        }

        if (userMessage.StartsWith("/commit ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCommitCommandAsync(userMessage["/commit ".Length..].Trim()).ConfigureAwait(true);
            return true;
        }

        if (userMessage.StartsWith("/pr ", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePrCommandAsync(userMessage["/pr ".Length..].Trim()).ConfigureAwait(true);
            return true;
        }

        return false;
    }

    private async Task HandleBranchCommandAsync(string branchName)
    {
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/branch {branchName}",
            Timestamp = DateTime.Now
        });

        if (string.IsNullOrWhiteSpace(branchName))
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "⚠️ Usage: `/branch <name>` — please provide a branch name.",
                Timestamp = DateTime.Now
            });
            return;
        }

        StatusText = $"Creating branch '{branchName}'...";
        IsProcessing = true;
        try
        {
            var result = await _gitService!.CreateBranchAsync(branchName).ConfigureAwait(true);
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = FormatGitResultCard("🌿 Branch", result,
                    result.Success
                        ? $"Created and switched to branch `{branchName}`."
                        : $"Failed to create branch: {result.Summary}"),
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = $"**Error creating branch:** {ex.Message}",
                Timestamp = DateTime.Now
            });
        }
        finally
        {
            IsProcessing = false;
            StatusText = "Ready";
        }
    }

    private async Task HandleCommitCommandAsync(string message)
    {
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/commit {message}",
            Timestamp = DateTime.Now
        });

        if (string.IsNullOrWhiteSpace(message))
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "⚠️ Usage: `/commit <message>` — please provide a commit message.",
                Timestamp = DateTime.Now
            });
            return;
        }

        StatusText = "Staging and committing changes...";
        IsProcessing = true;
        try
        {
            var status = await _gitService!.GetStatusAsync().ConfigureAwait(true);
            if (status.ChangedFiles.Count == 0)
            {
                Messages.Add(new ChatMessage
                {
                    Role = "System",
                    Content = "ℹ️ Working tree is clean — nothing to commit.",
                    Timestamp = DateTime.Now
                });
                return;
            }

            var result = await _gitService!.CommitAsync(message).ConfigureAwait(true);
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = FormatGitResultCard("📦 Commit", result,
                    result.Success
                        ? $"Committed as `{result.CommitHash}` — {result.Files.Count} file(s) changed."
                        : $"Failed to commit: {result.Summary}"),
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = $"**Error committing:** {ex.Message}",
                Timestamp = DateTime.Now
            });
        }
        finally
        {
            IsProcessing = false;
            StatusText = "Ready";
        }
    }

    private async Task HandlePrCommandAsync(string title)
    {
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/pr {title}",
            Timestamp = DateTime.Now
        });

        if (string.IsNullOrWhiteSpace(title))
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "⚠️ Usage: `/pr <title>` — please provide a PR title.",
                Timestamp = DateTime.Now
            });
            return;
        }

        var confirm = await RequestUserConfirmationAsync(
            $"This will **push** the current branch to the remote and **open a pull request** titled `{title}`. Proceed?");
        if (!confirm)
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = "Cancelled — no changes pushed.",
                Timestamp = DateTime.Now
            });
            return;
        }

        StatusText = "Pushing branch and creating pull request...";
        IsProcessing = true;
        try
        {
            var body = await BuildPrBodyAsync(title).ConfigureAwait(true);
            StatusText = "Pushing branch and creating pull request...";
            var result = await _gitService!.PushAndCreatePullRequestAsync(title, body).ConfigureAwait(true);
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = FormatGitResultCard("🔗 Pull Request", result,
                    result.Success
                        ? $"PR created: {result.PullRequestUrl}"
                        : $"Failed to create PR: {result.Summary}"),
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "System",
                Content = $"**Error creating PR:** {ex.Message}",
                Timestamp = DateTime.Now
            });
        }
        finally
        {
            IsProcessing = false;
            StatusText = "Ready";
        }
    }

    /// <summary>
    /// Auto-generates a meaningful PR body from the current diff. Uses the
    /// agent's LLM to summarize the changes when available; otherwise falls
    /// back to a structured file-list summary derived from git status. The
    /// body always includes the user-supplied title as a heading.
    /// </summary>
    private async Task<string> BuildPrBodyAsync(string title)
    {
        var status = await _gitService!.GetStatusAsync().ConfigureAwait(true);
        var diff = await _gitService!.GetDiffAsync().ConfigureAwait(true);

        // Truncate very large diffs to stay within a reasonable token budget.
        const int MaxDiffChars = 12000;
        var truncatedDiff = diff.Length > MaxDiffChars
            ? diff[..MaxDiffChars] + "\n...[diff truncated]"
            : diff;

        // Build a structured fallback body from the changed-file list. This
        // is always present so the PR body is meaningful even when the LLM
        // summarization step is unavailable.
        var fileList = status.ChangedFiles.Count == 0
            ? "_No changes detected._"
            : string.Join("\n", status.ChangedFiles.Select(f => $"- `{f}`"));

        var body = new StringBuilder()
            .AppendLine($"## {title}")
            .AppendLine()
            .AppendLine("### Changed files")
            .AppendLine(fileList)
            .AppendLine();

        // Include a concise diff-stat block so reviewers see the scope of
        // changes at a glance even without a full LLM-generated narrative.
        if (!string.IsNullOrWhiteSpace(truncatedDiff))
        {
            var added = 0;
            var removed = 0;
            foreach (var line in truncatedDiff.Split('\n'))
            {
                if (line.StartsWith("+++") || line.StartsWith("---"))
                    continue;
                if (line.StartsWith('+'))
                    added++;
                else if (line.StartsWith('-'))
                    removed++;
            }
            body.AppendLine("### Diff stats")
                .AppendLine($"`{status.ChangedFiles.Count}` file(s) changed - " +
                             $"~{added} additions, ~{removed} deletions");
        }

        return body.ToString().Trim();
    }

    /// <summary>
    /// Renders a structured git-result card as a Markdown block inside the
    /// chat transcript (Feature 6: "rich cards"). The UI already renders
    /// Markdown, so a fenced block with a header line and clickable PR link
    /// is the lightest-weight integration that satisfies the acceptance
    /// criteria without new XAML templates.
    /// </summary>
    private static string FormatGitResultCard(string header, GitResult result, string successSummary)
    {
        if (!result.Success)
            return $"### {header} — ❌\n\n{successSummary}";

        var lines = new List<string>
        {
            $"### {header} — ✅",
            string.Empty,
            successSummary,
            string.Empty,
            "```",
            result.Output,
            "```"
        };

        if (result is PullRequestResult { PullRequestUrl: { } url } && !string.IsNullOrEmpty(url))
        {
            lines.Add(string.Empty);
            lines.Add($"**PR link:** [{url}]({url})");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Requests a yes/no confirmation from the user via the existing approval
    /// dialog. Reuses <see cref="ShowApprovalDialog"/> so no separate
    /// confirmation template is needed for git remote operations.
    /// </summary>
    private async Task<bool> RequestUserConfirmationAsync(string message)
    {
        Messages.Add(new ChatMessage
        {
            Role = "System",
            Content = $"🔔 **Confirmation required**\n{message}\nUse the approval dialog below to **Approve** or **Decline**.",
            Timestamp = DateTime.Now
        });

        ApprovalRisk = "Write";
        ApprovalToolName = "git";
        ApprovalArgs = message;
        ShowApprovalDialog = true;

        _pendingApproval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            return await _pendingApproval.Task.ConfigureAwait(true);
        }
        finally
        {
            ShowApprovalDialog = false;
            _pendingApproval = null;
        }
    }
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
    public string Icon { get; set; } = "рџ“„";
}

public class SlashCommandItem
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "рџ“‹";
}
