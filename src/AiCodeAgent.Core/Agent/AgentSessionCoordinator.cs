using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Coordinates multiple agent orchestrator instances in a multi-agent session.
/// Manages shared state (changeset, context store) and sequential turn-taking.
/// </summary>
public class AgentSessionCoordinator
{
    private readonly Dictionary<string, IAgentOrchestrator> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedChangeset _changeset = new();
    private readonly SharedContextStore _context = new();
    private readonly IAgentEventBus? _eventBus;
    private readonly ILogger<AgentSessionCoordinator> _logger;
    private readonly RolePresetLoader? _presetLoader;

    public SharedChangeset Changeset => _changeset;
    public SharedContextStore Context => _context;
    public IReadOnlyDictionary<string, IAgentOrchestrator> Agents => _agents;

    public AgentSessionCoordinator(
        ILogger<AgentSessionCoordinator> logger,
        IAgentEventBus? eventBus = null,
        RolePresetLoader? presetLoader = null)
    {
        _logger = logger;
        _eventBus = eventBus;
        _presetLoader = presetLoader;
    }

    /// <summary>
    /// Resolves the effective options for a step: fills in the role preset's
    /// system prompt and allowed tools when the step didn't already specify them,
    /// so a step's Role is more than a display label.
    /// </summary>
    private AgentOptions ResolveStepOptions(SessionStep step)
    {
        var options = step.Options with { AgentId = step.AgentId, Role = step.Role };
        if (_presetLoader == null || string.IsNullOrEmpty(step.Role))
            return options;

        var preset = _presetLoader.GetPreset(step.Role);
        if (preset == null)
            return options;

        if (string.IsNullOrWhiteSpace(options.RoleSystemPrompt))
            options = options with { RoleSystemPrompt = preset.SystemPrompt };
        if (options.EnabledTools.Count == 0 && preset.AllowedTools.Count > 0)
            options = options with { EnabledTools = preset.AllowedTools };

        return options;
    }

    /// <summary>Register an agent orchestrator instance under a given agent ID.</summary>
    public void RegisterAgent(string agentId, IAgentOrchestrator orchestrator)
    {
        _agents[agentId] = orchestrator;
        _logger.LogInformation("Registered agent {AgentId}", agentId);
    }

    /// <summary>Unregister an agent by ID.</summary>
    public bool UnregisterAgent(string agentId) => _agents.Remove(agentId);

    /// <summary>
    /// Run a multi-agent session plan with strict sequential turn-taking.
    /// Each step's events are tagged with the source agent.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        SessionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (plan.Steps.Count == 0)
        {
            _logger.LogWarning("Session plan has no steps");
            yield break;
        }

        _logger.LogInformation("Starting multi-agent session {SessionId} with {StepCount} steps",
            plan.SessionId, plan.Steps.Count);

        foreach (var step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            if (!_agents.TryGetValue(step.AgentId, out var orchestrator))
            {
                _logger.LogWarning("Agent {AgentId} not registered, skipping step", step.AgentId);
                var errorEvent = new AgentErrorEvent(
                    new InvalidOperationException($"Agent '{step.AgentId}' is not registered"));
                yield return new AgentTaggedEvent(errorEvent, step.AgentId, step.Role);
                _eventBus?.Publish(errorEvent);
                continue;
            }

            _logger.LogInformation("Running step for agent {AgentId} (role: {Role})", step.AgentId, step.Role);

            // Tag every event with the source agent
            await foreach (var evt in orchestrator.StreamRunAsync(
                step.Prompt,
                plan.SessionId,
                ResolveStepOptions(step),
                cancellationToken))
            {
                var tagged = new AgentTaggedEvent(evt, step.AgentId, step.Role);
                yield return tagged;
                _eventBus?.Publish(tagged);

                // Capture diff events into the shared changeset with attribution
                if (evt is DiffProducedEvent diffEvent)
                {
                    _changeset.Add(diffEvent.Diff with { AgentId = step.AgentId });

                    // Parse diff text into hunks and add to canonical store
                    var hunks = DiffParser.ParseSimpleDiff(
                        diffEvent.Diff.DiffText,
                        diffEvent.Diff.FilePath,
                        step.AgentId);
                    _changeset.AddHunks(hunks);
                }
            }
        }

        _logger.LogInformation("Multi-agent session {SessionId} completed", plan.SessionId);
    }

    /// <summary>Run a single step of a session plan (for event-driven wake-ups).</summary>
    public async IAsyncEnumerable<AgentEvent> RunStepAsync(
        SessionStep step,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(step.AgentId, out var orchestrator))
        {
            _logger.LogWarning("Agent {AgentId} not registered, skipping step", step.AgentId);
            var errorEvent = new AgentErrorEvent(
                new InvalidOperationException($"Agent '{step.AgentId}' is not registered"));
            yield return new AgentTaggedEvent(errorEvent, step.AgentId, step.Role);
            _eventBus?.Publish(errorEvent);
            yield break;
        }

        await foreach (var evt in orchestrator.StreamRunAsync(
            step.Prompt,
            sessionId,
            ResolveStepOptions(step),
            cancellationToken))
        {
            var tagged = new AgentTaggedEvent(evt, step.AgentId, step.Role);
            yield return tagged;
            _eventBus?.Publish(tagged);

            if (evt is DiffProducedEvent diffEvent)
            {
                _changeset.Add(diffEvent.Diff with { AgentId = step.AgentId });

                // Parse diff text into hunks and add to canonical store
                var hunks = DiffParser.ParseSimpleDiff(
                    diffEvent.Diff.DiffText,
                    diffEvent.Diff.FilePath,
                    step.AgentId);
                _changeset.AddHunks(hunks);
            }
        }
    }

    /// <summary>Clear shared state between sessions.</summary>
    public void Reset()
    {
        _changeset.Clear();
        _context.Clear();
    }
}