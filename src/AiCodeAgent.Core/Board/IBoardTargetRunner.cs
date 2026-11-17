using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Board;

/// <summary>What a card hands to its agent: the task, the session it belongs to and the options (worktree, permissions) of the run.</summary>
public sealed record BoardRunRequest(string Prompt, string SessionId, AgentOptions Options);

/// <summary>
/// What a run that does not stream events by itself (a flow, an external agent) reports while it works: the last thing it did,
/// and its cost and tokens when it knows them. The numbers are totals of the run so far, not increments.
/// </summary>
public sealed record BoardProgressEvent(string? Action = null, bool IsToolCall = false, decimal? CostUsd = null, long? Tokens = null) : AgentEvent;

/// <summary>
/// Runs a card with an agent other than the default one (E11-S2). Every runner answers with the same stream of events the board
/// already understands, ending with an <see cref="AgentFinishedEvent"/> or an <see cref="AgentErrorEvent"/>.
/// </summary>
public interface IBoardTargetRunner
{
    bool Supports(AgentTargetKind kind);

    /// <summary>
    /// Checks that the target can run before a worktree is made for it (the character exists, the program is installed...).
    /// Returns what is wrong in a sentence the person can act on, or null when the target is ready.
    /// </summary>
    Task<string?> CheckAsync(AgentTarget target, CancellationToken cancellationToken);

    IAsyncEnumerable<AgentEvent> RunAsync(AgentTarget target, BoardRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a running card of this target can be paused, messaged and redirected (E11-S3): true when a new turn in the same
    /// session continues the earlier work. Programs that start from scratch on every call (flows, external agents) can only be stopped.
    /// </summary>
    Task<bool> CanSteerAsync(AgentTarget target, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Sends each target to the runner of its kind.</summary>
public sealed class BoardTargetRouter : IBoardTargetRunner
{
    private readonly IReadOnlyList<IBoardTargetRunner> _runners;

    public BoardTargetRouter(params IBoardTargetRunner[] runners) => _runners = runners;

    public bool Supports(AgentTargetKind kind) => kind == AgentTargetKind.Default || _runners.Any(r => r.Supports(kind));

    public Task<string?> CheckAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        if (target.Problem is { } problem) return Task.FromResult<string?>(problem);
        var runner = RunnerFor(target.Kind);
        return runner == null
            ? Task.FromResult<string?>($"{target.DisplayName} cannot run on the board in this build.")
            : runner.CheckAsync(target, cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentTarget target, BoardRunRequest request, CancellationToken cancellationToken = default) =>
        (RunnerFor(target.Kind) ?? throw new InvalidOperationException($"{target.DisplayName} cannot run on the board in this build."))
            .RunAsync(target, request, cancellationToken);

    public Task<bool> CanSteerAsync(AgentTarget target, CancellationToken cancellationToken) =>
        RunnerFor(target.Kind) is { } runner ? runner.CanSteerAsync(target, cancellationToken) : Task.FromResult(false);

    private IBoardTargetRunner? RunnerFor(AgentTargetKind kind) => _runners.FirstOrDefault(r => r.Supports(kind));
}

/// <summary>Turns work that reports through callbacks into the event stream the board reads.</summary>
internal static class BoardEventStream
{
    /// <summary>
    /// Runs <paramref name="work"/> and yields what it emits. An exception becomes an <see cref="AgentErrorEvent"/>; cancelling
    /// ends the stream with a cancelled <see cref="AgentFinishedEvent"/>, as an agent run does.
    /// </summary>
    public static async IAsyncEnumerable<AgentEvent> From(
        Func<Action<AgentEvent>, CancellationToken, Task> work, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var task = Task.Run(async () =>
        {
            try
            {
                await work(e => channel.Writer.TryWrite(e), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                channel.Writer.TryWrite(new AgentFinishedEvent(new AgentResponse { WasCancelled = true }));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new AgentErrorEvent(ex));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            yield return e;
        await task.ConfigureAwait(false);
    }
}
