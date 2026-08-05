using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.Services;

public class AgentService
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IToolRegistry _toolRegistry;
    private readonly IContextManager _contextManager;
    private readonly ILogger<AgentService> _logger;
    private readonly IAgentEventBus _eventBus;
    private readonly SessionRecorder _recorder;
    private CancellationTokenSource? _currentCts;

    public IAgentEventBus EventBus => _eventBus;

    public AgentService(
        IAgentOrchestrator orchestrator,
        IToolRegistry toolRegistry,
        IContextManager contextManager,
        ILogger<AgentService> logger,
        IAgentEventBus eventBus,
        SessionRecorder recorder)
    {
        _orchestrator = orchestrator;
        _toolRegistry = toolRegistry;
        _contextManager = contextManager;
        _logger = logger;
        _eventBus = eventBus;
        _recorder = recorder;

        // Begin recording all agent events from the shared bus so the
        // current chat session can be exported (Priority 5).
        _recorder.Start("default");
    }

    public async Task<string> SendMessageAsync(string message, string sessionId = "default")
    {
        try
        {
            _logger.LogInformation("Processing message: {Message}", message);

            await _contextManager.AddMessageAsync(sessionId, new Message
            {
                Role = MessageRole.User,
                Content = message
            });

            var response = await _orchestrator.RunAsync(
                message,
                sessionId,
                new AgentOptions());

            await _contextManager.AddMessageAsync(sessionId, new Message
            {
                Role = MessageRole.Assistant,
                Content = response.Content
            });

            return response.Content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            return $"Error: {ex.Message}";
        }
    }

    public async Task StreamMessageAsync(
        string message,
        string sessionId = "default",
        AgentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _currentCts.Token;

        try
        {
            _logger.LogInformation("Streaming message: {Message}", message);

            await foreach (var evt in _orchestrator.StreamRunAsync(
                message,
                sessionId,
                options ?? new AgentOptions(),
                token))
            {
                _eventBus.Publish(evt);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Message streaming cancelled");
            _eventBus.Publish(new AgentFinishedEvent(new AgentResponse
            {
                Content = string.Empty,
                WasCancelled = true
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming message");
            _eventBus.Publish(new AgentErrorEvent(ex));
        }
    }

    public void Cancel()
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = null;
    }

    public string GetAvailableTools()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available tools:");

        foreach (var tool in _toolRegistry.GetAllTools())
        {
            sb.AppendLine($"- {tool.Name} [{tool.Risk}]: {tool.Description}");
        }

        return sb.ToString();
    }
}