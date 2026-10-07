using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Turns a <see cref="CharacterInfo"/> into concrete <see cref="AgentOptions"/>:
/// character → base role preset → option merge (persona + role instructions,
/// tools with add/remove, permission mode, model, skills).
/// </summary>
public static class CharacterResolver
{
    /// <summary>
    /// Apply <paramref name="character"/> to <paramref name="options"/>.
    /// With <paramref name="overwrite"/> false (the default, used for plan steps)
    /// values the caller already set explicitly — a role prompt, a tool list, a
    /// model, a skill list — are kept; with true the character's values win.
    /// A character's explicit <c>permission-mode</c> always applies. The base role's
    /// default mode applies only with <paramref name="useBaseRolePermission"/>
    /// (pipelines do this, matching how stages used role presets); otherwise the
    /// caller's mode is kept, because <see cref="AgentOptions.PermissionMode"/> has
    /// no "unset" value to detect.
    /// </summary>
    public static AgentOptions Apply(
        AgentOptions options,
        CharacterInfo character,
        AgentRolePreset? basePreset,
        bool overwrite = false,
        bool useBaseRolePermission = true)
    {
        var result = options with
        {
            CharacterId = character.Id,
            Role = string.IsNullOrEmpty(options.Role) || overwrite ? character.BaseRole ?? character.Id : options.Role
        };

        if (overwrite || string.IsNullOrWhiteSpace(options.RoleSystemPrompt))
            result = result with { RoleSystemPrompt = BuildRolePrompt(character, basePreset) };

        if (overwrite || options.EnabledTools.Count == 0)
            result = result with { EnabledTools = ResolveTools(character, basePreset) };

        if (character.ToolsRemove.Count > 0)
            result = result with
            {
                DisabledTools = result.DisabledTools.Concat(character.ToolsRemove).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };

        var mode = character.PermissionMode ?? (useBaseRolePermission ? basePreset?.DefaultPermissionMode : null);
        if (mode is { } m) result = result with { PermissionMode = m };

        if (!string.IsNullOrWhiteSpace(character.Model) && (overwrite || string.IsNullOrWhiteSpace(options.Model)))
            result = result with { Model = character.Model };

        if (overwrite || options.AllowedSkills is null)
            result = result with { AllowedSkills = character.Skills?.ToList() };

        result = result with
        {
            PinnedSkills = (overwrite ? character.PinnedSkills : options.PinnedSkills.Concat(character.PinnedSkills))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        return result;
    }

    /// <summary>Persona first (who the agent is), then the base role's duties (what it does).</summary>
    public static string BuildRolePrompt(CharacterInfo character, AgentRolePreset? basePreset)
    {
        var parts = new List<string>();
        var header = string.IsNullOrWhiteSpace(character.Description)
            ? $"You are acting as the character \"{character.DisplayName}\"."
            : $"You are acting as the character \"{character.DisplayName}\": {character.Description}";
        if (!character.IsBuiltIn) parts.Add(header);
        if (!string.IsNullOrWhiteSpace(character.Persona)) parts.Add(character.Persona.Trim());
        if (!string.IsNullOrWhiteSpace(basePreset?.SystemPrompt)) parts.Add(basePreset!.SystemPrompt.Trim());
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Effective tool whitelist. An empty list means "all tools", matching
    /// <see cref="AgentOptions.EnabledTools"/>; removals are additionally carried
    /// in <see cref="AgentOptions.DisabledTools"/> so they also apply then.
    /// </summary>
    public static List<string> ResolveTools(CharacterInfo character, AgentRolePreset? basePreset)
    {
        List<string> tools;
        if (character.Tools != null)
            tools = character.Tools.ToList();
        else if (basePreset?.AllowedTools.Count > 0)
            tools = basePreset.AllowedTools.Concat(character.ToolsAdd).ToList();
        else
            tools = new List<string>(); // all tools; adds are implied

        return tools
            .Where(t => !character.ToolsRemove.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
