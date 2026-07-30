using System;
using System.Threading.Tasks;
using System.Text;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.Services;

public class AgentService
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IToolRegistry _toolRegistry;
    private readonly IContextManager _contextManager;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IAgentOrchestrator orchestrator,
        IToolRegistry toolRegistry,
        IContextManager contextManager,
        ILogger<AgentService> logger)
    {
        _orchestrator = orchestrator;
        _toolRegistry = toolRegistry;
        _contextManager = contextManager;
        _logger = logger;
    }

    public async Task<string> SendMessageAsync(string message, string sessionId = "default")
    {
        try
        {
            _logger.LogInformation("Processing message: {Message}", message);

            // Add user message to context
            await _contextManager.AddMessageAsync(sessionId, new Message
            {
                Role = MessageRole.User,
                Content = message
            });

            // Process the message through the orchestrator
            var response = await _orchestrator.RunAsync(
                message,
                sessionId,
                new AgentOptions());

            // Add assistant response to context
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

    public string GetAvailableTools()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available tools:");

        foreach (var tool in _toolRegistry.GetAllTools())
        {
            sb.AppendLine($"- {tool.Name}: {tool.Description}");
        }

        return sb.ToString();
    }
}