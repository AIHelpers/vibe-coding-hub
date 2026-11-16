using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Board;

/// <summary>
/// Runs one agent task and streams what happens, so the board does not depend on how the host drives agents
/// (the desktop app goes through its agent service, which adds project memory, defaults, audit and cost tracking).
/// </summary>
public interface IBoardAgentRunner
{
    /// <summary>
    /// Runs <paramref name="prompt"/> in <paramref name="sessionId"/> and yields its events (unwrapped, in order) until the run
    /// ends. Cancelling <paramref name="cancellationToken"/> stops the agent; the stream then ends with a cancelled
    /// <see cref="AgentFinishedEvent"/> or simply ends.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string sessionId, AgentOptions options, CancellationToken cancellationToken);
}
