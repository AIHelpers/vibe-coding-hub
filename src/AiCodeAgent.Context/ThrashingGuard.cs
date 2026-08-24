using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Context;

/// <summary>
/// Prevents repeated compaction cycles ("thrashing") when a session
/// keeps refilling the context window immediately after compaction.
/// </summary>
public class ThrashingGuard : IThrashingGuard
{
    private readonly ConcurrentDictionary<string, ThrashingState> _states = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _window;

    public ThrashingGuard(int maxAttempts = 3, TimeSpan? window = null)
    {
        _maxAttempts = maxAttempts;
        _window = window ?? TimeSpan.FromMinutes(2);
    }

    public bool TryBeginCompaction(string sessionId)
    {
        var state = _states.GetOrAdd(sessionId, _ => new ThrashingState());
        lock (state)
        {
            var now = DateTime.UtcNow;
            // Reset window if enough time has passed since last compaction
            if (now - state.LastCompaction > _window)
            {
                state.ConsecutiveCount = 0;
            }

            if (state.ConsecutiveCount >= _maxAttempts)
            {
                state.IsThrashing = true;
                return false;
            }

            state.IsThrashing = false;
            return true;
        }
    }

    public void RecordCompaction(string sessionId)
    {
        var state = _states.GetOrAdd(sessionId, _ => new ThrashingState());
        lock (state)
        {
            state.ConsecutiveCount++;
            state.LastCompaction = DateTime.UtcNow;
        }
    }

    public void RecordFailure(string sessionId)
    {
        var state = _states.GetOrAdd(sessionId, _ => new ThrashingState());
        lock (state)
        {
            // A failure doesn't increment the thrashing counter,
            // but we note the attempt time.
            state.LastCompaction = DateTime.UtcNow;
        }
    }

    public bool IsThrashing(string sessionId)
    {
        if (!_states.TryGetValue(sessionId, out var state))
            return false;
        lock (state) return state.IsThrashing;
    }

    public void Reset(string sessionId)
    {
        if (_states.TryRemove(sessionId, out _)) { }
    }

    private class ThrashingState
    {
        public int ConsecutiveCount;
        public DateTime LastCompaction = DateTime.MinValue;
        public bool IsThrashing;
    }
}