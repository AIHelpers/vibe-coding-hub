using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Health checks for the skills registry and characters, shown by <c>/doctor</c>
/// (CLI) and the Project Knowledge doctor list (desktop): invalid files,
/// warnings, and characters that reference skills that do not exist.
/// </summary>
public static class SkillDoctor
{
    public static async Task<IReadOnlyList<DoctorCheck>> DiagnoseAsync(
        ISkillRegistry skills,
        ICharacterRegistry? characters = null,
        RolePresetLoader? presets = null,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        // A doctor looks at what is on disk now, not at a cached scan.
        skills.Refresh();
        characters?.Refresh();
        var all = await skills.ListAllAsync(cancellationToken).ConfigureAwait(false);

        var globalCount = all.Count(s => s.Scope == SkillScope.Global);
        var projectCount = all.Count(s => s.Scope == SkillScope.Project);
        checks.Add(all.Count == 0
            ? new DoctorCheck("Skills", DoctorStatus.Warning, "No skills yet.",
                "Create one: aiagent skills new <name> --description \"...\" (or Project Knowledge → Skills → New Skill).")
            : new DoctorCheck("Skills", DoctorStatus.Ok, $"{all.Count} skill(s): {globalCount} global, {projectCount} project."));

        foreach (var s in all.Where(s => !s.IsValid))
            checks.Add(new DoctorCheck($"Skill '{s.Name}'", DoctorStatus.Error, string.Join(" ", s.ValidationErrors),
                $"Fix or delete {s.FilePath}. Invalid skills are hidden from the agent."));

        foreach (var s in all.Where(s => s.IsValid && s.Warnings.Count > 0))
            checks.Add(new DoctorCheck($"Skill '{s.Name}'", DoctorStatus.Warning, string.Join(" ", s.Warnings), s.FilePath));

        if (presets != null)
            foreach (var (role, tools) in presets.UnknownToolsByRole.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                checks.Add(new DoctorCheck($"Preset {role}", DoctorStatus.Error, KnownToolNames.Describe(tools),
                    $"Run `aiagent skills doctor --fix`, or edit {presets.UserPresetFiles.GetValueOrDefault(role)}."));

        if (characters == null) return checks;

        var list = await characters.ListAsync(cancellationToken).ConfigureAwait(false);
        var custom = list.Where(c => !c.IsBuiltIn).ToList();
        checks.Add(new DoctorCheck("Characters", DoctorStatus.Ok,
            $"{custom.Count} custom, {list.Count - custom.Count} built-in."));

        foreach (var c in custom.Where(c => !c.IsValid))
            checks.Add(new DoctorCheck($"Character '{c.Id}'", DoctorStatus.Error, string.Join(" ", c.ValidationErrors),
                $"Fix {c.FilePath}. Invalid characters cannot run steps."));

        foreach (var c in custom.Where(c => c.IsValid && c.Warnings.Count > 0))
            checks.Add(new DoctorCheck($"Character '{c.Id}'", DoctorStatus.Warning, string.Join(" ", c.Warnings), c.FilePath));

        foreach (var c in custom)
        {
            var unknown = KnownToolNames.Unknown((c.Tools ?? Array.Empty<string>()).Concat(c.ToolsAdd).Concat(c.ToolsRemove));
            if (unknown.Count > 0)
                checks.Add(new DoctorCheck($"Character '{c.Id}'", DoctorStatus.Error, KnownToolNames.Describe(unknown),
                    c.FilePath == null ? "Fix the tool names in the character's tools list." : $"Fix the tool names in {c.FilePath}."));
        }

        if (presets != null)
            foreach (var ch in list.Where(x => presets.UnknownToolsFor(x.BaseRole).Count > 0))
                checks.Add(new DoctorCheck($"Character '{ch.Id}'", DoctorStatus.Warning,
                    $"Base role '{ch.BaseRole}' has {KnownToolNames.Describe(presets.UnknownToolsFor(ch.BaseRole))}",
                    "Run `aiagent skills doctor --fix`."));

        var templates = custom.Count(c => c.IsTemplate);
        if (templates > 0)
            checks.Add(new DoctorCheck("Templates", DoctorStatus.Ok, $"{templates} template(s)."));

        var dangling = await new SkillMaintenance(skills, characters).FindDanglingReferencesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in dangling.GroupBy(d => d.CharacterId))
        {
            var names = string.Join(", ", group.Select(d => d.SkillName));
            checks.Add(new DoctorCheck($"Character '{group.Key}'", DoctorStatus.Warning,
                $"References missing skill(s): {names}. They are skipped at run time.",
                $"Create them (aiagent skills new <name>) or remove them (aiagent characters unassign {group.Key} {names.Replace(",", "")})."));
        }

        var largePinned = custom
            .SelectMany(c => c.PinnedSkills.Select(p => (Character: c.Id, Skill: all.FirstOrDefault(s => string.Equals(s.Name, p, StringComparison.OrdinalIgnoreCase)))))
            .Where(x => x.Skill != null && x.Skill.EstimatedTokens > Context.SkillRegistry.LargeSkillTokenThreshold);
        foreach (var (characterId, skill) in largePinned)
            checks.Add(new DoctorCheck($"Character '{characterId}'", DoctorStatus.Warning,
                $"Pins large skill '{skill!.Name}' (~{skill.EstimatedTokens:N0} tokens on every turn).",
                $"Assign it unpinned instead: aiagent characters assign {characterId} {skill.Name}"));

        return checks;
    }
}
