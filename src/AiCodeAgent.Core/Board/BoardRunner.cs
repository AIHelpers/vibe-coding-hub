using System.Collections.Concurrent;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Core.Board;

/// <summary>
/// Runs the cards of a <see cref="TaskBoard"/>. Each run gets its own git worktree and branch from
/// <see cref="IWorkspaceIsolation"/>, so cards started together cannot overwrite each other's files. When the agent
/// stops, its work is committed on the card's branch and not merged: merging is a decision for the person reviewing the card.
/// A card that cannot get a worktree (no git repository, no commit yet) fails instead of running in the shared folder.
/// </summary>
public sealed class BoardRunner
{
    private readonly TaskBoard _board;
    private readonly IWorkspaceIsolation _isolation;
    private readonly IBoardAgentRunner _agent;
    private readonly IBoardTargetRunner? _targets;
    private readonly string _projectDirectory;
    private readonly Func<BoardCard, AgentOptions, AgentOptions>? _configureOptions;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    // `git worktree add` and the snapshot of uncommitted work touch the repository's shared files: one at a time.
    private readonly SemaphoreSlim _acquireGate = new(1, 1);

    /// <param name="agent">Runs cards that use the default agent.</param>
    /// <param name="configureOptions">Lets the host set the model, a budget or other options for a card's run.</param>
    /// <param name="targets">Runs cards that are assigned to a character, a flow or an external agent (E11-S2); without it such cards fail with a clear message.</param>
    public BoardRunner(
        TaskBoard board, IWorkspaceIsolation isolation, IBoardAgentRunner agent, string projectDirectory,
        Func<BoardCard, AgentOptions, AgentOptions>? configureOptions = null, ILogger? logger = null,
        IBoardTargetRunner? targets = null)
    {
        _board = board;
        _isolation = isolation;
        _agent = agent;
        _targets = targets;
        _projectDirectory = projectDirectory;
        _configureOptions = configureOptions;
        _logger = logger ?? NullLogger.Instance;
    }

    public int RunningCount => _runs.Count;

    public bool IsRunning(string cardId) => _runs.ContainsKey(cardId);

    /// <summary>
    /// Starts a card from the Backlog and returns at once with the task that completes when the run has ended; null when the
    /// card is missing or not in the Backlog.
    /// </summary>
    public Task? Start(string cardId)
    {
        var card = _board.BeginRun(cardId);
        if (card == null) return null;

        var run = new ActiveRun();
        _runs[cardId] = run;
        run.Task = Task.Run(() => RunCardAsync(card, run));
        return run.Task;
    }

    /// <summary>Starts every card in the Backlog at once; returns how many were started.</summary>
    public int StartAll() =>
        _board.Cards.Where(c => c.Column == BoardColumn.Backlog).Count(c => Start(c.Id) != null);

    /// <summary>Asks a running card's agent to stop. Its work so far is committed on the branch and the card moves to Needs review.</summary>
    public bool Stop(string cardId)
    {
        if (!_runs.TryGetValue(cardId, out var run)) return false;
        run.Cancel();
        return true;
    }

    // ------------------------------------------------------------------ control (E11-S3)

    /// <summary>
    /// Whether the running card's agent can be paused, messaged and redirected. False for flows and external agents (they can only
    /// be stopped), and for a card that is not running.
    /// </summary>
    public bool CanSteer(string cardId) => SteeringOf(cardId) is { Enabled: true };

    /// <summary>Pauses a running card: its agent stops after the step it is on and waits. Nothing is lost; <see cref="Resume"/> continues.</summary>
    public bool Pause(string cardId)
    {
        if (SteeringOf(cardId) is not { } steering || !steering.Pause()) return false;
        _board.Report(cardId, c => c with { IsPaused = true, LastAction = "Pausing…" });
        return true;
    }

    /// <summary>Continues a paused card where it left off.</summary>
    public bool Resume(string cardId)
    {
        if (SteeringOf(cardId) is not { } steering || !steering.Resume()) return false;
        _board.Report(cardId, c => c with { IsPaused = false, LastAction = "Continuing…" });
        return true;
    }

    /// <summary>
    /// Tells a running or paused card's agent something. It breaks off the step the agent is on and continues the same
    /// conversation with the message, so the work done so far is kept. A <see cref="SteerKind.Redirect"/> says to drop the
    /// current approach. Returns false when the card cannot be steered or the text is empty.
    /// </summary>
    public bool Send(string cardId, string text, SteerKind kind = SteerKind.Message)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0 || SteeringOf(cardId) is not { } steering) return false;
        if (!steering.Post(BoardSteering.Compose(kind, text))) return false;
        var shown = text.ReplaceLineEndings(" ");
        if (shown.Length > 80) shown = shown[..80] + "…";
        _board.Report(cardId, c => c with { IsPaused = false, LastAction = (kind == SteerKind.Redirect ? "Redirected: " : "You: ") + shown });
        return true;
    }

    private BoardSteering? SteeringOf(string cardId) => _runs.TryGetValue(cardId, out var run) ? run.Steering : null;

    public void StopAll()
    {
        foreach (var run in _runs.Values) run.Cancel();
    }

    /// <summary>Completes when every run that is active now has ended.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_runs.Values.Select(r => r.Task).Where(t => t != null)!);

    private async Task RunCardAsync(BoardCard card, ActiveRun run)
    {
        var token = run.Token;
        var state = new RunState();
        WorkspaceLease? lease = null;
        try
        {
            // A missing character or an agent that is not installed fails here, before a worktree is made for it.
            var problem = await CheckAgentAsync(card.Agent, token).ConfigureAwait(false);
            if (problem != null)
            {
                state.Error = problem;
            }
            else
            {
                _board.Report(card.Id, c => c with { LastAction = "Preparing a worktree…" });
                lease = await AcquireAsync(card, token).ConfigureAwait(false);
                if (lease == null)
                {
                    state.Error = "Could not create a git worktree for this card. Mission Control needs a git repository with at least one commit.";
                }
                else
                {
                    _board.Update(card.Id, c => c with { Branch = lease.Branch, WorktreePath = lease.RootPath, LastAction = $"Starting {card.Agent.DisplayName}…" });
                    await DriveAgentAsync(card, lease, state, run, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state.Cancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Card {Card} failed", card.Id);
            state.Error = ex.Message;
        }

        if (token.IsCancellationRequested) state.Cancelled = true;
        await SettleAsync(card, lease, state).ConfigureAwait(false);
        _runs.TryRemove(card.Id, out _);
        run.Dispose();
    }

    /// <summary>What stops the card's agent from running, as a sentence the person can act on; null when it is ready.</summary>
    private async Task<string?> CheckAgentAsync(AgentTarget agent, CancellationToken token)
    {
        if (agent.Kind == AgentTargetKind.Default) return null;
        if (_targets == null || !_targets.Supports(agent.Kind))
            return $"{agent.DisplayName} cannot run on the board in this build. Choose the default agent.";
        return await _targets.CheckAsync(agent, token).ConfigureAwait(false);
    }

    /// <summary>The events of the card's run: from the default agent, or from the runner of the card's chosen agent.</summary>
    private IAsyncEnumerable<AgentEvent> StreamAgent(BoardCard card, string prompt, AgentOptions options, CancellationToken token) =>
        card.Agent.Kind == AgentTargetKind.Default
            ? _agent.RunAsync(prompt, SessionIdOf(card), options, token)
            : _targets!.RunAsync(card.Agent, new BoardRunRequest(prompt, SessionIdOf(card), options), token);

    private async Task<bool> CanSteerAsync(AgentTarget agent, CancellationToken token) =>
        agent.Kind == AgentTargetKind.Default || (_targets != null && await _targets.CanSteerAsync(agent, token).ConfigureAwait(false));

    private async Task<WorkspaceLease?> AcquireAsync(BoardCard card, CancellationToken token)
    {
        await _acquireGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await _isolation.AcquireAsync(_projectDirectory, SessionIdOf(card), card.Title, token).ConfigureAwait(false);
        }
        finally
        {
            _acquireGate.Release();
        }
    }

    private async Task DriveAgentAsync(BoardCard card, WorkspaceLease lease, RunState state, ActiveRun run, CancellationToken token)
    {
        var options = new AgentOptions
        {
            WorkingDirectory = lease.WorkingDirectory,
            // The agent may touch its own worktree and nothing else, so parallel cards cannot reach each other's files.
            AllowedPaths = new List<string> { lease.WorkingDirectory },
            // Nobody sits at a card to answer approval prompts: edits go through, anything that would ask is denied at once.
            PermissionMode = PermissionMode.AutoEdit,
            NonInteractive = true
        };
        if (_configureOptions != null) options = _configureOptions(card, options);

        var steering = new BoardSteering(await CanSteerAsync(card.Agent, token).ConfigureAwait(false));
        run.Steering = steering;
        _board.Report(card.Id, c => c with { CanSteer = steering.Enabled });

        try
        {
            // The card's work is one turn, or several when the person pauses, messages or redirects the agent.
            string? prompt = card.Prompt;
            while (prompt != null)
            {
                await RunTurnAsync(card, prompt, options, state, steering, token).ConfigureAwait(false);
                if (state.Error != null || state.Cancelled || token.IsCancellationRequested) break;
                if (steering.IsPaused) _board.Report(card.Id, c => c with { IsPaused = true, LastAction = "Paused. Waiting for you." });
                prompt = await steering.NextAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            steering.Close();
        }
    }

    /// <summary>One turn of the agent: its events until it stops, or until the person interrupts it.</summary>
    private async Task RunTurnAsync(BoardCard card, string prompt, AgentOptions options, RunState state, BoardSteering steering, CancellationToken token)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(token);
        steering.BeginTurn(prompt, turn);

        string? finalText = null, error = null;
        var cancelled = false;
        try
        {
            await foreach (var evt in StreamAgent(card, prompt, options, turn.Token).WithCancellation(turn.Token).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case BoardProgressEvent progress:
                        // Agents that do not stream tool calls (flows, external agents) report what they are doing and their totals.
                        if (progress.IsToolCall) state.ToolCalls++;
                        if (progress.CostUsd is { } cost) state.Cost = cost;
                        if (progress.Tokens is { } tokens) state.TurnTokens = tokens;
                        var progressCalls = state.ToolCalls;
                        var runCost = state.Cost;
                        var runTokens = state.Tokens;
                        _board.Report(card.Id, c => c with
                        {
                            LastAction = string.IsNullOrWhiteSpace(progress.Action) ? c.LastAction : progress.Action,
                            ToolCalls = progressCalls,
                            CostUsd = card.CostUsd + runCost,
                            Tokens = card.Tokens + runTokens
                        });
                        break;

                    case ToolCallStartEvent start:
                        state.ToolCalls++;
                        var action = Describe(start.Call);
                        var calls = state.ToolCalls;
                        _board.Report(card.Id, c => c with { LastAction = action, ToolCalls = calls });
                        break;

                    case TokenUsageEvent usage:
                        state.Cost = usage.SessionCostUsd;
                        state.TurnTokens = usage.Usage.TotalTokens + usage.Usage.CacheReadTokens + usage.Usage.CacheCreationTokens;
                        var spent = state.Cost;
                        var used = state.Tokens;
                        _board.Report(card.Id, c => c with { CostUsd = card.CostUsd + spent, Tokens = card.Tokens + used });
                        break;

                    case ApprovalRequestEvent approval:
                        approval.Approval.TrySetResult(false);
                        break;

                    case AgentFinishedEvent finished:
                        finalText = finished.Response.Content;
                        if (finished.Response.WasCancelled) cancelled = true;
                        break;

                    case AgentErrorEvent failure:
                        error = failure.Error.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            cancelled = true;
        }

        state.TokensDone += state.TurnTokens;
        state.TurnTokens = 0;

        // A turn that a message or a pause broke off is not the card's result; the next turn carries on.
        if (steering.EndTurn() && !token.IsCancellationRequested) return;

        if (finalText != null) state.FinalText = finalText;
        if (cancelled) state.Cancelled = true;
        if (error != null) state.Error = error;
    }

    private async Task SettleAsync(BoardCard card, WorkspaceLease? lease, RunState state)
    {
        WorkspaceMergeResult? result = null;
        if (lease != null)
        {
            try
            {
                // Commit what the agent did on the card's branch; merging is left to the reviewer.
                result = await _isolation.CompleteAsync(lease, card.Title, merge: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Committing the work of card {Card} failed", card.Id);
                result = new WorkspaceMergeResult(WorkspaceMergeOutcome.Failed, lease.Branch, ex.Message, Array.Empty<string>(), lease.RootPath);
            }
        }

        var outcome = state.Cancelled ? CardOutcome.Stopped
            : state.Error != null ? CardOutcome.Failed
            : result?.Outcome switch
            {
                WorkspaceMergeOutcome.NoChanges => CardOutcome.NoChanges,
                WorkspaceMergeOutcome.Kept or WorkspaceMergeOutcome.Merged => CardOutcome.Completed,
                _ => CardOutcome.Failed
            };

        var nothingKept = result?.Outcome == WorkspaceMergeOutcome.NoChanges;
        var summary = state.Error
            ?? (result is { Outcome: WorkspaceMergeOutcome.Failed or WorkspaceMergeOutcome.Conflict } ? result.Message : null)
            ?? (state.Cancelled ? "Stopped by you." : state.FinalText);

        _board.EndRun(card.Id, outcome, c => c with
        {
            Branch = nothingKept ? null : lease?.Branch,
            WorktreePath = nothingKept ? null : result?.RetainedPath ?? lease?.RootPath,
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
            CostUsd = card.CostUsd + state.Cost,
            Tokens = card.Tokens + state.Tokens
        });
    }

    private static string SessionIdOf(BoardCard card) => $"card-{card.Id}-{card.Runs}";

    /// <summary>"edit_file src/Program.cs": the tool and its first text argument, shortened.</summary>
    internal static string Describe(ToolCall call)
    {
        var first = call.Arguments?.Values.OfType<string>().FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (first == null) return call.Name;
        first = first.ReplaceLineEndings(" ").Trim();
        if (first.Length > 80) first = first[..80] + "…";
        return $"{call.Name} {first}";
    }

    private sealed class RunState
    {
        public string? FinalText;
        public string? Error;
        public bool Cancelled;
        public int ToolCalls;
        public decimal Cost;
        /// <summary>Tokens of the turns that ended.</summary>
        public long TokensDone;
        /// <summary>Tokens of the turn that runs now.</summary>
        public long TurnTokens;
        public long Tokens => TokensDone + TurnTokens;
    }

    private sealed class ActiveRun : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;
        public Task Task { get; set; } = null!;
        /// <summary>Set once the agent is ready to take commands; null before that.</summary>
        public BoardSteering? Steering { get; set; }
        public CancellationToken Token => _cts.Token;

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* the run already ended */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _cts.Dispose();
        }
    }
}
