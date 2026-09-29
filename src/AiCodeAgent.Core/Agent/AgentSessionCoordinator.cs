using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Coordinates multiple agent orchestrator instances in a multi-agent session.
/// Manages shared state (changeset, context store, inter-agent mailbox), sequential
/// turn-taking, and concurrent execution of steps grouped into parallel groups
/// (consecutive steps sharing the same SessionStep.ParallelGroup label).
/// </summary>
public class AgentSessionCoordinator
{
    private readonly Dictionary<string, IAgentOrchestrator> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedChangeset _changeset = new();
    private readonly SharedContextStore _context = new();
    private readonly AgentMailbox _mailbox = new();
    private readonly IAgentEventBus? _eventBus;
    private readonly ILogger<AgentSessionCoordinator> _logger;
    private readonly RolePresetLoader? _presetLoader;
    private readonly IContextManager? _contextManager;

    public SharedChangeset Changeset => _changeset;
    public SharedContextStore Context => _context;

    /// <summary>
    /// Shared mailbox for inter-agent communication. Agents post to it via the
    /// send_message tool; the coordinator drains each agent's pending messages
    /// into its prompt before the agent's step runs.
    /// </summary>
    public AgentMailbox Mailbox => _mailbox;

    public IReadOnlyDictionary<string, IAgentOrchestrator> Agents => _agents;

    public AgentSessionCoordinator(
        ILogger<AgentSessionCoordinator> logger,
        IAgentEventBus? eventBus = null,
        RolePresetLoader? presetLoader = null,
        IContextManager? contextManager = null)
    {
        _logger = logger;
        _eventBus = eventBus;
        _presetLoader = presetLoader;
        _contextManager = contextManager;
    }

    /// <summary>
    /// Resolves the effective options for a step: fills in the role preset's
    /// system prompt and allowed tools when the step did not already specify them,
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
        _mailbox.EnsureAgent(agentId);
        _logger.LogInformation("Registered agent {AgentId}", agentId);
    }

    /// <summary>Unregister an agent by ID.</summary>
    public bool UnregisterAgent(string agentId) => _agents.Remove(agentId);

    /// <summary>
    /// Run a multi-agent session plan. Steps run sequentially by default;
    /// consecutive steps sharing the same non-empty ParallelGroup label run
    /// concurrently, with their events merged (tagged per agent) into one stream.
    /// Each step's events are tagged with the source agent, and any pending mailbox
    /// messages for the step's agent are injected into its prompt first.
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

        // Register every agent in the plan with the mailbox up front so
        // broadcasts posted by early agents reach agents that run later.
        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrEmpty(step.AgentId) == false)
                _mailbox.EnsureAgent(step.AgentId);
        }

        var index = 0;
        while (index < plan.Steps.Count)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            var step = plan.Steps[index];

            if (string.IsNullOrEmpty(step.ParallelGroup))
            {
                if (step.RequireConfirmation)
                {
                    var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var gateCall = new ToolCall
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Name = "pipeline_stage",
                        Arguments = new Dictionary<string, object?>
                        {
                            ["stage"] = step.Role,
                            ["agent"] = step.AgentId,
                            ["prompt"] = step.Prompt.Length > 300 ? step.Prompt[..300] + "…" : step.Prompt
                        }
                    };
                    var gateEvent = new ApprovalRequestEvent(gateCall, confirmation, RiskLevel.Execute);
                    yield return new AgentTaggedEvent(gateEvent, step.AgentId, step.Role);
                    _eventBus?.Publish(gateEvent);

                    using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gateCts.CancelAfter(TimeSpan.FromMinutes(10));
                    await Task.WhenAny(confirmation.Task, Task.Delay(Timeout.InfiniteTimeSpan, gateCts.Token))
                        .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);

                    var confirmed = confirmation.Task.IsCompletedSuccessfully && confirmation.Task.Result;
                    if (!confirmed)
                    {
                        var declined = new StatusUpdateEvent("Halted",
                            $"Stage '{step.Role}' was not confirmed. It and the stages after it were not run.");
                        yield return new AgentTaggedEvent(declined, step.AgentId, step.Role);
                        _eventBus?.Publish(declined);
                        yield break;
                    }
                }

                string? failure = null;
                await foreach (var tagged in RunStepCore(step, plan.SessionId, cancellationToken))
                {
                    failure ??= DetectStepFailure(tagged);
                    yield return tagged;
                }

                // Gate: a stage that failed or ran out of steps must not silently hand over to
                // the next one (e.g. "Deploy" after tests that never ran).
                if (failure != null && index < plan.Steps.Count - 1)
                {
                    var halted = new StatusUpdateEvent("Halted",
                        $"Stopped after '{step.Role ?? step.AgentId}': {failure}. Remaining stages were not run.");
                    yield return new AgentTaggedEvent(halted, step.AgentId, step.Role);
                    _eventBus?.Publish(halted);
                    _logger.LogWarning("Halting session {SessionId} after step {AgentId}: {Failure}", plan.SessionId, step.AgentId, failure);
                    yield break;
                }
                index++;
                continue;
            }

            // Collect all consecutive steps that share this parallel group.
            var groupName = step.ParallelGroup;
            var groupSteps = new List<SessionStep>();
            while (index < plan.Steps.Count &&
                   string.Equals(plan.Steps[index].ParallelGroup, groupName, StringComparison.OrdinalIgnoreCase))
            {
                groupSteps.Add(plan.Steps[index]);
                index++;
            }

            await foreach (var evt in RunParallelGroupAsync(groupName ?? string.Empty, groupSteps, plan.SessionId, cancellationToken))
                yield return evt;
        }

        _logger.LogInformation("Multi-agent session {SessionId} completed", plan.SessionId);
    }

    private static string? DetectStepFailure(AgentEvent evt) => (evt is AgentTaggedEvent t ? t.Inner : evt) switch
    {
        AgentErrorEvent err => err.Error.Message,
        AgentFinishedEvent { Response.StopReason: "repeated_errors" } => "every tool call kept failing",
        AgentFinishedEvent { Response.StopReason: "max_iterations" } => "it hit its step limit before finishing",
        _ => null
    };

    /// <summary>Run a single step of a session plan (for event-driven wake-ups).</summary>
    public async IAsyncEnumerable<AgentEvent> RunStepAsync(
        SessionStep step,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var tagged in RunStepCore(step, sessionId, cancellationToken))
            yield return tagged;
    }

    /// <summary>
    /// Core execution of a single step: drains the agent's mailbox into the prompt,
    /// streams the agent's events (tagged with the source agent), and captures
    /// produced diffs into the shared changeset with attribution.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunStepCore(
        SessionStep step,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

        _logger.LogInformation("Running step for agent {AgentId} (role: {Role})", step.AgentId, step.Role);

        var prompt = BuildStepPrompt(step);

        await foreach (var evt in orchestrator.StreamRunAsync(
            prompt,
            sessionId,
            ResolveStepOptions(step),
            cancellationToken))
        {
            var tagged = new AgentTaggedEvent(evt, step.AgentId, step.Role);
            yield return tagged;
            _eventBus?.Publish(tagged);

            if (evt is DiffProducedEvent diffEvent)
                CaptureDiff(diffEvent, step.AgentId);
        }
    }

    /// <summary>
    /// Runs every step of a parallel group concurrently and merges their tagged
    /// events into a single stream via an unbounded channel. The group completes
    /// when all of its agents finish (or fail); a failing agent surfaces an
    /// AgentErrorEvent but does not cancel its peers.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunParallelGroupAsync(
        string groupName,
        IReadOnlyList<SessionStep> steps,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agentIds = steps.Select(s => s.AgentId).ToList();

        var started = new ParallelGroupStartedEvent(groupName, agentIds);
        yield return started;
        _eventBus?.Publish(started);

        _logger.LogInformation("Running parallel group '{Group}' with agents {Agents}",
            groupName, string.Join(", ", agentIds));

        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Every parallel agent works on its own forked copy of the shared conversation: two agents
        // appending assistant/tool messages to ONE history interleave them, which providers reject.
        var branchIds = new Dictionary<SessionStep, string>();
        var finalTexts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        if (_contextManager != null)
        {
            var baseMessages = await _contextManager.GetContextAsync(sessionId).ConfigureAwait(false) ?? new List<Message>();
            foreach (var step in steps)
            {
                var branchId = $"{sessionId}::{step.AgentId}";
                branchIds[step] = branchId;
                try { await _contextManager.ClearAsync(branchId).ConfigureAwait(false); } catch { /* fresh branch */ }
                foreach (var m in baseMessages)
                    await _contextManager.AddMessageAsync(branchId, m).ConfigureAwait(false);
            }
        }

        var tasks = new List<Task>(steps.Count);
        foreach (var step in steps)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (!_agents.TryGetValue(step.AgentId, out var orchestrator))
                    {
                        var errorEvent = new AgentErrorEvent(
                            new InvalidOperationException($"Agent '{step.AgentId}' is not registered"));
                        await channel.Writer.WriteAsync(
                            new AgentTaggedEvent(errorEvent, step.AgentId, step.Role),
                            CancellationToken.None);
                        return;
                    }

                    var prompt = BuildStepPrompt(step);
                    var stepOptions = ResolveStepOptions(step);
                    if (branchIds.TryGetValue(step, out var branch))
                        stepOptions = stepOptions with { ContextSessionId = branch };

                    await foreach (var evt in orchestrator.StreamRunAsync(
                        prompt,
                        sessionId,
                        stepOptions,
                        cancellationToken))
                    {
                        var tagged = new AgentTaggedEvent(evt, step.AgentId, step.Role);
                        await channel.Writer.WriteAsync(tagged, CancellationToken.None);

                        if (evt is AgentFinishedEvent fin)
                            finalTexts[step.AgentId] = fin.Response.Content;

                        if (evt is DiffProducedEvent diffEvent)
                            CaptureDiff(diffEvent, step.AgentId);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Session cancelled: stop this agent; peers observe the same token.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Agent {AgentId} in parallel group '{Group}' failed", step.AgentId, groupName);
                    var errorEvent = new AgentTaggedEvent(new AgentErrorEvent(ex), step.AgentId, step.Role);
                    await channel.Writer.WriteAsync(errorEvent, CancellationToken.None);
                }
            }, CancellationToken.None));
        }

        var completion = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Individual task failures are already surfaced as error events.
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
            _eventBus?.Publish(evt);
        }

        await completion;

        // Fold the branches back: the shared history gets one summary message with each agent's
        // final answer, and the throw-away branch histories are dropped.
        if (_contextManager != null && branchIds.Count > 0)
        {
            try
            {
                var summary = new StringBuilder($"Parallel group '{groupName}' finished. Results per agent:\n");
                foreach (var step in steps)
                {
                    finalTexts.TryGetValue(step.AgentId, out var text);
                    summary.AppendLine($"\n## {step.Role ?? step.AgentId} ({step.AgentId})");
                    summary.AppendLine(string.IsNullOrWhiteSpace(text) ? "(no final answer)" : text.Trim());
                }
                await _contextManager.AddMessageAsync(sessionId, new Message
                {
                    Role = MessageRole.User,
                    Content = summary.ToString()
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to merge parallel group '{Group}' results into the shared context", groupName);
            }
            foreach (var branchId in branchIds.Values)
            {
                try { await _contextManager.ClearAsync(branchId).ConfigureAwait(false); } catch { /* best effort */ }
            }
        }

        var finished = new ParallelGroupFinishedEvent(groupName, agentIds);
        yield return finished;
        _eventBus?.Publish(finished);

        _logger.LogInformation("Parallel group '{Group}' completed", groupName);
    }

    /// <summary>
    /// Builds the effective prompt for a step: the step's prompt plus any mailbox
    /// messages addressed to the agent (direct or broadcast), oldest first.
    /// </summary>
    private string BuildStepPrompt(SessionStep step)
    {
        var messages = _mailbox.Drain(step.AgentId);
        if (messages.Count == 0)
            return step.Prompt;

        var sb = new StringBuilder(step.Prompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("# Messages from other agents");
        foreach (var message in messages)
        {
            var kind = message.IsBroadcast ? " (broadcast)" : string.Empty;
            sb.AppendLine($"[{message.Timestamp:HH:mm:ss}] From {message.FromAgentId}{kind}:");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        _logger.LogDebug("Injected {Count} mailbox message(s) into agent {AgentId}'s prompt",
            messages.Count, step.AgentId);

        return sb.ToString();
    }

    /// <summary>Captures a diff event into the shared changeset with per-agent attribution.</summary>
    private void CaptureDiff(DiffProducedEvent diffEvent, string agentId)
    {
        _changeset.Add(diffEvent.Diff with { AgentId = agentId });

        var hunks = DiffParser.ParseSimpleDiff(
            diffEvent.Diff.DiffText,
            diffEvent.Diff.FilePath,
            agentId);
        _changeset.AddHunks(hunks);
    }

    /// <summary>Clear shared state between sessions.</summary>
    public void Reset()
    {
        _changeset.Clear();
        _context.Clear();
        _mailbox.Clear();
    }
}
