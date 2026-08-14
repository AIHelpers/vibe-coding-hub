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

    public AgentOrchestrator(
        IAiProvider provider,
        IContextManager contextManager,
        IToolRegistry toolRegistry,
        AgentConfiguration config,
        ILogger<AgentOrchestrator> logger,
        IPermissionService permissionService,
        ICheckpointManager checkpointManager,
        IAgentEventBus? eventBus = null)
    {
        _provider = provider;
        _contextManager = contextManager;
        _toolRegistry = toolRegistry;
        _config = config;
        _logger = logger;
        _permissionService = permissionService;
        _checkpointManager = checkpointManager;
        _eventBus = eventBus;
    }

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
            Role = options.Role
        };

        // Set permission mode (per-agent if AgentId is provided)
        _permissionService.SetMode(options.PermissionMode, options.AgentId);

        // Emit status update
        var statusEvent = new StatusUpdateEvent("Processing", "Starting agent loop");
        yield return statusEvent;
        _eventBus?.Publish(statusEvent);

        // Add user message to context
        await _contextManager.AddMessageAsync(sessionId, new Message
        {
            Role = MessageRole.User,
            Content = userMessage
        }).ConfigureAwait(false);

        var tools = _toolRegistry.GetTools(options.EnabledTools) ?? new List<ITool>();

        // In Plan mode, filter out non-Read tools so the model is never
        // tempted to call write/execute tools (which would all fail).
        if (options.PermissionMode == PermissionMode.Plan)
        {
            tools = tools.Where(t => t.Risk == RiskLevel.Read).ToList();
        }
        var totalUsage = new TokenUsage();
        var toolExecutions = new List<ToolExecution>();
        var fullContent = new StringBuilder();
        var startTime = DateTime.UtcNow;
        var iteration = 0;
        var turnId = Guid.NewGuid().ToString("N")[..12];

        while (iteration < options.MaxIterations && !cancellationToken.IsCancellationRequested)
        {
            iteration++;

            var messages = await _contextManager.GetContextAsync(sessionId).ConfigureAwait(false) ?? new List<Message>();
            await _contextManager.TrimContextAsync(sessionId, options.MaxTokens).ConfigureAwait(false);

            var request = new CompletionRequest
            {
                Messages = messages,
                Tools = tools.Select(t => t.Definition).ToList(),
                SystemPrompt = BuildSystemPrompt(options),
                Options = new CompletionOptions
                {
                    Model = options.Model,
                    Stream = true,
                    MaxTokens = options.MaxTokens > 0 ? Math.Min(options.MaxTokens, 8192) : 8192
                }
            };

            var currentContent = new StringBuilder();
            List<ToolCall>? pendingToolCalls = null;

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
                    _eventBus?.Publish(textEvent);
                }

                if (chunk.Usage != null)
                {
                    totalUsage = new TokenUsage
                    {
                        PromptTokens = totalUsage.PromptTokens + chunk.Usage.PromptTokens,
                        CompletionTokens = totalUsage.CompletionTokens + chunk.Usage.CompletionTokens
                    };
                    var usageEvent = new TokenUsageEvent(totalUsage);
                    _eventBus?.Publish(usageEvent);
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
                _eventBus?.Publish(finishedEvent);
                yield break;
            }

            // Save assistant message
            var assistantMessage = new Message
            {
                Role = MessageRole.Assistant,
                Content = currentContent.ToString(),
                ToolCalls = pendingToolCalls
            };
            await _contextManager.AddMessageAsync(sessionId, assistantMessage).ConfigureAwait(false);

            // If no tool calls, we're done
            if (pendingToolCalls == null || pendingToolCalls.Count == 0)
                break;

            // Execute tool calls
            foreach (var toolCall in pendingToolCalls)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (string.IsNullOrWhiteSpace(toolCall.Name))
                {
                    _logger.LogWarning("Received tool call with empty name, skipping");
                    continue;
                }

                var tool = _toolRegistry.GetTool(toolCall.Name);
                if (tool == null)
                {
                    _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
                    var result = new ToolResult
                    {
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        Content = $"Unknown tool: {toolCall.Name}",
                        IsError = true
                    };
                    var duration = TimeSpan.Zero;
                    toolExecutions.Add(new ToolExecution { Call = toolCall, Result = result, Duration = duration });
                    var endEvent = new ToolCallEndEvent(toolCall, result, duration);
                    yield return endEvent;
                    _eventBus?.Publish(endEvent);

                    await _contextManager.AddMessageAsync(sessionId, new Message
                    {
                        Role = MessageRole.Tool,
                        Content = result.Content,
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name
                    }).ConfigureAwait(false);
                    continue;
                }

                // Check permissions before executing
                var statusEvent2 = new StatusUpdateEvent($"Requesting approval for {toolCall.Name}");
                yield return statusEvent2;
                _eventBus?.Publish(statusEvent2);

                // For write/execute operations, check permissions
                if (tool.Risk != RiskLevel.Read)
                {
                    var isApproved = await _permissionService.RequestApprovalAsync(toolCall, tool.Risk, options, options.AgentId);
                    if (!isApproved)
                    {
                        // In Plan mode, non-Read tools are silently denied
                        // (no approval dialog) so the model gets a tool error
                        // and can continue instead of showing an approval
                        // dialog that would loop forever.
                        var mode = _permissionService.GetMode(options.AgentId);
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
                            _eventBus?.Publish(planDeniedEndEvent);

                            await _contextManager.AddMessageAsync(sessionId, new Message
                            {
                                Role = MessageRole.Tool,
                                Content = planDeniedResult.Content,
                                ToolCallId = toolCall.Id,
                                Name = toolCall.Name
                            }).ConfigureAwait(false);
                            continue;
                        }

                        // Emit approval request event - UI will handle this
                        var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var approvalEvent = new ApprovalRequestEvent(toolCall, approvalTcs, tool.Risk);
                        yield return approvalEvent;
                        _eventBus?.Publish(approvalEvent);

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
                            _eventBus?.Publish(deniedEndEvent);

                            await _contextManager.AddMessageAsync(sessionId, new Message
                            {
                                Role = MessageRole.Tool,
                                Content = deniedResult.Content,
                                ToolCallId = toolCall.Id,
                                Name = toolCall.Name
                            }).ConfigureAwait(false);
                            continue;
                        }
                    }
                }

                // Create checkpoint before write operations
                if (tool.Risk == RiskLevel.Write && toolCall.Arguments.TryGetValue("path", out var pathObj) && pathObj != null)
                {
                    var filePath = pathObj.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        // Create checkpoint without yield in try-catch
                        CheckpointCreatedEvent? checkpointEvent = null;
                        try
                        {
                            var checkpoint = await _checkpointManager.CreateCheckpointAsync(filePath, turnId, sessionId);
                            checkpointEvent = new CheckpointCreatedEvent(checkpoint);
                            _eventBus?.Publish(checkpointEvent);
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

                // Execute tool
                var startEvent = new ToolCallStartEvent(toolCall);
                yield return startEvent;
                _eventBus?.Publish(startEvent);

                var statusEvent3 = new StatusUpdateEvent($"Executing {toolCall.Name}");
                yield return statusEvent3;
                _eventBus?.Publish(statusEvent3);

                ToolResult toolResult;
                var toolStart = DateTime.UtcNow;

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
                _eventBus?.Publish(toolEndEvent);

                // Emit diff event for write operations (single-agent mode)
                if (tool.Risk == RiskLevel.Write && !toolResult.IsError)
                {
                    var filePath = toolCall.Arguments.TryGetValue("path", out var p) ? p?.ToString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        var diffEntry = new DiffEntry
                        {
                            FilePath = filePath,
                            DiffText = toolResult.Content,
                            AgentId = options.AgentId
                        };
                        var diffEvent = new DiffProducedEvent(diffEntry);
                        yield return diffEvent;
                        _eventBus?.Publish(diffEvent);
                    }
                }

                // Add tool result to context
                await _contextManager.AddMessageAsync(sessionId, new Message
                {
                    Role = MessageRole.Tool,
                    Content = toolResult.Content,
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Name
                }).ConfigureAwait(false);
            }

            // If all tool results in this iteration are errors, break to prevent infinite loop
            // (toolExecutions accumulates across iterations, so check only this iteration's batch)
            var currentIterationExecutions = toolExecutions
                .Skip(toolExecutions.Count - pendingToolCalls.Count)
                .ToList();
            if (pendingToolCalls.Count > 0 && currentIterationExecutions.Count > 0 &&
                currentIterationExecutions.All(te => te.Result.IsError))
            {
                _logger.LogWarning("All {Count} tool calls in iteration {Iteration} returned errors — breaking to prevent infinite loop",
                    currentIterationExecutions.Count, iteration);
                break;
            }
        }

        var response = new AgentResponse
        {
            Content = fullContent.ToString(),
            ToolExecutions = toolExecutions,
            TotalUsage = totalUsage,
            Duration = DateTime.UtcNow - startTime,
            WasCancelled = cancellationToken.IsCancellationRequested
        };

        var finished = new AgentFinishedEvent(response);
        yield return finished;
        _eventBus?.Publish(finished);
        
        var doneEvent = new StatusUpdateEvent("Done", "Agent completed");
        yield return doneEvent;
        _eventBus?.Publish(doneEvent);
    }

    private string BuildSystemPrompt(AgentOptions options)
    {
        var roleLine = string.IsNullOrEmpty(options.Role)
            ? string.Empty
            : $"Role: {options.Role}";
        var agentLine = string.IsNullOrEmpty(options.AgentId)
            ? string.Empty
            : $"Agent ID: {options.AgentId}";
        var roleInstructions = string.IsNullOrWhiteSpace(options.RoleSystemPrompt)
            ? string.Empty
            : $"\n{options.RoleSystemPrompt.Trim()}\n";

        return $"""
            You are an expert AI coding assistant with deep knowledge of software development.
            You have access to tools to read/write files, execute commands, search code, and more.
            
            Working directory: {options.WorkingDirectory}
            Date: {DateTime.UtcNow:yyyy-MM-dd}
            OS: {RuntimeInformation.OSDescription}
            Permission mode: {options.PermissionMode}
            {roleLine}
            {agentLine}
            {roleInstructions}
            {(options.PermissionMode == PermissionMode.Plan ? """
            Plan mode is ACTIVE:
            - You may ONLY use read-only tools (read files, search, list directories)
            - Write, execute, and edit tools are DISABLED and will return errors
            - Do NOT attempt write/execute tools; plan the work and present it instead
            - Gather information with read tools, then summarize a plan for the user
            
            """ : string.Empty)}
            Guidelines:
            - Always read files before editing them to understand current state
            - Make minimal, targeted changes when fixing bugs
            - Run diagnostics after making changes to verify correctness
            - Explain what you're doing and why
            - If a task is ambiguous, ask for clarification
            - Prefer editing specific code over rewriting entire files
            - Use git to understand history when helpful

            Important — "write" does not always mean "create a file":
            - When the user asks you to "write a plan", "write an outline",
              "write a summary", "write a description", or similar, they want
              you to produce that content as your chat response (text), NOT to
              call the write_file tool.
            - Only use the write_file/edit tools when the user explicitly asks
              you to create, modify, or save a file (e.g. "create a file
              named X", "save this to a file", "edit the file at path Y").
            - When in doubt, answer in chat and ask before touching the filesystem.

            When writing code:
            - Follow existing code style and conventions
            - Add appropriate error handling
            - Write clean, maintainable code
            - Consider edge cases
            """;
    }
}