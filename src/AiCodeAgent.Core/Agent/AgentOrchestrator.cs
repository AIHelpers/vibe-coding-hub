using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AiCodeAgent.Core.Configuration;
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

    public AgentOrchestrator(
        IAiProvider provider,
        IContextManager contextManager,
        IToolRegistry toolRegistry,
        AgentConfiguration config,
        ILogger<AgentOrchestrator> logger)
    {
        _provider = provider;
        _contextManager = contextManager;
        _toolRegistry = toolRegistry;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Runs the agent with the given user message, collecting all events into a final response.
    /// </summary>
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

    /// <summary>
    /// Streams agent events (text deltas, tool calls, errors) for real-time UI updates.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> StreamRunAsync(
        string userMessage,
        string sessionId,
        AgentOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var execContext = new AgentExecutionContext
        {
            SessionId = sessionId,
            WorkingDirectory = options.WorkingDirectory
        };

        // Add user message to context
        await _contextManager.AddMessageAsync(sessionId, new Message
        {
            Role = MessageRole.User,
            Content = userMessage
        }).ConfigureAwait(false);

        var tools = _toolRegistry.GetTools(options.EnabledTools) ?? new List<ITool>();
        var totalUsage = new TokenUsage();
        var toolExecutions = new List<ToolExecution>();
        var fullContent = new StringBuilder();
        var startTime = DateTime.UtcNow;
        var iteration = 0;

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

            // Stream the response
            var wasCancelled = false;
            var textDeltaEvents = new List<TextDeltaEvent>();
            
            try
            {
                await foreach (var chunk in _provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(chunk.Delta))
                    {
                        currentContent.Append(chunk.Delta);
                        fullContent.Append(chunk.Delta);
                        textDeltaEvents.Add(new TextDeltaEvent(chunk.Delta));
                    }

                    if (chunk.Usage != null)
                    {
                        totalUsage = new TokenUsage
                        {
                            PromptTokens = totalUsage.PromptTokens + chunk.Usage.PromptTokens,
                            CompletionTokens = totalUsage.CompletionTokens + chunk.Usage.CompletionTokens
                        };
                    }

                    if (chunk.IsFinished)
                    {
                        pendingToolCalls = chunk.ToolCalls;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }

            // Yield collected text deltas
            foreach (var textEvent in textDeltaEvents)
                yield return textEvent;

            if (wasCancelled)
            {
                yield return new AgentFinishedEvent(new AgentResponse
                {
                    Content = fullContent.ToString(),
                    ToolExecutions = toolExecutions,
                    TotalUsage = totalUsage,
                    Duration = DateTime.UtcNow - startTime,
                    WasCancelled = true
                });
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

                // Validate tool call has a name
                if (string.IsNullOrWhiteSpace(toolCall.Name))
                {
                    _logger.LogWarning("Received tool call with empty name, skipping");
                    continue;
                }

                yield return new ToolCallStartEvent(toolCall);

                var tool = _toolRegistry.GetTool(toolCall.Name);
                ToolResult result;
                var toolStart = DateTime.UtcNow;

                if (tool == null)
                {
                    _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
                    result = new ToolResult
                    {
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        Content = $"Unknown tool: {toolCall.Name}",
                        IsError = true
                    };
                }
                else
                {
                    try
                    {
                        result = await tool.ExecuteAsync(toolCall, execContext).ConfigureAwait(false);
                        result = result with { ToolCallId = toolCall.Id };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Tool execution failed: {Tool}", toolCall.Name);
                        result = new ToolResult
                        {
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = $"Tool error: {ex.Message}",
                            IsError = true
                        };
                    }
                }

                var duration = DateTime.UtcNow - toolStart;
                toolExecutions.Add(new ToolExecution { Call = toolCall, Result = result, Duration = duration });
                yield return new ToolCallEndEvent(toolCall, result, duration);

                // Add tool result to context
                await _contextManager.AddMessageAsync(sessionId, new Message
                {
                    Role = MessageRole.Tool,
                    Content = result.Content,
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Name
                }).ConfigureAwait(false);
            }

            // If all tool results are errors, break to prevent infinite loop
            if (pendingToolCalls.Count > 0 && toolExecutions.Count > 0 && 
                toolExecutions.All(te => te.Result.IsError))
                break;
        }

        var response = new AgentResponse
        {
            Content = fullContent.ToString(),
            ToolExecutions = toolExecutions,
            TotalUsage = totalUsage,
            Duration = DateTime.UtcNow - startTime,
            WasCancelled = cancellationToken.IsCancellationRequested
        };

        yield return new AgentFinishedEvent(response);
    }

    /// <summary>
    /// Builds the system prompt that instructs the AI model on its role and capabilities.
    /// </summary>
    private string BuildSystemPrompt(AgentOptions options)
    {
        return $"""
            You are an expert AI coding assistant with deep knowledge of software development.
            You have access to tools to read/write files, execute commands, search code, and more.
            
            Working directory: {options.WorkingDirectory}
            Date: {DateTime.UtcNow:yyyy-MM-dd}
            OS: {RuntimeInformation.OSDescription}
            
            Guidelines:
            - Always read files before editing them to understand current state
            - Make minimal, targeted changes when fixing bugs
            - Run diagnostics after making changes to verify correctness
            - Explain what you're doing and why
            - If a task is ambiguous, ask for clarification
            - Prefer editing specific code over rewriting entire files
            - Use git to understand history when helpful
            
            When writing code:
            - Follow existing code style and conventions
            - Add appropriate error handling
            - Write clean, maintainable code
            - Consider edge cases
            """;
    }
}