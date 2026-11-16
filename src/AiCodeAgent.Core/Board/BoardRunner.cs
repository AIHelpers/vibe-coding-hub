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
    private readonly string _projectDirectory;
    private readonly Func<BoardCard, AgentOptions, AgentOptions>? _configureOptions;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    // `git worktree add` and the snapshot of uncommitted work touch the repository's shared files: one at a time.
    private readonly SemaphoreSlim _acquireGate = new(1, 1);

    /// <param name="configureOptions">Lets the host set the model, a budget or other options for a card's run.</param>
    public BoardRunner(
        TaskBoard board, IWorkspaceIsolation isolation, IBoardAgentRunner agent, string projectDirectory,
        Func<BoardCard, AgentOptions, AgentOptions>? configureOptions = null, ILogger? logger = null)
    {
        _board = board;
        _isolation = isolation;
        _agent = agent;
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
            _board.Report(card.Id, c => c with { LastAction = "Preparing a worktree…" });
            lease = await AcquireAsync(card, token).ConfigureAwait(false);
            if (lease == null)
            {
                state.Error = "Could not create a git worktree for this card. Mission Control needs a git repository with at least one commit.";
            }
            else
            {
                _board.Update(card.Id, c => c with { Branch = lease.Branch, WorktreePath = lease.RootPath, LastAction = "Starting the agent…" });
                await DriveAgentAsync(card, lease, state, token).ConfigureAwait(false);
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

    private async Task DriveAgentAsync(BoardCard card, WorkspaceLease lease, RunState state, CancellationToken token)
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

        await foreach (var evt in _agent.RunAsync(card.Prompt, SessionIdOf(card), options, token).WithCancellation(token).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ToolCallStartEvent start:
                    state.ToolCalls++;
                    var action = Describe(start.Call);
                    var calls = state.ToolCalls;
                    _board.Report(card.Id, c => c with { LastAction = action, ToolCalls = calls });
                    break;

                case TokenUsageEvent usage:
                    state.Cost = usage.SessionCostUsd;
                    state.Tokens = usage.Usage.TotalTokens + usage.Usage.CacheReadTokens + usage.Usage.CacheCreationTokens;
                    _board.Report(card.Id, c => c with { CostUsd = card.CostUsd + state.Cost, Tokens = card.Tokens + state.Tokens });
                    break;

                case ApprovalRequestEvent approval:
                    approval.Approval.TrySetResult(false);
                    break;

                case AgentFinishedEvent finished:
                    state.FinalText = finished.Response.Content;
                    if (finished.Response.WasCancelled) state.Cancelled = true;
                    break;

                case AgentErrorEvent error:
                    state.Error = error.Error.Message;
                    break;
            }
        }
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
        public long Tokens;
    }

    private sealed class ActiveRun : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;
        public Task Task { get; set; } = null!;
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
