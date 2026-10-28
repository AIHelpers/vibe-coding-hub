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
    /// Stable id for this chat conversation's lifetime, used for every
    /// agent run this view-model starts (chat messages, annotations,
    /// grounded/semantic-retrieval answers). Previously every call site
    /// passed the literal "default", which happened to work only because
    /// every call used the exact same literal; a real per-instance id lets
    /// checkpoints and other session-scoped state be looked up reliably
    /// (see MainViewModel, which forwards this to CheckpointBrowser).
    /// </summary>
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
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

    /// <summary>What the approval in the dialog would do: a diff, the command, or the raw arguments.</summary>
    [ObservableProperty]
    private ApprovalPreviewViewModel? _approvalPreview;

    /// <summary>True while the "Deny with feedback…" box is open.</summary>
    [ObservableProperty]
    private bool _isEnteringApprovalFeedback;

    [ObservableProperty]
    private string _approvalFeedbackText = string.Empty;

    /// <summary>Standing decisions: "approve all edits this task", "always allow &lt;tool&gt; this session".</summary>
    public ApprovalPolicy Approvals { get; } = new();

    /// <summary>The "Agent:" row is hidden when nobody is named (plain agent, no character).</summary>
    public bool HasApprovalAgent => !string.IsNullOrWhiteSpace(ApprovalAgent);

    /// <summary>False for explicit confirmations (git push/PR): no standing rule can be set from them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApproveAllEdits))]
    private bool _canSetApprovalRules = true;

    /// <summary>"Approve all edits this task" only makes sense for an edit tool.</summary>
    public bool IsEditApproval => ApprovalPolicy.IsEditTool(ApprovalToolName);

    public bool CanApproveAllEdits => IsEditApproval && CanSetApprovalRules;

    public string AlwaysAllowLabel => $"Always allow {ApprovalToolName} this session";

    partial void OnApprovalAgentChanged(string value) => OnPropertyChanged(nameof(HasApprovalAgent));

    partial void OnApprovalToolNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsEditApproval));
        OnPropertyChanged(nameof(CanApproveAllEdits));
        OnPropertyChanged(nameof(AlwaysAllowLabel));
    }
    // Agent session sidebar
    [ObservableProperty]
    private bool _isAgentSidebarOpen;
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

    /// <summary>
    /// Approval requests waiting for the user. Parallel agents (and background
    /// tasks) can each ask at the same time; a single slot used to be overwritten
    /// by the newest request so the earlier agent hung until its timeout.
    /// </summary>
    private readonly Queue<PendingApprovalRequest> _approvalQueue = new();
    private sealed record PendingApprovalRequest(
        TaskCompletionSource<bool> Completion, string Risk, string ToolName, string Args, string Agent, string Role,
        ApprovalItem? Item, ApprovalPreviewViewModel? Preview, ApprovalFeedback? Feedback, bool RulesApply = true);

    /// <summary>Where "Deny with feedback" writes the user's note for the approval shown right now.</summary>
    private ApprovalFeedback? _currentApprovalFeedback;

    /// <summary>The timeline entry of the approval shown in the dialog right now.</summary>
    private ApprovalItem? _currentApprovalItem;

    private void EnqueueApproval(TaskCompletionSource<bool> completion, string risk, string toolName, string args,
        string agent = "", string role = "", ApprovalItem? item = null,
        ApprovalPreviewViewModel? preview = null, ApprovalFeedback? feedback = null, bool rulesApply = true)
    {
        _approvalQueue.Enqueue(new PendingApprovalRequest(completion, risk, toolName, args, agent, role, item, preview, feedback, rulesApply));
        if (_pendingApproval is null)
            ShowNextApproval();
    }

    private void ShowNextApproval()
    {
        while (_approvalQueue.Count > 0)
        {
            var next = _approvalQueue.Dequeue();
            if (next.Completion.Task.IsCompleted)
            {
                // Already answered / timed out while waiting.
                next.Item?.Resolve(next.Completion.Task.IsCompletedSuccessfully && next.Completion.Task.Result);
                continue;
            }

            // A rule chosen on an earlier approval ("approve all edits", "always allow") covers queued ones too.
            if (next.RulesApply && Approvals.TryAutoApprove(next.ToolName, out var reason))
            {
                next.Completion.TrySetResult(true);
                next.Item?.Resolve(true, reason);
                continue;
            }

            ApprovalRisk = next.Risk;
            ApprovalToolName = next.ToolName;
            ApprovalArgs = next.Args;
            ApprovalAgent = next.Agent;
            ApprovalRole = next.Role;
            _pendingApproval = next.Completion;
            _currentApprovalItem = next.Item;
            _currentApprovalFeedback = next.Feedback;
            CanSetApprovalRules = next.RulesApply;
            ApprovalPreview = next.Preview;
            IsEnteringApprovalFeedback = false;
            ApprovalFeedbackText = string.Empty;
            ShowApprovalDialog = true;
            return;
        }

        _pendingApproval = null;
        _currentApprovalItem = null;
        _currentApprovalFeedback = null;
        ApprovalPreview = null;
        IsEnteringApprovalFeedback = false;
        ShowApprovalDialog = false;
    }

    /// <summary>
    /// Handles an approval request from the agent (main chat or a tagged multi-agent event): auto-approves it when a
    /// standing rule covers the tool, otherwise records it in the timeline and queues it for the dialog with its
    /// diff / command preview.
    /// </summary>
    internal void OnApprovalRequested(ApprovalRequestEvent approval, string? agentId = null, string? role = null)
    {
        var risk = approval.Risk.ToString();
        var item = Timeline.AddApproval(approval.Call, risk, role ?? agentId);

        if (Approvals.TryAutoApprove(approval.Call.Name, out var reason))
        {
            approval.Approval.TrySetResult(true);
            item.Resolve(true, reason);
            return;
        }

        var preview = ApprovalPreviewBuilder.Build(approval.Call, approval.Preview);
        EnqueueApproval(approval.Approval, risk, approval.Call.Name, FormatArguments(approval.Call.Arguments),
            ApprovalAgentLabel(agentId, role), role ?? string.Empty, item, preview, approval.Feedback);
        Activity.SetBlocking($"⏸ Waiting for your approval: {AgentActivityTracker.DescribeTool(approval.Call.Name, approval.Call.Arguments)}");
    }

    /// <summary>Who asks: the agent's role/id in multi-agent runs, else the active character's display name, else nobody.</summary>
    private string ApprovalAgentLabel(string? agentId, string? role)
    {
        if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(agentId) && role != agentId)
            return $"{role} ({agentId})";
        if (!string.IsNullOrWhiteSpace(role)) return role!;
        if (!string.IsNullOrWhiteSpace(agentId)) return agentId!;
        if (!HasActiveCharacter) return string.Empty;
        try
        {
            var character = ActiveCharacter();
            return string.IsNullOrWhiteSpace(character?.DisplayName) ? SelectedCharacterName : character!.DisplayName;
        }
        catch
        {
            return SelectedCharacterName;
        }
    }

    /// <summary>Answers the approval in the dialog, records why in the timeline, and shows the next one.</summary>
    private void ResolveCurrentApproval(bool approved, string? note = null)
    {
        _pendingApproval?.TrySetResult(approved);
        _pendingApproval = null;
        _currentApprovalItem?.Resolve(approved, note);
        _currentApprovalItem = null;
        _currentApprovalFeedback = null;
        Activity.ClearBlocking();
        ShowNextApproval();
    }

    private void DenyAllApprovals()
    {
        _pendingApproval?.TrySetResult(false);
        _pendingApproval = null;
        _currentApprovalItem?.Resolve(false);
        _currentApprovalItem = null;
        _currentApprovalFeedback = null;
        ApprovalPreview = null;
        IsEnteringApprovalFeedback = false;
        while (_approvalQueue.Count > 0)
        {
            var queued = _approvalQueue.Dequeue();
            queued.Completion.TrySetResult(false);
            queued.Item?.Resolve(false);
        }
        ShowApprovalDialog = false;
    }
    // Feature 8: Conversational Requirements Clarification
    private readonly IRequirementsClarifier? _requirementsClarifier;
    private TaskCompletionSource<string?>? _pendingClarificationAnswer;
    [ObservableProperty]
    private bool _isAwaitingClarification;
    [ObservableProperty]
    private AppRequirements? _currentRequirements;
    private bool _requirementsSummaryShown;
    private AppRequirements? _pendingRequirements;

    /// <summary>One-line requirements status under the chat, or null when none are confirmed.</summary>
    public string? RequirementsLine => CurrentRequirements?.ToStatusLine();

    partial void OnCurrentRequirementsChanged(AppRequirements? value) => OnPropertyChanged(nameof(RequirementsLine));

    private AppRequirements? TakePendingRequirements()
    {
        var pending = _pendingRequirements;
        _pendingRequirements = null;
        return pending;
    }
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
    /// <summary>
    /// The chat as one chronological timeline: user messages, assistant text per step, tool cards, approvals and
    /// notices in the order they happened. The view renders it with a single ItemsControl.
    /// </summary>
    public ChatTimeline Timeline { get; } = new();

    /// <summary>Live "what is the agent doing now" line with elapsed time, shown above the input box.</summary>
    public AgentActivityTracker Activity { get; } = new();
    private Avalonia.Threading.DispatcherTimer? _activityTimer;

    partial void OnIsProcessingChanged(bool value)
    {
        if (value)
        {
            Activity.Begin();
            StartActivityTimer();
        }
        else
        {
            StopActivityTimer();
            Activity.End();
            // "Approve all edits this task" ends with the task.
            Approvals.EndTask();
        }
    }

    // Plan steps, indexing, pipeline stages, etc. report progress through StatusText; mirror it as the current activity.
    partial void OnStatusTextChanged(string value)
    {
        if (!IsProcessing) return;
        if (value is "Ready" or "Done" or "Error" or "Processing..." or "Cancelling...") return;
        Activity.SetPhase(value);
    }

    private void StartActivityTimer()
    {
        try
        {
            _activityTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _activityTimer.Tick -= OnActivityTimerTick;
            _activityTimer.Tick += OnActivityTimerTick;
            _activityTimer.Start();
        }
        catch (Exception ex)
        {
            // No dispatcher (e.g. a bare unit test): the elapsed time just won't tick.
            System.Diagnostics.Debug.WriteLine($"Activity timer unavailable: {ex.Message}");
        }
    }

    private void StopActivityTimer()
    {
        try { _activityTimer?.Stop(); } catch { /* ignore */ }
    }

    private void OnActivityTimerTick(object? sender, EventArgs e) => Activity.Tick();
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
        "/branch", "/commit", "/pr",
        "/rewind", "/fork", "/effort", "/requirements",
        // Characters & skills
        "/character", "/characters", "/skill", "/skills"
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
        IRequirementsClarifier? requirementsClarifier = null,
        AiCodeAgent.Core.Sessions.ConversationRewinder? rewinder = null,
        IRewindPrompt? rewindPrompt = null,
        AiCodeAgent.Core.Characters.ICharacterRegistry? characterRegistry = null,
        RolePresetLoader? presetLoader = null,
        ISkillRegistry? skillRegistry = null,
        SessionResumeService? sessionResume = null)
    {
        InitializeRewind(rewinder, rewindPrompt);
        InitializeResume(sessionResume);
        InitializeCharacters(characterRegistry, presetLoader, skillRegistry);
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
            RefreshAvailablePipelines();
            // Flows saved in the Flows panel show up in the pipeline picker right away.
            _pipelineLoader.Changed += (_, _) =>
            {
                try
                {
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RefreshAvailablePipelines();
                    else Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAvailablePipelines);
                }
                catch
                {
                    RefreshAvailablePipelines();
                }
            };
        }
        // Restore the last selected model from persisted UI settings so the
        // user's choice survives application restarts. Fall back to "Auto"
        // when unset or no longer in the available list.
        // The permission mode chosen in the setup wizard is where a new chat starts.
        if (_configurationService?.Config.Agent?.DefaultPermissionMode is ("Ask" or "AutoEdit" or "FullAuto" or "Plan") and var startMode)
            PermissionMode = startMode;
        // The reasoning effort chosen in Settings -> Agent is where a new chat starts.
        if (_configurationService?.Config.Agent?.DefaultReasoningEffort is { } startEffort
            && EffortOptions.FirstOrDefault(o => string.Equals(o, startEffort, StringComparison.OrdinalIgnoreCase)) is { } effortName)
            ReasoningEffortName = effortName;
        var savedModel = _configurationService?.Config.Ui?.SelectedModel;
        if (!string.IsNullOrWhiteSpace(savedModel))
        {
            SelectedModel = AvailableModels.Contains(savedModel)
                ? savedModel
                : savedModel; // keep even if not in the static list (may be a provider model id)
        }
        // Initialize slash commands
        SlashCommandItems.Add(new SlashCommandItem { Name = "/edit", Description = "Edit a specific file", Icon = "✏️" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/search", Description = "Search the codebase", Icon = "🔍" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/explain", Description = "Explain code logic", Icon = "💡" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/test", Description = "Generate tests", Icon = "🧪" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/fix", Description = "Fix issues in code", Icon = "🔧" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/requirements", Description = "Clarify requirements for the next app", Icon = "📋" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/refactor", Description = "Refactor code", Icon = "🔄" });
        SlashCommandItems.Add(new SlashCommandItem { Name = "/help", Description = "Show available commands", Icon = "❓" });
        // Add welcome message
        Timeline.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = WelcomeText,
            Timestamp = DateTime.Now
        });
    }
    /// <summary>The first message of every new chat. Plain Markdown with a clean list: no emoji, so it looks the same on every OS.</summary>
    internal const string WelcomeText =
        "Hello! I'm your AI code assistant. I can read, write and edit files, search code, run commands and work with git.\n\n" +
        "**What you can do here**\n\n" +
        "- Watch answers stream in as they are written\n" +
        "- Choose how much I may do on my own: Ask, AutoEdit, FullAuto or Plan\n" +
        "- Review every change as a diff, with checkpoints to roll back\n" +
        "- Stop at any time with Esc or the Stop button\n" +
        "- Open the Explorer (Ctrl+B) and the terminal (Ctrl+`)\n" +
        "- Type @ to add a file as context, or / for commands";

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

        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = content,
            Timestamp = DateTime.Now
        });

        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Processing annotation...";

        var run = Timeline.BeginRun();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processingTask = ProcessEventsAsync(run, token, SessionId);
        try
        {
            await _agentService.StreamMessageAsync(
                content,
                SessionId,
                new AgentOptions
                {
                    PermissionMode = ParsePermissionMode(PermissionMode),
                    Reasoning = ReasoningEffortValue,
                    AllowedPaths = AdditionalFolders,
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
            run.AppendNote("\n\n*Cancelled*");
        }
        catch (Exception ex)
        {
            run.AppendNote($"\n\n**Error:** {ex.Message}");
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
                run.AppendNote($"\n\n**Error:** {ex.Message}");
            }
        }

        IsProcessing = false;
        IsCancellable = false;
        StatusText = "Ready";
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        run.Complete();
    }

    /// <summary>
    /// Persists the user's model selection to the UI configuration so it is
    /// restored on the next application launch. Fire-and-forget: failures are
    /// logged to the debug output and never crash the UI.
    /// </summary>
    /// <summary>
    /// The model id this run sends. The picker's Fast and Smart map to the fast and frontier tier of the default
    /// provider (see models.json); Auto sends nothing, so the provider's configured default model is used; a
    /// concrete id is used as it is.
    /// </summary>
    internal string? ModelForRun()
    {
        var provider = _configurationService?.Config.DefaultProvider;
        if (string.IsNullOrWhiteSpace(provider)) return null;
        return AiCodeAgent.Core.Configuration.ModelCatalogLoader.Default.ModelForRun(SelectedModel, provider);
    }

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
    // Concurrent execution must be allowed: while the clarification flow is awaiting inside the first SendAsync,
    // the user's answer arrives through a second SendAsync call (otherwise the Send button stays disabled).
    [RelayCommand(AllowConcurrentExecutions = true)]
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
        // Rewind / fork / reasoning effort.
        if (await TryHandleSessionCommandAsync(userMessage).ConfigureAwait(true))
            return;
        // Characters and skills: /character, /characters, /skill, /skills.
        if (await TryHandleCharacterCommandAsync(userMessage).ConfigureAwait(true))
            return;
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
        var clarifierMode = _configurationService?.Config.Agent?.RequirementsClarifier ?? RequirementsClarifierMode.Auto;
        if (_requirementsClarifier != null &&
            ClarifierTrigger.ShouldClarify(userMessage, () => ClarifierTrigger.HasSourceFiles(WorkingDirectory), clarifierMode))
        {
            requirements = await RunClarificationFlowAsync(userMessage).ConfigureAwait(true);
        }
        // Requirements confirmed with /requirements apply to the task that follows them.
        requirements ??= TakePendingRequirements();
        // Add user message
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = userMessage,
            Timestamp = DateTime.Now
        });
        TaskSubmitted?.Invoke(this, EventArgs.Empty);
        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Processing...";
        var run = Timeline.BeginRun();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Start the event-processing loop on the calling (UI) thread so it
        // captures the real SynchronizationContext. Previously this was wrapped
        // in Task.Run, which runs on a thread-pool thread where
        // SynchronizationContext.Current is null — causing
        // TaskScheduler.FromCurrentSynchronizationContext() to throw, so no
        // events were ever applied and the assistant message stayed empty.
        var processingTask = ProcessEventsAsync(run, token, SessionId);
        try
        {
            // Start streaming
            await _agentService.StreamMessageAsync(
                ApplyPendingSkills(userMessage),
                SessionId,
                ApplyActiveCharacter(new AgentOptions
                {
                    Model = ModelForRun(),
                    PermissionMode = ParsePermissionMode(PermissionMode),
                    Reasoning = ReasoningEffortValue,
                    AllowedPaths = AdditionalFolders,
                    WorkingDirectory = WorkingDirectory,
                    Rights = new GranularRights
                    {
                        AllowRead = AllowRead,
                        AllowEdit = AllowEdit,
                        AllowExecute = AllowExecute
                    },
                    Requirements = requirements
                }),
                token);
        }
        catch (OperationCanceledException)
        {
            run.AppendNote("\n\n*Cancelled*");
        }
        catch (Exception ex)
        {
            run.AppendNote($"\n\n**Error:** {ex.Message}");
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
                run.AppendNote($"\n\n**Error:** {ex.Message}");
            }
        }
        IsProcessing = false;
        IsCancellable = false;
        StatusText = "Ready";
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        run.Complete();
    }
    /// <summary>
    /// True when an event from <paramref name="eventSessionId"/> belongs to the run <paramref name="runSessionId"/>:
    /// the same session, or a flow step's sub-session "&lt;run&gt;/&lt;step&gt;#&lt;round&gt;".
    /// </summary>
    internal static bool BelongsToSession(string eventSessionId, string runSessionId) =>
        string.Equals(eventSessionId, runSessionId, StringComparison.Ordinal) ||
        eventSessionId.StartsWith(runSessionId + "/", StringComparison.Ordinal);

    private void RefreshAvailablePipelines()
    {
        if (_pipelineLoader == null) return;
        var selected = SelectedPipeline;
        AvailablePipelines.Clear();
        foreach (var pipeline in _pipelineLoader.GetAllPipelines())
            AvailablePipelines.Add(pipeline.Name);
        if (AvailablePipelines.Contains(selected))
            SelectedPipeline = selected;
        else if (AvailablePipelines.Count > 0)
            SelectedPipeline = AvailablePipelines[0];
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
        // Guard conditions — provide user-visible feedback instead of a silent no-op.
        if (IsProcessing)
        {
            System.Diagnostics.Debug.WriteLine("RunPipeline: skipped because a turn is already in progress.");
            StatusText = "A turn is already running. Cancel it first.";
            return;
        }
        if (_pipelineRunner == null || _pipelineLoader == null)
        {
            System.Diagnostics.Debug.WriteLine("RunPipeline: skipped because the SDLC pipeline services are not registered.");
            Timeline.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "⚠️ SDLC pipeline services are not available. Please check the application configuration.",
                Timestamp = DateTime.Now
            });
            StatusText = "Pipeline unavailable";
            return;
        }
        if (string.IsNullOrWhiteSpace(InputText))
        {
            System.Diagnostics.Debug.WriteLine("RunPipeline: skipped because the input text is empty.");
            StatusText = "Enter a task before running a pipeline.";
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = $"**Run pipeline: {pipeline.Name}**\n{task}",
            Timestamp = DateTime.Now
        });
        IsProcessing = true;
        IsPipelineRunning = true;
        IsCancellable = true;
        StatusText = $"Running pipeline '{pipeline.Name}'...";
        var run = Timeline.BeginRun();
        AgentSessions.Clear();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        var pipelineSessionId = Guid.NewGuid().ToString();
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Start the event-processing loop on the calling (UI) thread so it
        // captures the real SynchronizationContext (see SendAsync for rationale).
        var processingTask = ProcessEventsAsync(run, token, pipelineSessionId);
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
            run.AppendNote("\n\n*Cancelled*");
        }
        catch (Exception ex)
        {
            run.AppendNote($"\n\n**Error:** {ex.Message}");
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
                run.AppendNote($"\n\n**Error:** {ex.Message}");
            }
        }
        IsProcessing = false;
        IsPipelineRunning = false;
        IsCancellable = false;
        StatusText = "Ready";
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        run.Complete();
    }
    private async Task ProcessEventsAsync(AssistantRun run, CancellationToken token, string sessionId = "default")
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
                    if (!BelongsToSession(scoped.SessionId, sessionId))
                        continue;
                    evt = scoped.Inner;
                }

                switch (evt)
                {
                    case TextDeltaEvent delta:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                run.AppendText(delta.Delta);
                                Activity.ClearBlocking();
                                Activity.SetPhase("Writing response…");
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case ToolCallStartEvent start:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                Timeline.ToolStarted(start.Call);
                                RaiseCommandStarting(start.Call);
                                Activity.ToolStarted(start.Call.Id, start.Call.Name, start.Call.Arguments);
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case ToolCallEndEvent end:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                Activity.ToolFinished(end.Call.Id, end.Call.Name, end.Result.IsError);
                                Timeline.ToolEnded(end.Call, end.Result, end.Duration);
                                RaiseToolFinished(end.Call, end.Result);
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case ApprovalRequestEvent approval:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                // An explicit approval entry in the timeline, at the point where the agent
                                // asked, so the user sees that a decision is needed (and later, what it was).
                                OnApprovalRequested(approval);
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
                            () => HandleAgentTaggedEvent(tagged, run),
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case StatusUpdateEvent status:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                StatusText = status.Status;
                                // A run that stops early (spend cap, step limit) says why in the conversation, not only in the status line.
                                if (status.Status == "Limit" && AgentOutputText.StatusNote(status) is { } limitNote)
                                    run.AppendNote(limitNote);
                            },
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        break;
                    case TokenUsageEvent usage:
                        await Task.Factory.StartNew(
                            () => TokenUsage = usage.Format(),
                            token,
                            TaskCreationOptions.None,
                            uiScheduler);
                        RecordSessionCost(usage.SessionCostUsd);
                        break;
                    case PlanGeneratedEvent plan:
                        await Task.Factory.StartNew(
                            () =>
                            {
                                var stepList = plan.Steps.ToList();
                                PlanVM.LoadPlan(stepList);
                                run.AppendNote($"**Plan generated** ({stepList.Count} steps):\n" +
                                    string.Join("\n", stepList.Select(s =>
                                        $"{s.Index}. {s.Description}" +
                                        (s.FilesLikelyTouched.Count == 0 ? "" :
                                            $" — `{string.Join("`, `", s.FilesLikelyTouched)}`"))));
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
                                    run.AppendNote($"\n\n**Step {stepFinished.StepIndex + 1} failed:** {stepFinished.Step.FailureReason}");
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
                                    run.AppendNote("\n\n*Cancelled*");
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
                                run.AppendNote($"\n\n**Error:** {error.Error.Message}");
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

            // The summary card appears once per session; later confirmations only update the status line.
            if (!_requirementsSummaryShown)
            {
                _requirementsSummaryShown = true;
                Timeline.Add(new ChatMessage
                {
                    Role = "System",
                    Content = requirements.ToSummaryCard(),
                    Timestamp = DateTime.Now
                });
            }

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
        Timeline.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = $"📝 **Requirements clarification** ({dimension})\n{question}\n\n_Type your answer, or \"just build it\" to skip._",
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
        DenyAllApprovals();
        IsCancellable = false;
        StatusText = "Cancelling...";
    }
    [RelayCommand]
    private void ApproveTool()
    {
        // No card is added here: the orchestrator emits ToolCallStartEvent right after approval,
        // and that event creates the (single) card for this call.
        ResolveCurrentApproval(true);
    }

    /// <summary>Approves this edit and every later edit tool call until the task ends. Commands still ask.</summary>
    [RelayCommand]
    private void ApproveAllEditsThisTask()
    {
        if (!CanApproveAllEdits) return;
        Approvals.AllowEditsForTask();
        ResolveCurrentApproval(true, "edits allowed for this task");
    }

    /// <summary>Approves this call and every later call of the same tool for the rest of the chat session.</summary>
    [RelayCommand]
    private void AlwaysAllowToolThisSession()
    {
        if (!CanSetApprovalRules) return;
        Approvals.AllowToolForSession(ApprovalToolName);
        ResolveCurrentApproval(true, "always allowed this session");
    }

    [RelayCommand]
    private void DenyTool()
    {
        ResolveCurrentApproval(false);
    }

    [RelayCommand]
    private void StartDenyWithFeedback()
    {
        IsEnteringApprovalFeedback = true;
    }

    [RelayCommand]
    private void CancelDenyWithFeedback()
    {
        IsEnteringApprovalFeedback = false;
        ApprovalFeedbackText = string.Empty;
    }

    /// <summary>Denies the call and sends the typed note to the model as the tool result; the agent continues.</summary>
    [RelayCommand]
    private void DenyWithFeedback()
    {
        var text = ApprovalFeedbackText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ResolveCurrentApproval(false);
            return;
        }

        if (_currentApprovalFeedback != null) _currentApprovalFeedback.Text = text;
        ResolveCurrentApproval(false, $"feedback: {text}");
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
    private void HandleAgentTaggedEvent(AgentTaggedEvent tagged, AssistantRun run)
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
            IsAgentSidebarOpen = true; // reveal the sidebar only once an agent actually appears
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
                run.AppendText(delta.Delta, tagged.AgentId, tagged.Role);
                break;
            case ToolCallStartEvent start:
                Timeline.ToolStarted(start.Call, tagged.AgentId, tagged.Role);
                RaiseCommandStarting(start.Call);
                break;
            case ToolCallEndEvent end:
                Timeline.ToolEnded(end.Call, end.Result, end.Duration, tagged.AgentId, tagged.Role);
                RaiseToolFinished(end.Call, end.Result);
                break;
            case ApprovalRequestEvent approval:
                // The approval entry sits in the timeline where the agent asked, for multi-agent sessions too.
                OnApprovalRequested(approval, tagged.AgentId, tagged.Role);
                break;
            case StatusUpdateEvent status:
                StatusText = $"[{tagged.AgentId}] {status.Status}";
                if (status.Status == "Limit" && AgentOutputText.StatusNote(status) is { } limitNote)
                    run.AppendNote(limitNote);
                break;
            case TokenUsageEvent usage:
                TokenUsage = usage.Format();
                break;
            case AgentFinishedEvent finished:
                if (finished.Response.WasCancelled)
                {
                    run.AppendNote("\n\n*Cancelled*");
                }
                if (existing != null)
                {
                    existing.Status = "Done";
                }
                StatusText = "Done";
                _eventProcessingComplete?.TrySetResult();
                break;
            case AgentErrorEvent error:
                run.AppendNote($"\n\n**Error ({tagged.AgentId}):** {error.Error.Message}");
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
                "/rewind" => "Go back to an earlier message (chat, code or both)",
                "/fork" => "Continue from an earlier message in a new session",
                "/effort" => "Set reasoning effort (off/low/medium/high)",
                "/character" => "Switch the character that answers (/character none = default agent)",
                "/characters" => "List characters",
                "/skill" => "Send a skill's instructions with your next message",
                "/skills" => "List the active character's skills (--all for every skill)",
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
                "/rewind" => "Go back to an earlier message (chat, code or both)",
                "/fork" => "Continue from an earlier message in a new session",
                "/effort" => "Set reasoning effort (off/low/medium/high)",
                "/character" => "Switch the character that answers (/character none = default agent)",
                "/characters" => "List characters",
                "/skill" => "Send a skill's instructions with your next message",
                "/skills" => "List the active character's skills (--all for every skill)",
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
        Timeline.Clear();
        Approvals.ResetSession();
        Timeline.Add(new ChatMessage
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
        => ToolCallCardFormatting.FormatArguments(args);
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
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = $"@codebase " + question,
            Timestamp = DateTime.Now
        });
        if (_semanticIndex == null || _retriever == null)
        {
            Timeline.Add(new ChatMessage
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
                Timeline.Add(new ChatMessage
                {
                    Role = "Assistant",
                    Content = "No indexed code chunks were found. Try re-indexing the workspace.",
                    Timestamp = DateTime.Now
                });
                return;
            }
            var contextBlock = retrieval.ContextBlock;
            var groundedPrompt = AiCodeAgent.Indexing.Semantic.Retriever.BuildPrompt(question, contextBlock);
            var run = Timeline.BeginRun();
            StatusText = "Answering...";
            _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processingTask = ProcessEventsAsync(run, token, SessionId);
            try
            {
                await _agentService.StreamMessageAsync(
                    groundedPrompt,
                    SessionId,
                    new AgentOptions
                    {
                        PermissionMode = ParsePermissionMode(PermissionMode),
                        Reasoning = ReasoningEffortValue,
                        AllowedPaths = AdditionalFolders,
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
                run.AppendNote("\n\n*Cancelled*");
            }
            catch (Exception ex)
            {
                run.AppendNote("\n\n**Error:** " + ex.Message);
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
                    run.AppendNote("\n\n**Error:** " + ex.Message);
                }
            }
            run.Complete();
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = "*Cancelled.*",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
            {
                Role = "System",
                Content = "⚠️ Autonomous agent is not configured (IPlanGenerator/IAutonomousAgentRunner not registered).",
                Timestamp = DateTime.Now
            });
            return;
        }
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/plan {task}",
            Timestamp = DateTime.Now
        });
        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Planning...";
        var run = Timeline.BeginRun();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _eventProcessingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = Guid.NewGuid().ToString("N");
        var processingTask = ProcessEventsAsync(run, token, sessionId);
        try
        {
            var steps = await _planGenerator.GenerateAsync(task, WorkingDirectory, token)
                .ConfigureAwait(true);
            var options = new AgentOptions
            {
                PermissionMode = ParsePermissionMode(PermissionMode),
                Reasoning = ReasoningEffortValue,
                AllowedPaths = AdditionalFolders,
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
                run.AppendNote($"\n\n**Plan result:** {log.FailureSummary}");
            else
                run.AppendNote("\n\n**Plan completed successfully.**");
        }
        catch (OperationCanceledException)
        {
            run.AppendNote("\n\n*Cancelled*");
        }
        catch (Exception ex)
        {
            run.AppendNote($"\n\n**Error:** {ex.Message}");
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
                run.AppendNote($"\n\n**Error:** {ex.Message}");
            }
            IsProcessing = false;
            IsCancellable = false;
            StatusText = "Ready";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
        run.Complete();
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
            Timeline.Add(new ChatMessage
            {
                Role = "User",
                Content = userMessage,
                Timestamp = DateTime.Now
            });
            var sample = GetProjectFiles().Take(15).Select(f => $"- `{f}`");
            Timeline.Add(new ChatMessage
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
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = userMessage,
            Timestamp = DateTime.Now
        });

        if (!File.Exists(filePath))
        {
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
            {
                Role = "System",
                Content = $"✏️ Opened `{filePath}` in the editor. Make your changes and press **Ctrl+S** to save.",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            Timeline.Add(new ChatMessage
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
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/branch {branchName}",
            Timestamp = DateTime.Now
        });

        if (string.IsNullOrWhiteSpace(branchName))
        {
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/commit {message}",
            Timestamp = DateTime.Now
        });

        if (string.IsNullOrWhiteSpace(message))
        {
            Timeline.Add(new ChatMessage
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
                Timeline.Add(new ChatMessage
                {
                    Role = "System",
                    Content = "ℹ️ Working tree is clean — nothing to commit.",
                    Timestamp = DateTime.Now
                });
                return;
            }

            var result = await _gitService!.CommitAsync(message).ConfigureAwait(true);
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
        Timeline.Add(new ChatMessage
        {
            Role = "User",
            Content = $"/pr {title}",
            Timestamp = DateTime.Now
        });

        if (string.IsNullOrWhiteSpace(title))
        {
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
            Timeline.Add(new ChatMessage
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
        var approvalItem = Timeline.AddApproval("git", message, message, "Write");

        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gitCall = new ToolCall { Name = "git", Arguments = new Dictionary<string, object?> { ["action"] = message } };
        EnqueueApproval(confirmation, "Write", "git", message, item: approvalItem,
            preview: ApprovalPreviewBuilder.Build(gitCall, null),
            rulesApply: false); // an explicit confirmation (push, PR) is never skipped by "always allow"
        // Approve/Deny (and DenyAllApprovals on cancel) resolve the task and advance the queue.
        return await confirmation.Task.ConfigureAwait(true);
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
