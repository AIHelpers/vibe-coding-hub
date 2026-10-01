using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// Records agent events (tool calls, diffs, checkpoints, roles) into a
/// <see cref="SessionBundle"/> as a session runs. The bundle can later be
/// exported via <see cref="SessionExporter"/>.
/// </summary>
public class SessionRecorder
{
    private readonly IAgentEventBus _eventBus;
    private readonly ILogger<SessionRecorder> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private TaskCompletionSource<bool>? _listeningTcs;

    public SessionBundle Bundle { get; } = new();

    public SessionRecorder(IAgentEventBus eventBus, ILogger<SessionRecorder> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>Whether the recorder is currently listening to the event bus.</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _listenTask != null; }
    }

    /// <summary>Begin recording events from the bus into the bundle. Idempotent.</summary>
    public void Start(string sessionId)
    {
        _ = StartAsync(sessionId);
    }

    /// <summary>
    /// Begin recording and await until the listener is actively reading the
    /// event bus so no events published after this call returns are missed.
    /// </summary>
    public async Task StartAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Task? listeningTask;
        lock (_lock)
        {
            if (_listenTask != null)
                return;

            Bundle.SessionId = sessionId;
            Bundle.CreatedAt = DateTime.UtcNow;

            _cts = new CancellationTokenSource();
            _listeningTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _listenTask = Task.Run(() => ListenAsync(_cts.Token));
            listeningTask = _listeningTcs.Task;
        }

        // Wait until the listener has subscribed to the bus (bounded only by the token).
        await listeningTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait until the recorder is actively listening to the event bus.
    /// Useful for tests/hosts that publish events right after <see cref="Start"/>.
    /// </summary>
    public Task WaitUntilListeningAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_listenTask == null || _listeningTcs == null)
                return Task.CompletedTask;
            return _listeningTcs.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Stop recording. Can be restarted with <see cref="Start"/> for a new session.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;
            _listeningTcs = null;
        }
    }

    /// <summary>
    /// Return a copy of the current bundle without stopping the recorder.
    /// </summary>
    public SessionBundle GetSnapshot()
    {
        lock (_lock)
        {
            return new SessionBundle
            {
                SchemaVersion = Bundle.SchemaVersion,
                SessionId = Bundle.SessionId,
                CreatedAt = Bundle.CreatedAt,
                Conversation = Bundle.Conversation.ToList(),
                ToolCallLog = Bundle.ToolCallLog.ToList(),
                Changeset = Bundle.Changeset == null
                    ? null
                    : new ChangesetBundleDto
                    {
                        Entries = Bundle.Changeset.Entries.ToList(),
                        Hunks = Bundle.Changeset.Hunks.ToList()
                    },
                CheckpointRefs = Bundle.CheckpointRefs.ToList(),
                CheckpointSnapshots = Bundle.CheckpointSnapshots.ToList(),
                RolesUsed = Bundle.RolesUsed.ToList(),
                PromptPack = Bundle.PromptPack
            };
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        // Signal that the listener is now actively reading the bus
        lock (_lock)
        {
            _listeningTcs?.TrySetResult(true);
        }

        try
        {
            await foreach (var evt in _eventBus.GetEventsAsync(token))
            {
                Record(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session recorder failed while listening to event bus");
        }
    }

    private void Record(AgentEvent evt)
    {
        // Unwrap tagged events to capture agent/role attribution
        string? agentId = null;
        string? role = null;
        if (evt is AgentTaggedEvent tagged)
        {
            agentId = tagged.AgentId;
            role = tagged.Role;
            evt = tagged.Inner;
        }

        switch (evt)
        {
            case ToolCallStartEvent start:
                lock (_lock)
                {
                    Bundle.ToolCallLog.Add(new ToolCallLogEntryDto
                    {
                        ToolCallId = start.Call.Id,
                        ToolName = start.Call.Name,
                        Arguments = start.Call.Arguments,
                        Timestamp = DateTime.UtcNow,
                        AgentId = agentId,
                        Role = role
                    });
                }
                break;

            case ToolCallEndEvent end:
                lock (_lock)
                {
                    var existing = Bundle.ToolCallLog.FirstOrDefault(e => e.ToolCallId == end.Call.Id);
                    if (existing != null)
                    {
                        var index = Bundle.ToolCallLog.IndexOf(existing);
                        Bundle.ToolCallLog[index] = new ToolCallLogEntryDto
                        {
                            ToolCallId = existing.ToolCallId,
                            ToolName = existing.ToolName,
                            Arguments = existing.Arguments,
                            Output = end.Result.Content,
                            IsError = end.Result.IsError,
                            Duration = end.Duration,
                            Timestamp = existing.Timestamp,
                            AgentId = existing.AgentId,
                            Role = existing.Role
                        };
                    }
                }
                break;

            case DiffProducedEvent diff:
                lock (_lock)
                {
                    Bundle.Changeset ??= new ChangesetBundleDto();
                    Bundle.Changeset.Entries.Add(diff.Diff);
                    var hunks = DiffParser.ParseSimpleDiff(diff.Diff.DiffText, diff.Diff.FilePath, agentId ?? diff.Diff.AgentId);
                    Bundle.Changeset.Hunks.AddRange(hunks);
                }
                break;

            case CheckpointCreatedEvent checkpoint:
                lock (_lock)
                {
                    Bundle.CheckpointRefs.Add(checkpoint.Checkpoint.CheckpointId);
                    Bundle.CheckpointSnapshots.Add(new CheckpointSnapshotDto
                    {
                        CheckpointId = checkpoint.Checkpoint.CheckpointId,
                        FilePath = checkpoint.Checkpoint.FilePath,
                        OriginalContent = checkpoint.Checkpoint.OriginalContent,
                        Timestamp = checkpoint.Checkpoint.Timestamp,
                        TurnId = checkpoint.Checkpoint.TurnId,
                        SessionId = checkpoint.Checkpoint.SessionId
                    });
                }
                break;
        }

        // Track roles used
        if (!string.IsNullOrEmpty(role))
        {
            lock (_lock)
            {
                if (!Bundle.RolesUsed.Contains(role, StringComparer.OrdinalIgnoreCase))
                    Bundle.RolesUsed.Add(role);
            }
        }
    }
}