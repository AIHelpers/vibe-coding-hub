using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Board;

/// <summary>
/// Runs a card as one of the app's characters: the persona, tools, skills, model and permission mode of the character file
/// are applied to the run, and the default agent runner does the rest. A character with <c>runtime: acp</c> is an external
/// agent and is started as one, with its persona and skills as instructions.
/// </summary>
public sealed class CharacterBoardRunner : IBoardTargetRunner
{
    private readonly ICharacterRegistry _characters;
    private readonly IBoardAgentRunner _builtIn;
    private readonly ExternalBoardRunner? _external;
    private readonly Func<string?, AgentRolePreset?>? _presetOf;
    private readonly Func<string, ExternalAgentTarget?>? _externalTargetOf;

    /// <param name="builtIn">Runs the agent with the options the character produced (the same runner the default agent uses).</param>
    /// <param name="external">Starts characters with <c>runtime: acp</c>; without it such characters are refused.</param>
    /// <param name="presetOf">The base role preset of a role name, for the role's instructions and tools.</param>
    /// <param name="externalTargetOf">The program, arguments and instructions of an external character (persona and skills included).</param>
    public CharacterBoardRunner(
        ICharacterRegistry characters, IBoardAgentRunner builtIn, ExternalBoardRunner? external = null,
        Func<string?, AgentRolePreset?>? presetOf = null, Func<string, ExternalAgentTarget?>? externalTargetOf = null)
    {
        _characters = characters;
        _builtIn = builtIn;
        _external = external;
        _presetOf = presetOf;
        _externalTargetOf = externalTargetOf;
    }

    public bool Supports(AgentTargetKind kind) => kind == AgentTargetKind.Character;

    public async Task<string?> CheckAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        var character = await _characters.GetAsync(target.Id ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return Problem(character, target);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentTarget target, BoardRunRequest request, CancellationToken cancellationToken = default) =>
        BoardEventStream.From(async (emit, token) =>
        {
            var character = await _characters.GetAsync(target.Id ?? string.Empty, token).ConfigureAwait(false);
            if (Problem(character, target) is { } problem) throw new InvalidOperationException(problem);

            IAsyncEnumerable<AgentEvent> stream;
            if (character!.IsExternal)
            {
                var external = _externalTargetOf?.Invoke(character.Id);
                stream = _external!.RunProgramAsync(
                    external?.Command ?? character.Command ?? string.Empty, external?.Args ?? character.Args,
                    external?.Instructions ?? character.Persona, character.Label, request, token);
            }
            else
            {
                var preset = character.BaseRole == null ? null : _presetOf?.Invoke(character.BaseRole);
                var options = CharacterResolver.Apply(request.Options, character, preset, overwrite: true, useBaseRolePermission: false);
                // A character that asks before every edit would be refused every edit here: nobody is at a card to answer.
                if (options.PermissionMode == PermissionMode.Ask) options = options with { PermissionMode = PermissionMode.AutoEdit };
                stream = _builtIn.RunAsync(request.Prompt, request.SessionId, options, token);
            }

            await foreach (var evt in stream.WithCancellation(token).ConfigureAwait(false)) emit(evt);
        }, cancellationToken);

    /// <summary>A character that runs on the built-in agent keeps its session between turns; an external one starts over each time.</summary>
    public async Task<bool> CanSteerAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        var character = await _characters.GetAsync(target.Id ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return character != null && Problem(character, target) == null && !character.IsExternal;
    }

    private string? Problem(CharacterInfo? character, AgentTarget target)
    {
        if (character == null) return $"Character '{target.Id}' was not found.";
        if (!character.IsValid) return $"Character '{character.Id}' is not valid: {string.Join(" ", character.ValidationErrors)}";
        if (character.IsTemplate) return $"'{character.Id}' is a template, a base for other characters. Pick a character that extends it.";
        if (character.IsExternal && _external == null) return $"'{character.Id}' is an external agent and this build cannot start external agents.";
        return null;
    }
}
