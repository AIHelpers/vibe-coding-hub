using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IProjectMemoryLoader? _memoryLoader;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _sessionCts = new();

    // Cache the loaded project memory (AGENTS.md/AGENT.md/AIAGENT.md) keyed
    // by working directory, so every turn doesn't re-read from disk — only
    // reloaded when the working directory changes or a caller explicitly
    // asks for a refresh (e.g. after /init or editing the file).
    private ProjectMemory? _cachedProjectMemory;
    private string? _cachedMemoryDirectory;

    public IAgentEventBus EventBus => _eventBus;

    public AgentService(
        IAgentOrchestrator orchestrator,
        IToolRegistry toolRegistry,
        IContextManager contextManager,
        ILogger<AgentService> logger,
        IAgentEventBus eventBus,
        SessionRecorder recorder,
        IProjectMemoryLoader? memoryLoader = null)
    {
        _orchestrator = orchestrator;
        _toolRegistry = toolRegistry;
        _contextManager = contextManager;
        _logger = logger;
        _eventBus = eventBus;
        _recorder = recorder;
        _memoryLoader = memoryLoader;

        // Begin recording all agent events from the shared bus so the
        // current chat session can be exported (Priority 5).
        _recorder.Start("default");
    }

    /// <summary>
    /// Loads (or reloads) project memory for <paramref name="workingDirectory"/>
    /// and returns it. Called automatically the first time a message is sent
    /// for a given directory; callers can also invoke this explicitly (e.g.
    /// after the user edits AGENTS.md, or runs /init) to force a refresh.
    /// </summary>
    public async Task<ProjectMemory?> RefreshProjectMemoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (_memoryLoader == null || string.IsNullOrEmpty(workingDirectory))
            return null;

        try
        {
            _cachedProjectMemory = await _memoryLoader.LoadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            _cachedMemoryDirectory = workingDirectory;
            if (_cachedProjectMemory is { HasContent: true })
            {
                _logger.LogInformation("Loaded project memory from {Path}", _cachedProjectMemory.FilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load project memory for {Dir}", workingDirectory);
        }

        return _cachedProjectMemory;
    }

    /// <summary>
    /// Attaches the cached project memory for <see cref="AgentOptions.WorkingDirectory"/>
    /// to <paramref name="options"/>, loading it first if the working
    /// directory hasn't been seen yet or has changed since the last load.
    /// A caller that already set <see cref="AgentOptions.ProjectMemory"/>
    /// explicitly is left untouched.
    /// </summary>
    private async Task<AgentOptions> EnsureProjectMemoryAsync(AgentOptions options)
    {
        if (_memoryLoader == null || options.ProjectMemory != null)
            return options;

        var dir = options.WorkingDirectory;
        if (string.IsNullOrEmpty(dir))
            return options;

        if (!string.Equals(_cachedMemoryDirectory, dir, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshProjectMemoryAsync(dir).ConfigureAwait(false);
        }

        return _cachedProjectMemory == null ? options : options with { ProjectMemory = _cachedProjectMemory };
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

            var options = await EnsureProjectMemoryAsync(new AgentOptions());
            var response = await _orchestrator.RunAsync(
                message,
                sessionId,
                options);

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
        // Each session gets its own CancellationTokenSource so a background
        // task (a different sessionId) can run concurrently with the main
        // chat without one's Cancel() affecting the other. Replacing any
        // prior entry for this sessionId mirrors the old single-field
        // behavior for repeated calls on the same session.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionCts[sessionId] = cts;
        var token = cts.Token;

        try
        {
            _logger.LogInformation("Streaming message: {Message}", message);

            var effectiveOptions = await EnsureProjectMemoryAsync(options ?? new AgentOptions());

            await foreach (var evt in _orchestrator.StreamRunAsync(
                message,
                sessionId,
                effectiveOptions,
                token))
            {
                // The orchestrator already publishes each event to the
                // shared event bus. Publishing again here would duplicate
                // every event (doubled text, duplicate tool cards, double
                // finished/error signals), so we only consume the stream
                // to drive it to completion.
                _ = evt;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Message streaming cancelled for session {SessionId}", sessionId);
            _eventBus.Publish(new SessionScopedEvent(sessionId, new AgentFinishedEvent(new AgentResponse
            {
                Content = string.Empty,
                WasCancelled = true
            })));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming message for session {SessionId}", sessionId);
            _eventBus.Publish(new SessionScopedEvent(sessionId, new AgentErrorEvent(ex)));
        }
        finally
        {
            if (_sessionCts.TryGetValue(sessionId, out var current) && ReferenceEquals(current, cts))
            {
                _sessionCts.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    /// <summary>Cancels the "default" (main chat) session — kept for existing call sites that don't pass a session id.</summary>
    public void Cancel() => Cancel("default");

    /// <summary>Cancels the in-flight run for a specific session (e.g. one background task) without affecting any other running session.</summary>
    public void Cancel(string sessionId)
    {
        if (_sessionCts.TryGetValue(sessionId, out var cts))
        {
            cts.Cancel();
        }
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