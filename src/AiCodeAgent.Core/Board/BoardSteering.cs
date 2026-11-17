namespace AiCodeAgent.Core.Board;

/// <summary>What a message to a running card means to its agent (E11-S3).</summary>
public enum SteerKind
{
    /// <summary>Extra information or a request; the agent takes it into account and carries on.</summary>
    Message,
    /// <summary>A change of direction; the agent drops its current approach and does this instead.</summary>
    Redirect
}

/// <summary>
/// The control channel of one running card: pause, resume, message and redirect (E11-S3). A card's work is a series of turns in
/// one agent session. Pausing or messaging interrupts the turn that is running; the next turn continues the same conversation,
/// so the agent keeps what it has done and learned. Commands may arrive at any moment, also between two turns; none is lost.
/// </summary>
internal sealed class BoardSteering
{
    internal const string ResumePrompt = "Continue where you left off.";

    private readonly object _gate = new();
    private bool _closed;
    private bool _paused;
    private bool _interrupted;
    private string? _pending;
    private CancellationTokenSource? _turn;
    private TaskCompletionSource _wake = NewWake();

    public BoardSteering(bool enabled) => Enabled = enabled;

    /// <summary>False for agents that can only be stopped; every command then fails.</summary>
    public bool Enabled { get; }

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>The turn about to start. A command that arrived since the last turn ended this one before it starts.</summary>
    public void BeginTurn(string prompt, CancellationTokenSource turn)
    {
        var cancel = false;
        lock (_gate)
        {
            _turn = turn;
            _interrupted = false;
            if (Enabled && (_paused || _pending != null))
            {
                // What this turn was asked is kept in front of what came in, so nothing is dropped.
                _pending = _pending == null ? prompt : prompt + "\n\n" + _pending;
                _interrupted = true;
                cancel = true;
            }
        }
        if (cancel) Cancel(turn);
    }

    /// <summary>The turn ended. True when a command interrupted it: its result is not an answer.</summary>
    public bool EndTurn()
    {
        lock (_gate)
        {
            var interrupted = _interrupted;
            _interrupted = false;
            _turn = null;
            return interrupted;
        }
    }

    public bool Pause()
    {
        CancellationTokenSource? turn;
        lock (_gate)
        {
            if (!Accepts || _paused) return false;
            _paused = true;
            if (_wake.Task.IsCompleted) _wake = NewWake();
            turn = Interrupt();
        }
        Cancel(turn);
        return true;
    }

    public bool Resume()
    {
        lock (_gate)
        {
            if (!Accepts || !_paused) return false;
            _paused = false;
            _pending ??= ResumePrompt;
            _wake.TrySetResult();
        }
        return true;
    }

    /// <summary>Queues a message for the agent, interrupting the turn that is running. It also ends a pause.</summary>
    public bool Post(string prompt)
    {
        CancellationTokenSource? turn;
        lock (_gate)
        {
            if (!Accepts) return false;
            _paused = false;
            _pending = _pending == null || _pending == ResumePrompt ? prompt : _pending + "\n\n" + prompt;
            turn = Interrupt();
            _wake.TrySetResult();
        }
        Cancel(turn);
        return true;
    }

    /// <summary>
    /// What the agent does next: the queued prompt, waiting while paused; null when the card has nothing more to do.
    /// After null no command is accepted.
    /// </summary>
    public async Task<string?> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_closed) return null;
                if (_paused) wait = _wake.Task;
                else if (_pending != null)
                {
                    var prompt = _pending;
                    _pending = null;
                    return prompt;
                }
                else
                {
                    _closed = true;
                    return null;
                }
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _wake.TrySetResult();
        }
    }

    /// <summary>The prompt that carries a message from the person to the agent.</summary>
    public static string Compose(SteerKind kind, string text) => kind == SteerKind.Redirect
        ? "Change of direction from the person who owns this task. Stop the approach you are on and do this instead:\n"
          + text + "\n\nKeep the files you already changed unless they conflict with the new direction."
        : "A message from the person who owns this task:\n"
          + text + "\n\nTake it into account and carry on with the task.";

    private bool Accepts => Enabled && !_closed;

    private CancellationTokenSource? Interrupt()
    {
        if (_turn != null) _interrupted = true;
        return _turn;
    }

    private static void Cancel(CancellationTokenSource? turn)
    {
        try { turn?.Cancel(); }
        catch (ObjectDisposedException) { /* the turn already ended */ }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
