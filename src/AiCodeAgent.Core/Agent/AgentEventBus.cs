using System.Threading.Channels;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Decoupled event bus using Channel<T> for agent event delivery.
/// UI subscribes to events on the dispatcher thread.
/// </summary>
public class AgentEventBus : IAgentEventBus, IDisposable
{
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _cts;

    public AgentEventBus()
    {
        _channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();
    }

    public void Publish(AgentEvent evt)
    {
        _channel.Writer.TryWrite(evt);
    }

    public IAsyncEnumerable<AgentEvent> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        return _channel.Reader.ReadAllAsync(linkedCts.Token);
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}