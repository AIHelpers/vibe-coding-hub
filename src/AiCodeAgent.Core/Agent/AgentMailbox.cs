using System.Collections.Concurrent;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Thread-safe message bus for inter-agent communication in multi-agent
/// sessions. Agents post messages with the <c>send_message</c> tool; the
/// coordinator drains an agent's pending messages (direct + broadcast) into
/// its prompt before the agent's step starts. Parallel agents can therefore
/// exchange information without sharing a context window.
/// </summary>
public class AgentMailbox
{
    /// <summary>Wildcard recipient that addresses every agent in the session.</summary>
    public const string BroadcastId = "*";

    private readonly ConcurrentQueue<AgentMailboxMessage> _broadcasts = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentMailboxMessage>> _perAgent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total number of undelivered messages (direct + broadcast).</summary>
    public int PendingCount
    {
        get
        {
            var pending = 0;
            foreach (var (_, queue) in _perAgent)
                pending += queue.Count;

            return pending;
        }
    }

    /// <summary>
    /// Post a message to a recipient. A broadcast ("*") is stored once per
    /// known agent; agents not yet known receive it when they are registered
    /// (via <see cref="EnsureAgent"/>) before delivery.
    /// </summary>
    public AgentMailboxMessage Post(string fromAgentId, string toAgentId, string content)
    {
        if (string.IsNullOrWhiteSpace(toAgentId))
            toAgentId = BroadcastId;

        var message = new AgentMailboxMessage
        {
            FromAgentId = fromAgentId,
            ToAgentId = toAgentId.Trim(),
            Content = content
        };

        if (message.IsBroadcast)
        {
            // Store the broadcast once per agent queue so each agent drains
            // its own copy without affecting others.
            foreach (var (_, queue) in _perAgent)
                queue.Enqueue(message);
            _broadcasts.Enqueue(message);
        }
        else
        {
            GetQueue(message.ToAgentId).Enqueue(message);
        }

        return message;
    }

    /// <summary>Register an agent id so it starts receiving broadcasts.</summary>
    public void EnsureAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;

        var queue = GetQueue(agentId);

        // Replay any broadcasts that arrived before the agent was registered.
        foreach (var broadcast in _broadcasts.ToArray())
            queue.Enqueue(broadcast);
    }

    /// <summary>
    /// Drain all pending messages addressed to an agent (direct + broadcasts),
    /// oldest first. Messages are removed from the mailbox once drained.
    /// </summary>
    public IReadOnlyList<AgentMailboxMessage> Drain(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return Array.Empty<AgentMailboxMessage>();

        var result = new List<AgentMailboxMessage>();

        if (_perAgent.TryGetValue(agentId, out var queue))
        {
            while (queue.TryDequeue(out var message))
                result.Add(message);
        }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    /// <summary>Peek at an agent's pending messages without draining them.</summary>
    public IReadOnlyList<AgentMailboxMessage> Peek(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) ||
            !_perAgent.TryGetValue(agentId, out var queue))
        {
            return Array.Empty<AgentMailboxMessage>();
        }

        var items = queue.ToArray();
        Array.Sort(items, (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return items;
    }

    /// <summary>Remove all pending messages (used when a session is reset).</summary>
    public void Clear()
    {
        _broadcasts.Clear();
        _perAgent.Clear();
    }

    private ConcurrentQueue<AgentMailboxMessage> GetQueue(string agentId) =>
        _perAgent.GetOrAdd(agentId, _ => new ConcurrentQueue<AgentMailboxMessage>());
}