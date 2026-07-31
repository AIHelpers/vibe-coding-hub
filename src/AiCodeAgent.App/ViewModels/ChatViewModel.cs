using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AgentService _agentService;
    private readonly IAgentEventBus _eventBus;
    private CancellationTokenSource? _cancellationTokenSource;

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

    private TaskCompletionSource<bool>? _pendingApproval;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ToolCallCard> ToolCallCards { get; } = new();

    public ChatViewModel(AgentService agentService)
    {
        _agentService = agentService;
        _eventBus = agentService.EventBus;

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
                      "- ⚡ Cancel anytime with Esc or the Cancel button"
        });
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsProcessing)
            return;

        var userMessage = InputText.Trim();
        InputText = string.Empty;

        // Add user message
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = userMessage
        });

        IsProcessing = true;
        IsCancellable = true;
        StatusText = "Processing...";

        // Create assistant message placeholder
        var assistantMessage = new ChatMessage
        {
            Role = "Assistant",
            Content = string.Empty
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
                    PermissionMode = ParsePermissionMode(PermissionMode)
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

    private async Task ProcessEventsAsync(ChatMessage assistantMessage, CancellationToken token)
    {
        try
        {
            await foreach (var evt in _eventBus.GetEventsAsync(token))
            {
                switch (evt)
                {
                    case TextDeltaEvent delta:
                        // Append text to assistant message
                        assistantMessage.Content += delta.Delta;
                        break;

                    case ToolCallStartEvent start:
                        // Add tool call card
                        var card = new ToolCallCard
                        {
                            ToolName = start.Call.Name,
                            Arguments = FormatArguments(start.Call.Arguments),
                            IsExpanded = false
                        };
                        ToolCallCards.Add(card);
                        break;

                    case ToolCallEndEvent end:
                        // Update tool call card with result
                        var existingCard = ToolCallCards.FirstOrDefault(c => c.ToolName == end.Call.Name);
                        if (existingCard != null)
                        {
                            existingCard.Output = TruncateOutput(end.Result.Content, 500);
                            existingCard.Duration = end.Duration;
                            existingCard.IsError = end.Result.IsError;
                        }
                        break;

                    case ApprovalRequestEvent approval:
                        // Show approval dialog on UI thread
                        ShowApprovalDialog = true;
                        ApprovalToolName = approval.Call.Name;
                        ApprovalArgs = FormatArguments(approval.Call.Arguments);
                        _pendingApproval = approval.Approval;
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

        // Add to allowlist 
        if (!string.IsNullOrEmpty(ApprovalToolName))
        {
            ToolCallCards.Add(new ToolCallCard
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

    private static string FormatArguments(System.Collections.Generic.Dictionary<string, object?> args)
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

public class ChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}