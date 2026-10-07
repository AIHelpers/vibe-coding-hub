using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IAiProvider _provider;
    private readonly IContextManager _contextManager;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly AgentConfiguration _config;
    private readonly IPermissionService _permissionService;
    private readonly ICheckpointManager _checkpointManager;
    private readonly IAgentEventBus? _eventBus;
    private readonly IContextUsageTracker? _usageTracker;
    private readonly IContextCompactor? _compactor;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly IHookRunner? _hookRunner;
    private readonly IPermissionManager? _permissionManager;
    private readonly Usage.SessionCostTracker? _costTracker;

    public AgentOrchestrator(
        IAiProvider provider,
        IContextManager contextManager,
        IToolRegistry toolRegistry,
        AgentConfiguration config,
        ILogger<AgentOrchestrator> logger,
        IPermissionService permissionService,
        ICheckpointManager checkpointManager,
        IAgentEventBus? eventBus = null,
        IContextUsageTracker? usageTracker = null,
        IContextCompactor? compactor = null,
        ISkillRegistry? skillRegistry = null,
        IHookRunner? hookRunner = null,
        IPermissionManager? permissionManager = null,
        Usage.SessionCostTracker? costTracker = null)
    {
        _provider = provider;
        _contextManager = contextManager;
        _toolRegistry = toolRegistry;
        _config = config;
        _logger = logger;
        _permissionService = permissionService;
        _checkpointManager = checkpointManager;
        _eventBus = eventBus;
        _usageTracker = usageTracker;
        _compactor = compactor;
        _skillRegistry = skillRegistry;
        _hookRunner = hookRunner;
        _permissionManager = permissionManager;
        _costTracker = costTracker;
    }

    /// <summary>
    /// Publishes an event scoped to <paramref name="sessionId"/> — every
    /// event this orchestrator emits goes through here (never
    /// <c>_eventBus.Publish</c> directly) so listeners on the shared bus can
    /// tell which session/task an event belongs to. See
    /// <see cref="SessionScopedEvent"/>.
    /// </summary>
    private void PublishScoped(string sessionId, AgentEvent evt) =>
        _eventBus?.Publish(new SessionScopedEvent(sessionId, evt));

    public async Task<AgentResponse> RunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in StreamRunAsync(userMessage, sessionId, options, cancellationToken))
            events.Add(evt);

        var toolEnds = events.OfType<ToolCallEndEvent>().ToList();
        var errorEvt = events.OfType<AgentErrorEvent>().FirstOrDefault();
        if (errorEvt != null)
            throw errorEvt.Error;

        var finished = events.OfType<AgentFinishedEvent>().LastOrDefault()
                       ?? throw new InvalidOperationException("No agent finished event");

        return finished.Response;
    }

    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var execContext = new AgentExecutionContext
        {
            SessionId = sessionId,
            WorkingDirectory = options.WorkingDirectory,
            Permissions = new PermissionSettings { Mode = options.PermissionMode },
            AgentId = options.AgentId,
            Role = options.Role,
            CharacterId = options.CharacterId,
            AllowedSkills = BuildAllowedSkillSet(options),
            IsReadOnly = options.IsReadOnly,
            AllowedPaths = BuildAllowedPaths(options),
            CancellationToken = cancellationToken
        };

        var contextId = string.IsNullOrEmpty(options.ContextSessionId) ? sessionId : options.ContextSessionId;

        // NOTE: we deliberately do NOT call _permissionService.SetMode() here.
        // PermissionService used to be a singleton with one global
        // _currentMode; every run overwrote it, so a background task started
        // in FullAuto could flip the mode for the main chat mid-run (issue 4).
        // The mode now travels per-call on AgentOptions.PermissionMode and is
        // read directly wherever a decision is made, below and in
        // PermissionService.RequestApprovalAsync — no shared mutable state.

        // Emit status update
        var statusEvent = new StatusUpdateEvent("Processing", "Starting agent loop");
        yield return statusEvent;
        PublishScoped(sessionId, statusEvent);

        // Run PreSessionStart hooks
        if (_hookRunner is not null)
        {
            var preSessionResult = await _hookRunner.RunAsync(HookEvent.PreSessionStart, new HookContext
            {
                Event = HookEvent.PreSessionStart,
                SessionId = sessionId,
                WorkingDirectory = options.WorkingDirectory
            }, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(preSessionResult.CombinedOutput))
            {
                var hookEvent = new StatusUpdateEvent("Hook", preSessionResult.CombinedOutput);
                yield return hookEvent;
                PublishScoped(sessionId, hookEvent);
            }
        }

        // Add user message to context (with any attached images — see
        // AgentOptions.Images — so vision-capable providers can see
        // screenshots/annotations alongside the text instruction).
        await _contextManager.AddMessageAsync(contextId, new Message
        {
            Role = MessageRole.User,
            Content = userMessage,
            Images = options.Images
        }).ConfigureAwait(false);

        var tools = _toolRegistry.GetTools(options.EnabledTools) ?? new List<ITool>();

        // In Plan mode, filter out non-Read tools so the model is never
        // tempted to call write/execute tools (which would all fail).
        if (options.PermissionMode == PermissionMode.Plan)
        {
            tools = tools.Where(t => t.Risk == RiskLevel.Read).ToList();
        }
        if (options.DisabledTools.Count > 0)
        {
            tools = tools.Where(t => !options.DisabledTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        tools = await ApplySkillToolAsync(tools, options).ConfigureAwait(false);

        // The registry's GetTool() is global, so the model could call any
        // registered tool by name even if it was never offered. When the
        // offered set was narrowed (enabled list, Plan mode, disabled list)
        // execution is restricted to exactly that set.
        HashSet<string>? offeredToolNames = null;
        if (options.EnabledTools.Count > 0 || options.DisabledTools.Count > 0 || options.PermissionMode == PermissionMode.Plan)
        {
            offeredToolNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        }
        var totalUsage = new TokenUsage();
        var toolExecutions = new List<ToolExecution>();
        var fullContent = new StringBuilder();
        var startTime = DateTime.UtcNow;
        var iteration = 0;
        var turnId = Guid.NewGuid().ToString("N")[..12];
        var consecutiveAllErrorIterations = 0;
        const int MaxConsecutiveAllErrorIterations = 3;
        var finishedNaturally = false;
        string? stopReason = null;
        var budgetExceeded = false;

        while (iteration < options.MaxIterations && !cancellationToken.IsCancellationRequested)
        {
            iteration++;

            // Spend cap: checked before every model call, so a run can overshoot by at most one response.
            if (budgetExceeded)
            {
                stopReason = "budget_exceeded";
                break;
            }

            await _contextManager.TrimContextAsync(contextId, options.MaxTokens).ConfigureAwait(false);
            var messages = await _contextManager.GetContextAsync(contextId).ConfigureAwait(false) ?? new List<Message>();

            // Auto-compaction: when the usage tracker reports we've crossed
            // the soft limit, compact older messages before sending the next
            // request so we don't overflow the model's context window.
            if (_usageTracker is not null && _compactor is not null)
            {
                await _usageTracker.RefreshAsync(contextId).ConfigureAwait(false);
                if (_usageTracker.ShouldCompact(contextId))
                {
                    var autoCompactOptions = new CompactionOptions
                    {
                        TargetTokens = Math.Max(1, options.MaxTokens / 2),
                        KeepRecentMessages = 6,
                        Focus = userMessage
                    };

                    CompactionResult? autoResult = null;
                    try
                    {
                        autoResult = await _compactor.CompactAsync(contextId, autoCompactOptions, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto-compaction failed for session {SessionId}", sessionId);
                    }

                    if (autoResult is { Compacted: true })
                    {
                        var compactedEvent = new ContextCompactedEvent(
                            autoResult.MessagesBefore,
                            autoResult.MessagesAfter,
                            autoResult.TokensSaved,
                            autoResult.Focus);
                        yield return compactedEvent;
                        PublishScoped(sessionId, compactedEvent);

                        // Refresh messages after compaction
                        messages = await _contextManager.GetContextAsync(contextId).ConfigureAwait(false) ?? new List<Message>();
                    }
                }
            }

            var request = new CompletionRequest
            {
                Messages = messages,
                Tools = tools.Select(t => t.Definition).ToList(),
                SystemPrompt = await BuildSystemPromptAsync(options).ConfigureAwait(false),
                Options = new CompletionOptions
                {
                    Model = options.Model,
                    Reasoning = options.Reasoning,
                    Stream = true,
                    MaxTokens = options.MaxCompletionTokens > 0 ? options.MaxCompletionTokens : 8192
                }
            };

            var currentContent = new StringBuilder();
            List<ToolCall>? pendingToolCalls = null;
            List<ThinkingBlock>? thinkingBlocks = null;

            var wasCancelled = false;

            // Use an explicit enumerator so we can yield text deltas immediately
            // as they arrive (instead of buffering them until the stream completes).
            // C# forbids yield inside a try-catch, but allows it inside the
            // try-finally that 'await using' generates.
            await using var streamEnumerator = _provider
                .StreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                bool hasMore;
                try
                {
                    hasMore = await streamEnumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                    break;
                }

                if (!hasMore) break;

                var chunk = streamEnumerator.Current;
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    currentContent.Append(chunk.Delta);
                    fullContent.Append(chunk.Delta);
                    var textEvent = new TextDeltaEvent(chunk.Delta);
                    yield return textEvent;
                    PublishScoped(sessionId, textEvent);
                }

                if (!string.IsNullOrEmpty(chunk.ThinkingDelta))
                {
                    var thinkingEvent = new ThinkingEvent(chunk.ThinkingDelta);
                    yield return thinkingEvent;
                    PublishScoped(sessionId, thinkingEvent);
                }

                if (chunk.ThinkingBlocks is { Count: > 0 })
                    thinkingBlocks = chunk.ThinkingBlocks;

                if (chunk.Usage != null)
                {
                    totalUsage = new TokenUsage
                    {
                        PromptTokens = totalUsage.PromptTokens + chunk.Usage.PromptTokens,
                        CompletionTokens = totalUsage.CompletionTokens + chunk.Usage.CompletionTokens,
                        CacheReadTokens = totalUsage.CacheReadTokens + chunk.Usage.CacheReadTokens,
                        CacheCreationTokens = totalUsage.CacheCreationTokens + chunk.Usage.CacheCreationTokens
                    };
                    if (_costTracker is not null)
                    {
                        var snapshot = _costTracker.Record(sessionId, options.Model, chunk.Usage);
                        if (options.AgentId is not null)
                            _costTracker.Record("agent:" + options.AgentId, options.Model, chunk.Usage);
                        if (options.MaxBudgetUsd is > 0 && snapshot.CostUsd >= options.MaxBudgetUsd.Value)
                            budgetExceeded = true;
                    }
                    var usageEvent = new TokenUsageEvent(totalUsage,
                        _costTracker?.Get(sessionId).CostUsd ?? 0m);
                    PublishScoped(sessionId, usageEvent);
                }

                if (chunk.IsFinished)
                {
                    pendingToolCalls = chunk.ToolCalls;
                    break;
                }
            }

            if (wasCancelled)
            {
                var finishedEvent = new AgentFinishedEvent(new AgentResponse
                {
                    Content = fullContent.ToString(),
                    ToolExecutions = toolExecutions,
                    TotalUsage = totalUsage,
                    Duration = DateTime.UtcNow - startTime,
                    WasCancelled = true
                });
                yield return finishedEvent;
                PublishScoped(sessionId, finishedEvent);
                yield break;
            }

            // Save assistant message
            var assistantMessage = new Message
            {
                Role = MessageRole.Assistant,
                Content = currentContent.ToString(),
                ToolCalls = pendingToolCalls,
                ThinkingBlocks = thinkingBlocks
            };
            await _contextManager.AddMessageAsync(contextId, assistantMessage).ConfigureAwait(false);

            // If no tool calls, we're done
            if (pendingToolCalls == null || pendingToolCalls.Count == 0)
            {
                finishedNaturally = true;
                break;
            }

            // Execute tool calls
            foreach (var toolCall in pendingToolCalls)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (string.IsNullOrWhiteSpace(toolCall.Name))
                {
                    _logger.LogWarning("Received tool call with empty name, skipping");
                    var emptyNameResult = new ToolResult
                    {
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name ?? string.Empty,
                        Content = "Tool call had an empty name and could not be executed.",
                        IsError = true
                    };
                    var emptyNameDuration = TimeSpan.Zero;
                    toolExecutions.Add(new ToolExecution { Call = toolCall, Result = emptyNameResult, Duration = emptyNameDuration });
                    var emptyNameEndEvent = new ToolCallEndEvent(toolCall, emptyNameResult, emptyNameDuration);
                    yield return emptyNameEndEvent;
                    PublishScoped(sessionId, emptyNameEndEvent);

                    await _contextManager.AddMessageAsync(contextId, new Message
                    {
                        Role = MessageRole.Tool,
                        Content = FormatToolResultForModel(toolCall, emptyNameResult),
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name
                    }).ConfigureAwait(false);
                    continue;
                }

                var tool = _toolRegistry.GetTool(toolCall.Name);
                var notOffered = tool != null && offeredToolNames != null && !offeredToolNames.Contains(tool.Name);
                if (tool == null || notOffered)
                {
                    _logger.LogWarning("Unknown or unavailable tool requested: {ToolName}", toolCall.Name);
                    var result = new ToolResult
                    {
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        Content = notOffered
                            ? $"Unknown tool: {toolCall.Name} (not available in this session)"
                            : $"Unknown tool: {toolCall.Name}",
                        IsError = true
                    };
                    var duration = TimeSpan.Zero;
                    toolExecutions.Add(new ToolExecution { Call = toolCall, Result = result, Duration = duration });
                    var endEvent = new ToolCallEndEvent(toolCall, result, duration);
                    yield return endEvent;
                    PublishScoped(sessionId, endEvent);

                    await _contextManager.AddMessageAsync(contextId, new Message
                    {
                        Role = MessageRole.Tool,
                        Content = FormatToolResultForModel(toolCall, result),
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name
                    }).ConfigureAwait(false);
                    continue;
                }

                // Check permissions before executing
                var statusEvent2 = new StatusUpdateEvent($"Requesting approval for {toolCall.Name}");
                yield return statusEvent2;
                PublishScoped(sessionId, statusEvent2);

                // For write/execute operations, check permissions
                if (tool.Risk != RiskLevel.Read)
                {
                    // Feature 10: consult the permission manager first. When it
                    // returns Allow we skip the approval dialog entirely; when
                    // Deny we short-circuit with a tool error; when Ask we fall
                    // back to the existing approval flow.
                    PermissionDecision? managerDecision = null;
                    if (options.IsReadOnly)
                    {
                        // A read-only session can never write or execute, whatever
                        // the mode, rights or allow-rules say (and even when no
                        // permission manager is registered).
                        managerDecision = PermissionDecision.Deny;
                    }
                    else if (_permissionManager is not null)
                    {
                        managerDecision = await _permissionManager
                            .CanExecuteAsync(toolCall, tool.Risk, options, options.AgentId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    bool isApproved;
                    if (managerDecision == PermissionDecision.Allow)
                    {
                        isApproved = true;
                    }
                    else if (managerDecision == PermissionDecision.Deny)
                    {
                        // Build a denied result and continue.
                        var mgrDeniedResult = new ToolResult
                        {
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = $"Permission denied by permission manager (mode-based decision). {toolCall.Name} not executed.",
                            IsError = true
                        };
                        toolExecutions.Add(new ToolExecution { Call = toolCall, Result = mgrDeniedResult, Duration = TimeSpan.Zero });
                        var mgrDeniedEndEvent = new ToolCallEndEvent(toolCall, mgrDeniedResult, TimeSpan.Zero);
                        yield return mgrDeniedEndEvent;
                        PublishScoped(sessionId, mgrDeniedEndEvent);

                        await _contextManager.AddMessageAsync(contextId, new Message
                        {
                            Role = MessageRole.Tool,
                            Content = FormatToolResultForModel(toolCall, mgrDeniedResult),
                            ToolCallId = toolCall.Id,
                            Name = toolCall.Name
                        }).ConfigureAwait(false);
                        continue;
                    }
                    else
                    {
                        isApproved = await _permissionService.RequestApprovalAsync(toolCall, tool.Risk, options, options.AgentId);
                    }

                    if (!isApproved)
                    {
                        // In Plan mode, non-Read tools are silently denied
                        // (no approval dialog) so the model gets a tool error
                        // and can continue instead of showing an approval
                        // dialog that would loop forever.
                        var mode = options.PermissionMode;
                        if (mode == PermissionMode.Plan)
                        {
                            var planDeniedResult = new ToolResult
                            {
                                ToolCallId = toolCall.Id,
                                ToolName = toolCall.Name,
                                Content = $"Plan mode: write/execute tools are disabled. {toolCall.Name} not executed.",
                                IsError = true
                            };
                            var planDeniedDuration = TimeSpan.Zero;
                            toolExecutions.Add(new ToolExecution { Call = toolCall, Result = planDeniedResult, Duration = planDeniedDuration });
                            var planDeniedEndEvent = new ToolCallEndEvent(toolCall, planDeniedResult, planDeniedDuration);
                            yield return planDeniedEndEvent;
                            PublishScoped(sessionId, planDeniedEndEvent);

                            await _contextManager.AddMessageAsync(contextId, new Message
                            {
                                Role = MessageRole.Tool,
                                Content = FormatToolResultForModel(toolCall, planDeniedResult),
                                ToolCallId = toolCall.Id,
                                Name = toolCall.Name
                            }).ConfigureAwait(false);
                            continue;
                        }

                        // Nobody can answer a prompt (subagents, unattended runs):
                        // deny right away instead of hanging until the timeout.
                        if (options.NonInteractive)
                        {
                            var niDeniedResult = new ToolResult
                            {
                                ToolCallId = toolCall.Id,
                                ToolName = toolCall.Name,
                                Content = $"{toolCall.Name} requires approval, but this run is non-interactive. Not executed.",
                                IsError = true
                            };
                            toolExecutions.Add(new ToolExecution { Call = toolCall, Result = niDeniedResult, Duration = TimeSpan.Zero });
                            var niDeniedEndEvent = new ToolCallEndEvent(toolCall, niDeniedResult, TimeSpan.Zero);
                            yield return niDeniedEndEvent;
                            PublishScoped(sessionId, niDeniedEndEvent);

                            await _contextManager.AddMessageAsync(contextId, new Message
                            {
                                Role = MessageRole.Tool,
                                Content = FormatToolResultForModel(toolCall, niDeniedResult),
                                ToolCallId = toolCall.Id,
                                Name = toolCall.Name
                            }).ConfigureAwait(false);
                            continue;
                        }

                        // Emit approval request event - UI will handle this
                        var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var approvalEvent = new ApprovalRequestEvent(toolCall, approvalTcs, tool.Risk);
                        yield return approvalEvent;
                        PublishScoped(sessionId, approvalEvent);

                        // Wait for user approval (cancel-aware to avoid hang)
                        using var approvalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        approvalCts.CancelAfter(TimeSpan.FromMinutes(10));
                        var approvalTask = approvalTcs.Task;
                        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, approvalCts.Token);

                        try
                        {
                            await Task.WhenAny(approvalTask, delayTask).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            wasCancelled = true;
                            break;
                        }

                        if (approvalTask.IsCompletedSuccessfully)
                        {
                            isApproved = approvalTask.Result;
                        }
                        else
                        {
                            // Timeout or cancellation
                            isApproved = false;
                            if (approvalCts.IsCancellationRequested && cancellationToken.IsCancellationRequested)
                            {
                                wasCancelled = true;
                                break;
                            }
                        }

                        if (!isApproved)
                        {
                            var deniedResult = new ToolResult
                            {
                                ToolCallId = toolCall.Id,
                                ToolName = toolCall.Name,
                                Content = $"Tool execution denied by user: {toolCall.Name}",
                                IsError = true
                            };
                            var deniedDuration = TimeSpan.Zero;
                            toolExecutions.Add(new ToolExecution { Call = toolCall, Result = deniedResult, Duration = deniedDuration });
                            var deniedEndEvent = new ToolCallEndEvent(toolCall, deniedResult, deniedDuration);
                            yield return deniedEndEvent;
                            PublishScoped(sessionId, deniedEndEvent);

                            await _contextManager.AddMessageAsync(contextId, new Message
                            {
                                Role = MessageRole.Tool,
                                Content = FormatToolResultForModel(toolCall, deniedResult),
                                ToolCallId = toolCall.Id,
                                Name = toolCall.Name
                            }).ConfigureAwait(false);
                            continue;
                        }
                    }
                }

                // Create checkpoint before write operations. The path argument
                // is what the model sent — usually relative — so it must be
                // resolved against the working directory, not the process CWD
                // (issue 8). We also checkpoint unconditionally, even when the
                // target file doesn't exist yet, so Undo can remove a file the
                // agent is about to create (CheckpointManager records
                // ExistedBeforeCheckpoint=false for that case).
                if (tool.Risk == RiskLevel.Write && toolCall.Arguments.TryGetValue("path", out var pathObj) && pathObj != null)
                {
                    var rawPath = pathObj.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(rawPath))
                    {
                        var filePath = Path.IsPathRooted(rawPath)
                            ? rawPath
                            : Path.GetFullPath(Path.Combine(options.WorkingDirectory, rawPath));

                        // Create checkpoint without yield in try-catch
                        CheckpointCreatedEvent? checkpointEvent = null;
                        try
                        {
                            var checkpoint = await _checkpointManager.CreateCheckpointAsync(filePath, turnId, sessionId);
                            checkpointEvent = new CheckpointCreatedEvent(checkpoint);
                            PublishScoped(sessionId, checkpointEvent);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to create checkpoint for {Path}", filePath);
                        }

                        // Yield the checkpoint event outside the try-catch
                        if (checkpointEvent != null)
                        {
                            yield return checkpointEvent;
                        }
                    }
                }

                // Run PreToolUse hooks (may block)
                var preToolDenied = false;
                ToolResult? preToolDenyResult = null;
                if (_hookRunner is not null)
                {
                    var preToolResult = await _hookRunner.RunAsync(HookEvent.PreToolUse, new HookContext
                    {
                        Event = HookEvent.PreToolUse,
                        SessionId = sessionId,
                        ToolCall = toolCall,
                        WorkingDirectory = options.WorkingDirectory
                    }, cancellationToken).ConfigureAwait(false);

                    if (preToolResult.Denied)
                    {
                        preToolDenied = true;
                        preToolDenyResult = new ToolResult
                        {
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = $"Tool blocked by hook: {preToolResult.CombinedOutput}",
                            IsError = true
                        };
                        toolExecutions.Add(new ToolExecution { Call = toolCall, Result = preToolDenyResult, Duration = TimeSpan.Zero });
                        var denyEndEvent = new ToolCallEndEvent(toolCall, preToolDenyResult, TimeSpan.Zero);
                        yield return denyEndEvent;
                        PublishScoped(sessionId, denyEndEvent);
                        // NOTE: the tool_result message for the model is added exactly
                        // once, by the common path below — adding it here too would
                        // create a duplicate tool_result with the same ToolCallId,
                        // which providers reject.
                    }
                }

                ToolResult toolResult;
                var toolStart = DateTime.UtcNow;

                if (!preToolDenied)
                {
                    // Execute tool
                    var startEvent = new ToolCallStartEvent(toolCall);
                    yield return startEvent;
                    PublishScoped(sessionId, startEvent);

                    var statusEvent3 = new StatusUpdateEvent($"Executing {toolCall.Name}");
                    yield return statusEvent3;
                    PublishScoped(sessionId, statusEvent3);

                    try
                    {
                        toolResult = await tool.ExecuteAsync(toolCall, execContext).ConfigureAwait(false);
                        toolResult = toolResult with { ToolCallId = toolCall.Id };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Tool execution failed: {Tool}", toolCall.Name);
                        toolResult = new ToolResult
                        {
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = $"Tool error: {ex.Message}",
                            IsError = true
                        };
                    }

                    var execDuration = DateTime.UtcNow - toolStart;
                    toolExecutions.Add(new ToolExecution { Call = toolCall, Result = toolResult, Duration = execDuration });
                    var toolEndEvent = new ToolCallEndEvent(toolCall, toolResult, execDuration);
                    yield return toolEndEvent;
                    PublishScoped(sessionId, toolEndEvent);

                    // Run PostToolUse hooks
                    if (_hookRunner is not null)
                    {
                        var postToolResult = await _hookRunner.RunAsync(HookEvent.PostToolUse, new HookContext
                        {
                            Event = HookEvent.PostToolUse,
                            SessionId = sessionId,
                            ToolCall = toolCall,
                            ToolResult = toolResult,
                            WorkingDirectory = options.WorkingDirectory
                        }, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(postToolResult.CombinedOutput))
                        {
                            var hookStatus = new StatusUpdateEvent("Hook", postToolResult.CombinedOutput);
                            yield return hookStatus;
                            PublishScoped(sessionId, hookStatus);
                        }
                    }
                }
                else
                {
                    toolResult = preToolDenyResult ?? new ToolResult
                    {
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        Content = "Tool blocked by hook",
                        IsError = true
                    };
                }

                // Emit diff event for write operations (single-agent mode).
                // toolResult.Content is the tool's human-readable message, not a
                // diff — build the real thing from the FileWriteResult the
                // file-writing tools attach to ToolResult.Data.
                if (tool.Risk == RiskLevel.Write && !toolResult.IsError)
                {
                    var filePath = toolCall.Arguments.TryGetValue("path", out var p) ? p?.ToString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrEmpty(filePath) && toolResult.Data is FileWriteResult fileWrite)
                    {
                        var diffEntry = new DiffEntry
                        {
                            FilePath = filePath,
                            OriginalContent = fileWrite.OriginalContent,
                            ModifiedContent = fileWrite.NewContent,
                            DiffText = UnifiedDiffBuilder.Build(fileWrite.OriginalContent, fileWrite.NewContent, filePath),
                            AgentId = options.AgentId
                        };
                        var diffEvent = new DiffProducedEvent(diffEntry);
                        yield return diffEvent;
                        PublishScoped(sessionId, diffEvent);
                    }
                }

                // Add tool result to context
                await _contextManager.AddMessageAsync(contextId, new Message
                {
                    Role = MessageRole.Tool,
                    Content = FormatToolResultForModel(toolCall, toolResult),
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Name
                }).ConfigureAwait(false);
            }

            // A cancel (or approval abort) can leave tool calls from this reply
            // without a tool_result. Providers reject a history with dangling
            // tool_calls on the next request, so answer every one of them.
            {
                var answeredIds = new HashSet<string>(toolExecutions.Select(te => te.Call.Id));
                foreach (var orphan in pendingToolCalls.Where(tc => !answeredIds.Contains(tc.Id)))
                {
                    var cancelledResult = new ToolResult
                    {
                        ToolCallId = orphan.Id,
                        ToolName = orphan.Name ?? string.Empty,
                        Content = "Cancelled before execution.",
                        IsError = true
                    };
                    await _contextManager.AddMessageAsync(contextId, new Message
                    {
                        Role = MessageRole.Tool,
                        Content = FormatToolResultForModel(orphan, cancelledResult),
                        ToolCallId = orphan.Id,
                        Name = orphan.Name
                    }).ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            // If every tool result in this iteration is an error, count it toward a
            // consecutive-all-error streak; only break once that streak reaches a
            // threshold, so a single failed edit_file (e.g. "text not found") doesn't
            // end the run before the model gets a chance to see the error and retry.
            // (toolExecutions accumulates across iterations, so check only this iteration's batch)
            var currentIterationExecutions = toolExecutions
                .Skip(toolExecutions.Count - pendingToolCalls.Count)
                .ToList();
            if (pendingToolCalls.Count > 0 && currentIterationExecutions.Count > 0 &&
                currentIterationExecutions.All(te => te.Result.IsError))
            {
                consecutiveAllErrorIterations++;
                _logger.LogWarning("All {Count} tool calls in iteration {Iteration} returned errors ({Streak}/{Max} consecutive)",
                    currentIterationExecutions.Count, iteration, consecutiveAllErrorIterations, MaxConsecutiveAllErrorIterations);
                if (consecutiveAllErrorIterations >= MaxConsecutiveAllErrorIterations)
                {
                    _logger.LogWarning("{Streak} consecutive all-error iterations — breaking to prevent infinite loop",
                        consecutiveAllErrorIterations);
                    stopReason = "repeated_errors";
                    break;
                }
            }
            else if (pendingToolCalls.Count > 0 && currentIterationExecutions.Count > 0)
            {
                consecutiveAllErrorIterations = 0;
            }
        }

        if (stopReason == null && !finishedNaturally && !cancellationToken.IsCancellationRequested)
            stopReason = "max_iterations";

        // Never end a run silently: tell the user (and the transcript) why it stopped early.
        if (stopReason != null)
        {
            var reasonText = stopReason == "repeated_errors"
                ? "Stopped: every tool call failed several rounds in a row. Check the errors above, then send a follow-up to continue."
                : stopReason == "budget_exceeded"
                ? $"Stopped: this session reached its spend cap of ${options.MaxBudgetUsd:0.00}. Raise the cap to continue."
                : $"Stopped: reached the limit of {options.MaxIterations} steps before the task was complete. Send a follow-up to continue.";
            fullContent.AppendLine().AppendLine().Append(reasonText);
            var limitEvent = new StatusUpdateEvent("Limit", reasonText);
            yield return limitEvent;
            PublishScoped(sessionId, limitEvent);
        }

        var response = new AgentResponse
        {
            Content = fullContent.ToString(),
            ToolExecutions = toolExecutions,
            TotalUsage = totalUsage,
            Duration = DateTime.UtcNow - startTime,
            WasCancelled = cancellationToken.IsCancellationRequested,
            StopReason = stopReason
        };

        var finished = new AgentFinishedEvent(response);
        yield return finished;
        PublishScoped(sessionId, finished);

        // Run PostSessionEnd hooks
        if (_hookRunner is not null)
        {
            await _hookRunner.RunAsync(HookEvent.PostSessionEnd, new HookContext
            {
                Event = HookEvent.PostSessionEnd,
                SessionId = sessionId,
                WorkingDirectory = options.WorkingDirectory
            }, cancellationToken).ConfigureAwait(false);
        }

        var doneEvent = new StatusUpdateEvent("Done", "Agent completed");
        yield return doneEvent;
        PublishScoped(sessionId, doneEvent);
    }

    private static List<string> BuildAllowedPaths(AgentOptions options)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            paths.Add(options.WorkingDirectory);
        foreach (var extra in options.AllowedPaths)
        {
            if (!string.IsNullOrWhiteSpace(extra))
                paths.Add(extra);
        }
        return paths;
    }

    private async Task<string> BuildSystemPromptAsync(AgentOptions options)
    {
        var environmentBlock = $"""
            Working directory: {options.WorkingDirectory}
            Date: {DateTime.UtcNow:yyyy-MM-dd}
            OS: {RuntimeInformation.OSDescription}
            Permission mode: {options.PermissionMode}
            """;
        var extraFolders = options.AllowedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && !string.Equals(p, options.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (extraFolders.Count > 0)
            environmentBlock += "\nAdditional folders added to this task (you may read and edit files there; use absolute paths): " +
                                string.Join("; ", extraFolders);

        var roleBlock = BuildRoleBlock(options);
        var requirementsBlock = options.Requirements?.ToPromptBlock() ?? string.Empty;
        var memoryBlock = options.ProjectMemory?.HasContent == true
            ? options.ProjectMemory.ToPromptBlock()
            : string.Empty;
        var autoMemoryBlock = string.IsNullOrWhiteSpace(options.AutoMemory)
            ? string.Empty
            : $"Learned from earlier turns in this project — apply unless the user says otherwise:\n{options.AutoMemory.Trim()}";
        var skillsBlock = await BuildSkillsBlockAsync(options).ConfigureAwait(false);
        var pinnedSkillsBlock = await BuildPinnedSkillsBlockAsync(options).ConfigureAwait(false);
        var planModeBlock = options.PermissionMode == PermissionMode.Plan
            ? """
              Plan mode is ACTIVE:
              - You may ONLY use read-only tools (read files, search, list directories)
              - Write, execute, and edit tools are DISABLED and will return errors
              - Do NOT attempt write/execute tools; plan the work and present it instead
              - Gather information with read tools, then summarize a plan for the user
              """
            : string.Empty;

        // Structuring the system prompt with XML tags — the same convention
        // Anthropic's own prompts use — gives the model unambiguous
        // boundaries between environment facts, project-specific context,
        // and behavioral guidelines, instead of one undifferentiated block
        // of prose. Each section is omitted entirely when it has nothing to
        // say, so an agent with no role/memory/requirements set still gets a
        // clean prompt rather than empty tags.
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert AI coding assistant with deep knowledge of software development.");
        sb.AppendLine("You have access to tools to read/write files, execute commands, search code, and more.");
        sb.AppendLine();
        sb.Append(WrapSection("environment", environmentBlock));
        sb.Append(WrapSection("role", roleBlock));
        sb.Append(WrapSection("confirmed_requirements_context", requirementsBlock, alreadyTagged: true));
        sb.Append(WrapSection("project_memory_context", memoryBlock, alreadyTagged: true));
        sb.Append(WrapSection("learned_preferences", autoMemoryBlock));
        sb.Append(WrapSection("available_skills", skillsBlock));
        sb.Append(WrapSection("pinned_skills", pinnedSkillsBlock));
        sb.Append(WrapSection("plan_mode_constraints", planModeBlock));
        sb.Append(WrapSection("guidelines", """
            - Always read files before editing them to understand current state
            - Make minimal, targeted changes when fixing bugs
            - Run diagnostics after making changes to verify correctness
            - Explain what you're doing and why
            - If a task is ambiguous, ask for clarification
            - Prefer editing specific code over rewriting entire files
            - Use git to understand history when helpful
            """));
        sb.Append(WrapSection("write_tool_usage", """
            "Write" does not always mean "create a file":
            - When the user asks you to "write a plan", "write an outline",
              "write a summary", "write a description", or similar, they want
              you to produce that content as your chat response (text), NOT to
              call the write_file tool.
            - Only use the write_file/edit tools when the user explicitly asks
              you to create, modify, or save a file (e.g. "create a file
              named X", "save this to a file", "edit the file at path Y").
            - When in doubt, answer in chat and ask before touching the filesystem.
            """));
        sb.Append(WrapSection("code_style_guidelines", """
            - Follow existing code style and conventions
            - Add appropriate error handling
            - Write clean, maintainable code
            - Consider edge cases
            """));

        return sb.ToString().TrimEnd();
    }

    /// <summary>Builds the &lt;role&gt; section body (role name, agent id, custom role instructions).</summary>
    private static string BuildRoleBlock(AgentOptions options)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(options.Role)) lines.Add($"Role: {options.Role}");
        if (!string.IsNullOrEmpty(options.AgentId)) lines.Add($"Agent ID: {options.AgentId}");
        if (!string.IsNullOrWhiteSpace(options.RoleSystemPrompt)) lines.Add(options.RoleSystemPrompt.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Wraps non-empty content in an XML tag for the system prompt. Sections
    /// with nothing to say are omitted entirely rather than emitted as empty
    /// tags. When <paramref name="alreadyTagged"/> is true, the content
    /// already carries its own top-level tag (e.g. a nested
    /// <c>ToPromptBlock()</c> result) — <paramref name="tag"/> is used only
    /// as an outer grouping wrapper in that case.
    /// </summary>
    private static string WrapSection(string tag, string content, bool alreadyTagged = false)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        return alreadyTagged
            ? $"{content.Trim()}\n\n"
            : $"<{tag}>\n{content.Trim()}\n</{tag}>\n\n";
    }

    /// <summary>
    /// Wraps a tool's result in an XML tag before it becomes a "tool" role
    /// message sent back to the model — the same structuring convention
    /// <see cref="BuildSystemPrompt"/> uses for the system prompt, so the
    /// model sees a consistent, unambiguous shape for every kind of
    /// structured content in the conversation. Applied only at this point:
    /// <see cref="ToolResult.Content"/> itself is left untagged so UI
    /// surfaces (tool call cards, the event log, CLI output) keep showing
    /// clean, human-readable text rather than raw XML.
    /// </summary>
    private static string FormatToolResultForModel(ToolCall call, ToolResult result)
    {
        var name = string.IsNullOrEmpty(result.ToolName) ? call.Name : result.ToolName;
        var statusAttr = result.IsError ? " status=\"error\"" : string.Empty;
        // Keep untrusted tool output from closing the wrapper early and
        // masquerading as text outside the tool_result frame.
        var body = (result.Content ?? string.Empty)
            .Replace("</tool_result>", "&lt;/tool_result>", StringComparison.OrdinalIgnoreCase);
        return $"<tool_result name=\"{EscapeXmlAttribute(name)}\"{statusAttr}>\n{body}\n</tool_result>";
    }

    private static string EscapeXmlAttribute(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Name of the tool that loads a skill (see <c>UseSkillTool</c>).</summary>
    public const string UseSkillToolName = "use_skill";

    /// <summary>
    /// Skills the model may see and load for this run: model-visible skills,
    /// narrowed to <see cref="AgentOptions.AllowedSkills"/> when that is set.
    /// </summary>
    private async Task<IReadOnlyList<SkillInfo>> GetAgentSkillsAsync(AgentOptions options)
    {
        if (_skillRegistry is null) return Array.Empty<SkillInfo>();
        try
        {
            var visible = await _skillRegistry.ListAsync(default).ConfigureAwait(false);
            if (options.AllowedSkills is null) return visible;
            var allowed = new HashSet<string>(options.AllowedSkills, StringComparer.OrdinalIgnoreCase);
            return visible.Where(s => allowed.Contains(s.Name)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list skills");
            return Array.Empty<SkillInfo>();
        }
    }

    /// <summary>Allowed skill names carried on the execution context so <c>use_skill</c> can enforce them.</summary>
    private static IReadOnlyCollection<string>? BuildAllowedSkillSet(AgentOptions options)
    {
        if (options.AllowedSkills is null) return null;
        return new HashSet<string>(options.AllowedSkills.Concat(options.PinnedSkills), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Offers <c>use_skill</c> exactly when the agent has skills to load: it is
    /// added even when a role/character tool whitelist omits it (a character's
    /// skills are useless without it), and removed when there is nothing to load.
    /// An explicit <see cref="AgentOptions.DisabledTools"/> entry still wins.
    /// </summary>
    private async Task<List<ITool>> ApplySkillToolAsync(List<ITool> tools, AgentOptions options)
    {
        var skillTool = _toolRegistry.GetTool(UseSkillToolName);
        if (skillTool is null) return tools;
        var disabled = options.DisabledTools.Contains(UseSkillToolName, StringComparer.OrdinalIgnoreCase);
        var hasSkills = !disabled && (await GetAgentSkillsAsync(options).ConfigureAwait(false)).Count > 0;
        var offered = tools.Any(t => string.Equals(t.Name, UseSkillToolName, StringComparison.OrdinalIgnoreCase));
        if (hasSkills && !offered)
            return tools.Append(skillTool).ToList();
        if (!hasSkills && offered)
            return tools.Where(t => !string.Equals(t.Name, UseSkillToolName, StringComparison.OrdinalIgnoreCase)).ToList();
        return tools;
    }

    /// <summary>
    /// Builds the skills list for the &lt;available_skills&gt; section. Returns
    /// just the inner content — BuildSystemPrompt applies the wrapping tag.
    /// </summary>
    private async Task<string> BuildSkillsBlockAsync(AgentOptions options)
    {
        var skills = await GetAgentSkillsAsync(options).ConfigureAwait(false);
        var pinned = new HashSet<string>(options.PinnedSkills, StringComparer.OrdinalIgnoreCase);
        var listed = skills.Where(s => !pinned.Contains(s.Name)).ToList();
        if (listed.Count == 0) return string.Empty;
        var sb = new StringBuilder(
            $"Skills are reusable instruction packs. When a task matches a skill's description, call the {UseSkillToolName} tool " +
            "with its name to load the full instructions, then follow them.\n");
        foreach (var s in listed)
        {
            var desc = string.IsNullOrWhiteSpace(s.Description) ? string.Empty : $" — {s.Description}";
            sb.AppendLine($"- {s.Name}{desc}");
        }
        return sb.ToString();
    }

    /// <summary>Full instructions of the agent's pinned skills (always in context).</summary>
    private async Task<string> BuildPinnedSkillsBlockAsync(AgentOptions options)
    {
        if (_skillRegistry is null || options.PinnedSkills.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var name in options.PinnedSkills.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var invocation = await _skillRegistry.InvokeAsync(name).ConfigureAwait(false);
                if (invocation is null)
                {
                    _logger.LogWarning("Pinned skill {Name} was not found", name);
                    continue;
                }
                sb.AppendLine(invocation.ToPromptBlock());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load pinned skill {Name}", name);
            }
        }
        return sb.ToString();
    }
}
