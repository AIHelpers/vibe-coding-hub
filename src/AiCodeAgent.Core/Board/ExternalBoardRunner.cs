using System.Text;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Board;

/// <summary>
/// Runs a card with a coding agent outside the app (Claude Code, Codex, Gemini CLI or any ACP program), so the subscription the
/// person already pays for does the work. The agent edits files in the card's worktree; its answer becomes the card's summary.
/// Nobody sits at a card, so what the agent asks permission for is decided by the card's permission mode and refused when it would
/// need a person (the refusals are listed in the summary).
/// </summary>
public sealed class ExternalBoardRunner : IBoardTargetRunner
{
    private const string ToolPrefix = "ACP tool: ";

    private readonly IExternalAgentRunner _runner;
    private readonly ExternalAgentDetector _detector;
    private readonly TimeSpan _timeout;

    /// <param name="timeout">How long one card may take; an agent that is stuck is stopped after this.</param>
    public ExternalBoardRunner(IExternalAgentRunner runner, ExternalAgentDetector? detector = null, TimeSpan? timeout = null)
    {
        _runner = runner;
        _detector = detector ?? new ExternalAgentDetector();
        _timeout = timeout ?? TimeSpan.FromMinutes(60);
    }

    public bool Supports(AgentTargetKind kind) => kind == AgentTargetKind.External;

    public Task<string?> CheckAsync(AgentTarget target, CancellationToken cancellationToken) =>
        Task.FromResult(Resolve(target).Error);

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentTarget target, BoardRunRequest request, CancellationToken cancellationToken = default)
    {
        var (command, args, error) = Resolve(target);
        if (error != null) return BoardEventStream.From((_, _) => throw new ExternalAgentException(error), cancellationToken);
        return RunProgramAsync(command!, args, string.Empty, target.DisplayName, request, cancellationToken);
    }

    /// <summary>Runs a program as an ACP agent. <paramref name="instructions"/> (a character's persona and skills) go in front of the task.</summary>
    public IAsyncEnumerable<AgentEvent> RunProgramAsync(
        string command, IReadOnlyList<string> args, string instructions, string label, BoardRunRequest request, CancellationToken cancellationToken) =>
        BoardEventStream.From(async (emit, token) =>
        {
            emit(new BoardProgressEvent($"Starting {label}…"));
            var prompt = string.IsNullOrWhiteSpace(instructions) ? request.Prompt : instructions.Trim() + "\n\n" + request.Prompt;
            var externalRequest = new ExternalAgentRequest(command, args, request.Options.WorkingDirectory, prompt, request.Options.PermissionMode, _timeout);

            var result = await _runner.RunAsync(externalRequest, line =>
            {
                emit(line.StartsWith(ToolPrefix, StringComparison.Ordinal)
                    ? new BoardProgressEvent(line[ToolPrefix.Length..], IsToolCall: true)
                    : new BoardProgressEvent(line));
            }, token).ConfigureAwait(false);

            if (result.Usage is { } usage)
                emit(new BoardProgressEvent(Tokens: usage.PromptTokens + usage.CompletionTokens + usage.CacheReadTokens + usage.CacheCreationTokens));

            if (string.Equals(result.StopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                emit(new AgentErrorEvent(new ExternalAgentException($"{label} refused the request.")));
                return;
            }

            emit(new AgentFinishedEvent(new AgentResponse
            {
                Content = WithDenied(result),
                TotalUsage = result.Usage ?? new TokenUsage(),
                WasCancelled = string.Equals(result.StopReason, "cancelled", StringComparison.OrdinalIgnoreCase)
            }));
        }, cancellationToken);

    /// <summary>The agent's answer, followed by what it asked for and was refused, so the reviewer knows what did not happen.</summary>
    private static string WithDenied(ExternalAgentResult result)
    {
        var denied = result.Permissions.Where(p => !p.Allowed).Select(p => p.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (denied.Count == 0) return result.Text;
        var text = new StringBuilder(result.Text.TrimEnd());
        if (text.Length > 0) text.Append("\n\n");
        text.Append("Not allowed while running unattended: ").Append(string.Join("; ", denied)).Append('.');
        return text.ToString();
    }

    /// <summary>The program and arguments of a target: a preset is looked up again so the program is found where it is installed now.</summary>
    private (string? Command, IReadOnlyList<string> Args, string? Error) Resolve(AgentTarget target)
    {
        if (target.Kind != AgentTargetKind.External) return (null, Array.Empty<string>(), $"{target.DisplayName} is not an external agent.");
        if (target.Problem is { } problem) return (null, Array.Empty<string>(), problem);

        if (ExternalAgentPresets.Find(target.Id) is { } preset)
        {
            var detected = _detector.Detect(preset);
            return detected.Installed
                ? (preset.Command, preset.Args, null)
                : (null, Array.Empty<string>(), $"{preset.Name} is not ready: {detected.Detail}");
        }

        if (string.IsNullOrWhiteSpace(target.Command))
            return (null, Array.Empty<string>(), $"'{target.Id}' is not a known external agent and has no command.");
        return (target.Command, target.Args, null);
    }
}
