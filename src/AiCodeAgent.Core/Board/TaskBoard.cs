namespace AiCodeAgent.Core.Board;

public enum BoardChangeKind { Added, Updated, Removed }

public sealed class BoardChangedEventArgs : EventArgs
{
    public BoardChangedEventArgs(BoardChangeKind kind, BoardCard card) { Kind = kind; Card = card; }
    public BoardChangeKind Kind { get; }
    /// <summary>The card after the change (for <see cref="BoardChangeKind.Removed"/>, the card as it was).</summary>
    public BoardCard Card { get; }
}

/// <summary>
/// The cards of one project's board and the rules for moving them between the columns:
/// Backlog → Running → Needs review → Done, and back to the Backlog from Needs review or Done.
/// It does not run anything (see <see cref="BoardRunner"/>). Thread-safe: agents report progress from background threads.
/// </summary>
public sealed class TaskBoard
{
    private readonly object _gate = new();
    private readonly List<BoardCard> _cards;
    private readonly BoardStore? _store;
    private readonly TimeProvider _clock;

    public TaskBoard(BoardStore? store = null, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _cards = store?.Load().ToList() ?? new List<BoardCard>();
    }

    /// <summary>Raised after a card was added, changed or removed, on the thread that made the change.</summary>
    public event EventHandler<BoardChangedEventArgs>? Changed;

    /// <summary>A snapshot of all cards, oldest first.</summary>
    public IReadOnlyList<BoardCard> Cards
    {
        get { lock (_gate) return _cards.ToList(); }
    }

    public BoardCard? Get(string id)
    {
        lock (_gate) return _cards.FirstOrDefault(c => c.Id == id);
    }

    public int Count(BoardColumn column)
    {
        lock (_gate) return _cards.Count(c => c.Column == column);
    }

    /// <summary>Adds a card to the Backlog. An empty title is taken from the first line of the prompt; no agent means the default one.</summary>
    public BoardCard Add(string prompt, string? title = null, AgentTarget? agent = null)
    {
        var text = (prompt ?? string.Empty).Trim();
        if (text.Length == 0) throw new ArgumentException("A card needs a task.", nameof(prompt));
        var card = new BoardCard
        {
            Prompt = text,
            Title = string.IsNullOrWhiteSpace(title) ? BoardCard.TitleFrom(text) : title.Trim(),
            CreatedAt = _clock.GetUtcNow(),
            Agent = agent ?? AgentTarget.Default
        };
        lock (_gate)
        {
            _cards.Add(card);
            Persist();
        }
        Raise(BoardChangeKind.Added, card);
        return card;
    }

    /// <summary>Removes a card that is not running (stop it first). The branch and worktree of its runs are not touched.</summary>
    public bool Remove(string id)
    {
        BoardCard? removed;
        lock (_gate)
        {
            removed = _cards.FirstOrDefault(c => c.Id == id);
            if (removed == null || removed.IsRunning) return false;
            _cards.Remove(removed);
            Persist();
        }
        Raise(BoardChangeKind.Removed, removed);
        return true;
    }

    /// <summary>Chooses who does the work of a card that is not running; the next run uses it. Null when the card is missing or running.</summary>
    public BoardCard? SetAgent(string id, AgentTarget agent) =>
        Move(id, c => !c.IsRunning, c => c with { Agent = agent ?? AgentTarget.Default });

    /// <summary>Backlog → Running. Null when the card is missing or not in the Backlog.</summary>
    public BoardCard? BeginRun(string id) => Move(id, c => c.Column == BoardColumn.Backlog, c => c with
    {
        Column = BoardColumn.Running,
        Outcome = CardOutcome.None,
        StartedAt = _clock.GetUtcNow(),
        FinishedAt = null,
        Runs = c.Runs + 1,
        Branch = null,
        WorktreePath = null,
        LastAction = null,
        Summary = null,
        ToolCalls = 0,
        IsPaused = false,
        CanSteer = false
    });

    /// <summary>Running → Needs review, with how the run ended. <paramref name="change"/> sets the final fields (branch, summary, cost).</summary>
    public BoardCard? EndRun(string id, CardOutcome outcome, Func<BoardCard, BoardCard>? change = null) =>
        Move(id, c => c.Column == BoardColumn.Running, c => (change?.Invoke(c) ?? c) with
        {
            Column = BoardColumn.NeedsReview,
            Outcome = outcome,
            IsPaused = false,
            CanSteer = false,
            FinishedAt = _clock.GetUtcNow()
        });

    /// <summary>Needs review → Done: a human accepted the result.</summary>
    public BoardCard? MarkDone(string id) =>
        Move(id, c => c.Column == BoardColumn.NeedsReview, c => c with { Column = BoardColumn.Done });

    /// <summary>Needs review or Done → Backlog, to run the card again. The earlier branch stays; a new run starts on a new one.</summary>
    public BoardCard? ReturnToBacklog(string id) =>
        Move(id, c => c.Column is BoardColumn.NeedsReview or BoardColumn.Done, c => c with { Column = BoardColumn.Backlog });

    /// <summary>
    /// Records progress of a running card (last action, cost, tokens). Kept in memory only: it changes many times a second and
    /// the next state change saves the board anyway.
    /// </summary>
    public BoardCard? Report(string id, Func<BoardCard, BoardCard> change) =>
        Move(id, c => c.Column == BoardColumn.Running, change, persist: false);

    /// <summary>Saves a change to a card the runner makes during a run (for example its branch), without moving it.</summary>
    public BoardCard? Update(string id, Func<BoardCard, BoardCard> change) =>
        Move(id, _ => true, change, persist: true);

    /// <summary>
    /// After a restart no agent is running, so every card the file still shows as Running was cut off: it goes to Needs review
    /// as Interrupted. Returns how many cards were changed.
    /// </summary>
    public int RecoverInterrupted()
    {
        var changed = new List<BoardCard>();
        lock (_gate)
        {
            for (var i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].Column != BoardColumn.Running) continue;
                _cards[i] = _cards[i] with
                {
                    Column = BoardColumn.NeedsReview,
                    Outcome = CardOutcome.Interrupted,
                    IsPaused = false,
                    CanSteer = false,
                    FinishedAt = _clock.GetUtcNow(),
                    Summary = "The app was closed while this card ran. Work that was not committed is still in the worktree folder."
                };
                changed.Add(_cards[i]);
            }
            if (changed.Count > 0) Persist();
        }
        foreach (var card in changed) Raise(BoardChangeKind.Updated, card);
        return changed.Count;
    }

    private BoardCard? Move(string id, Func<BoardCard, bool> allowed, Func<BoardCard, BoardCard> change, bool persist = true)
    {
        BoardCard updated;
        lock (_gate)
        {
            var index = _cards.FindIndex(c => c.Id == id);
            if (index < 0 || !allowed(_cards[index])) return null;
            updated = change(_cards[index]) with { Id = id };
            _cards[index] = updated;
            if (persist) Persist();
        }
        Raise(BoardChangeKind.Updated, updated);
        return updated;
    }

    private void Persist() => _store?.TrySave(_cards);

    private void Raise(BoardChangeKind kind, BoardCard card) => Changed?.Invoke(this, new BoardChangedEventArgs(kind, card));
}
