using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.Services;

/// <summary>
/// Lets the Mission Control board drive agents through the app's <see cref="AgentService"/> (project memory, configured defaults,
/// audit trail and cost tracking included). The agent service publishes what a run does on the shared event bus, tagged with the
/// session id; this adapter subscribes before the run starts and hands that session's events back as a stream.
/// </summary>
public sealed class AgentServiceBoardRunner : IBoardAgentRunner
{
    /// <summary>How long to wait for the closing event after the run call returned, before giving up on it.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    private readonly IAgentEventBus _bus;
    private readonly Func<string, string, AgentOptions, CancellationToken, Task> _run;
    private readonly TimeSpan _grace;

    public AgentServiceBoardRunner(AgentService agentService)
        : this(agentService.EventBus, (prompt, session, options, ct) => agentService.StreamMessageAsync(prompt, session, options, ct))
    {
    }

    internal AgentServiceBoardRunner(IAgentEventBus bus, Func<string, string, AgentOptions, CancellationToken, Task> run, TimeSpan? grace = null)
    {
        _bus = bus;
        _run = run;
        _grace = grace ?? Grace;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string prompt, string sessionId, AgentOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var listening = new CancellationTokenSource();
        // Subscribing happens here, before the run publishes anything, so no early event is missed.
        var events = _bus.GetEventsAsync(listening.Token);
        var run = Task.Run(() => _run(prompt, sessionId, options, cancellationToken), CancellationToken.None);
        var runEnded = run.ContinueWith(static _ => { }, TaskScheduler.Default); // the run reports its errors as events

        var enumerator = events.GetAsyncEnumerator(CancellationToken.None);
        var next = enumerator.MoveNextAsync().AsTask();
        var runFinished = false;
        try
        {
            while (true)
            {
                if (!runFinished)
                {
                    if (await Task.WhenAny(next, runEnded).ConfigureAwait(false) == runEnded)
                    {
                        runFinished = true;
                        continue;
                    }
                }
                else if (await Task.WhenAny(next, Task.Delay(_grace)).ConfigureAwait(false) != next)
                {
                    break; // the run is over and nothing more arrives
                }

                if (!await next.ConfigureAwait(false)) break; // the bus was closed
                if (enumerator.Current is SessionScopedEvent scoped && string.Equals(scoped.SessionId, sessionId, StringComparison.Ordinal))
                {
                    yield return scoped.Inner;
                    if (scoped.Inner is AgentFinishedEvent or AgentErrorEvent) break;
                }
                next = enumerator.MoveNextAsync().AsTask();
            }
        }
        finally
        {
            // Stop listening first: the enumerator cannot be disposed while a read is still waiting for an event.
            listening.Cancel();
            try { await next.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected: the subscription was just cancelled */ }
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (NotSupportedException) { /* the channel reader's iterator cannot always be disposed; the subscription is already gone */ }
        }

        await runEnded.ConfigureAwait(false);
    }
}
