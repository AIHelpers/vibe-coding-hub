using AiCodeAgent.Core.Characters;

namespace AiCodeAgent.Core.Board;

/// <summary>One agent a card can be given, as the picker lists it.</summary>
/// <param name="Group">Heading in the picker: "Built-in", "Characters", "Flows" or "External agents".</param>
/// <param name="IsAvailable">False when the agent cannot start on this machine (an external CLI that is not installed).</param>
/// <param name="Detail">A hint: what the agent is, or what to do to make it available.</param>
public sealed record AgentChoice(AgentTarget Target, string Label, string Group, bool IsAvailable = true, string? Detail = null)
{
    public override string ToString() => Label;
}

/// <summary>Lists what can run a card: the default agent, characters, flows and the external agents found on this machine.</summary>
public sealed class BoardAgentCatalog
{
    public const string BuiltInGroup = "Built-in";
    public const string CharactersGroup = "Characters";
    public const string FlowsGroup = "Flows";
    public const string ExternalGroup = "External agents";

    private readonly ICharacterRegistry? _characters;
    private readonly Func<IEnumerable<(string Name, string? Description)>>? _flows;
    private readonly ExternalAgentDetector? _detector;

    /// <param name="flows">The flows (pipelines with connections) by name and description; null = none are offered.</param>
    /// <param name="detector">Checks the external agent presets; null = external agents are not offered.</param>
    public BoardAgentCatalog(
        ICharacterRegistry? characters = null, Func<IEnumerable<(string Name, string? Description)>>? flows = null,
        ExternalAgentDetector? detector = null)
    {
        _characters = characters;
        _flows = flows;
        _detector = detector;
    }

    public async Task<IReadOnlyList<AgentChoice>> ListAsync(CancellationToken cancellationToken = default)
    {
        var choices = new List<AgentChoice>
        {
            new(AgentTarget.Default, "Default agent", BuiltInGroup, true, "The app's own agent with the model and settings you use in chat.")
        };

        if (_characters != null)
        {
            var characters = await _characters.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var c in characters.Where(c => c.IsValid && !c.IsTemplate).OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
            {
                var label = c.IsExternal ? $"{c.Label} (external)" : c.Label;
                choices.Add(new AgentChoice(AgentTarget.ForCharacter(c.Id, c.Label), label, CharactersGroup, true, c.Description));
            }
        }

        if (_flows != null)
        {
            foreach (var (name, description) in _flows().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                choices.Add(new AgentChoice(AgentTarget.ForFlow(name), $"Flow: {name}", FlowsGroup, true, description));
        }

        if (_detector != null)
        {
            foreach (var found in _detector.DetectAll())
            {
                var label = found.Installed ? $"{found.Preset.Name} (ACP)" : $"{found.Preset.Name} (ACP) — not installed";
                choices.Add(new AgentChoice(AgentTarget.ForPreset(found.Preset), label, ExternalGroup, found.Installed, found.Detail));
            }
        }
        return choices;
    }
}
