using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Board;

/// <summary>Runs a flow to the end with nobody at the keyboard (<see cref="FlowHeadlessRunner"/> is the usual implementation).</summary>
public delegate Task<FlowHeadlessOutcome> FlowExecutor(
    SdlcPipelineDefinition pipeline, string task, string workingDirectory, FlowHeadlessOptions options, CancellationToken cancellationToken);

/// <summary>
/// Runs a card as a flow: the card's task is the flow's task, and the flow's steps work in the card's worktree. The run is
/// unattended, so a tool that needs approval is denied and a step that waits for a person stops the flow with a clear reason,
/// exactly as in a CI run. Progress shows as the step that is running; cost and tokens arrive when the flow ends.
/// </summary>
public sealed class FlowBoardRunner : IBoardTargetRunner
{
    private readonly Func<string, SdlcPipelineDefinition?> _lookup;
    private readonly FlowExecutor _execute;
    private readonly ICharacterRegistry? _characters;

    /// <param name="lookup">Finds a flow by name (null when there is none).</param>
    /// <param name="characters">Lets the flow check the characters its steps use.</param>
    public FlowBoardRunner(Func<string, SdlcPipelineDefinition?> lookup, FlowExecutor execute, ICharacterRegistry? characters = null)
    {
        _lookup = lookup;
        _execute = execute;
        _characters = characters;
    }

    public bool Supports(AgentTargetKind kind) => kind == AgentTargetKind.Flow;

    public Task<string?> CheckAsync(AgentTarget target, CancellationToken cancellationToken) =>
        Task.FromResult(Problem(_lookup(target.Id ?? string.Empty), target));

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentTarget target, BoardRunRequest request, CancellationToken cancellationToken = default) =>
        BoardEventStream.From(async (emit, token) =>
        {
            var pipeline = _lookup(target.Id ?? string.Empty);
            if (Problem(pipeline, target) is { } problem) throw new InvalidOperationException(problem);

            emit(new BoardProgressEvent($"Starting flow {pipeline!.Name}…"));
            var options = new FlowHeadlessOptions
            {
                Model = request.Options.Model,
                Permission = request.Options.PermissionMode,
                MaxBudgetUsd = request.Options.MaxBudgetUsd,
                Log = line => emit(new BoardProgressEvent(line)),
                CharacterLookup = _characters == null ? null : id => _characters.GetAsync(id).GetAwaiter().GetResult()
            };

            var outcome = await _execute(pipeline, request.Prompt, request.Options.WorkingDirectory, options, token).ConfigureAwait(false);

            if (outcome.Result is { } result)
            {
                var usage = result.Usage;
                emit(new BoardProgressEvent(CostUsd: usage.CostUsd, Tokens: usage.Tokens));
            }

            if (outcome.Succeeded)
            {
                emit(new AgentFinishedEvent(new AgentResponse { Content = Summary(outcome) }));
            }
            else
            {
                var reason = string.IsNullOrWhiteSpace(outcome.Reason) ? "The flow did not finish." : outcome.Reason.Trim();
                emit(new AgentErrorEvent(new InvalidOperationException(WithDenied(reason, outcome))));
            }
        }, cancellationToken);

    private static string? Problem(SdlcPipelineDefinition? pipeline, AgentTarget target)
    {
        if (pipeline == null) return $"Flow '{target.Id}' was not found.";
        if (!pipeline.IsFlow) return $"'{pipeline.Name}' is a linear pipeline. Mission Control runs flows (pipelines with connections between steps).";
        return null;
    }

    /// <summary>The output of the last step that produced one, followed by the requests that were denied along the way.</summary>
    private static string Summary(FlowHeadlessOutcome outcome)
    {
        var result = outcome.Result;
        var last = result?.Nodes.LastOrDefault(n => n.State == FlowNodeState.Done && !string.IsNullOrWhiteSpace(n.Output))?.Output;
        return WithDenied(string.IsNullOrWhiteSpace(last) ? result?.Message ?? string.Empty : last.Trim(), outcome);
    }

    private static string WithDenied(string text, FlowHeadlessOutcome outcome)
    {
        if (outcome.Denied.Count == 0) return text;
        var denied = string.Join("; ", outcome.Denied.Distinct());
        return text + (text.Length > 0 ? "\n\n" : string.Empty) + "Not allowed while running unattended: " + denied + ".";
    }
}
