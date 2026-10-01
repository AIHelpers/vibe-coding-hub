using System.Threading.Channels;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Decoupled event bus using per-subscriber broadcast channels for agent
/// event delivery. Each call to <see cref="GetEventsAsync"/> returns an
/// independent unbounded channel, and <see cref="Publish"/> writes every
/// event to all active subscriber channels. This guarantees that concurrent
/// consumers (e.g. the chat UI and the session recorder) each receive the
/// full event stream instead of having events distributed/split among them.
/// </summary>
public class AgentEventBus : IAgentEventBus, IDisposable
{
    private readonly List<Channel<AgentEvent>> _subscribers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    public AgentEventBus()
    {
        _cts = new CancellationTokenSource();
    }

    public void Publish(AgentEvent evt)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            foreach (var channel in _subscribers)
            {
                // TryWrite never blocks for an unbounded channel and returns
                // false only if the channel is completed/closed.
                channel.Writer.TryWrite(evt);
            }
        }
    }

    public IAsyncEnumerable<AgentEvent> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        // Each subscriber gets its own unbounded channel so it receives the
        // full broadcast stream. Removing the subscriber on cancellation
        // prevents orphaned channels from accumulating leaked events.
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            if (_disposed)
            {
                linkedCts.Dispose();
                channel.Writer.TryComplete();
                return channel.Reader.ReadAllAsync(cancellationToken);
            }

            _subscribers.Add(channel);
        }

        // Register cleanup so the subscriber's channel is removed when the
        // consumer stops reading (cancellation or disposal).
        linkedCts.Token.Register(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
            linkedCts.Dispose();
        });

        return channel.Reader.ReadAllAsync(linkedCts.Token);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var channel in _subscribers)
                channel.Writer.TryComplete();
            _subscribers.Clear();
        }

        _cts.Cancel();
        _cts.Dispose();
    }
}