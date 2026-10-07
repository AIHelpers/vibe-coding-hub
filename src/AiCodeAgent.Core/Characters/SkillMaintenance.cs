using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Skill operations that must keep character files consistent: renaming a skill
/// rewrites every character reference, deleting one can remove them. Dangling
/// references never crash a run (unknown skills are skipped), but the UI/CLI and
/// <c>/doctor</c> report them.
/// </summary>
public sealed class SkillMaintenance
{
    private readonly ISkillRegistry _skills;
    private readonly ICharacterRegistry _characters;

    public SkillMaintenance(ISkillRegistry skills, ICharacterRegistry characters)
    {
        _skills = skills;
        _characters = characters;
    }

    /// <summary>Rename a skill and update the characters that use it.</summary>
    public async Task<SkillRenameResult> RenameAsync(string oldName, string newName, SkillScope? scope = null, CancellationToken cancellationToken = default)
    {
        var info = await _skills.RenameAsync(oldName, newName, scope, cancellationToken).ConfigureAwait(false);
        // Only rewrite references when no other copy keeps the old name alive (e.g. a shadowed global skill).
        var stillExists = await _skills.GetAsync(oldName, cancellationToken).ConfigureAwait(false) != null;
        var updated = stillExists ? 0 : await _characters.RenameSkillReferencesAsync(oldName, info.Name, cancellationToken).ConfigureAwait(false);
        return new SkillRenameResult(info, updated);
    }

    /// <summary>
    /// Delete a skill. When <paramref name="removeReferences"/> is true and no other
    /// copy of the skill remains, the skill is also removed from every character.
    /// </summary>
    public async Task<SkillDeleteResult> DeleteAsync(string name, SkillScope? scope = null, bool removeReferences = true, CancellationToken cancellationToken = default)
    {
        var references = await _characters.FindSkillReferencesAsync(name, cancellationToken).ConfigureAwait(false);
        var deleted = await _skills.DeleteAsync(name, scope, cancellationToken).ConfigureAwait(false);
        if (!deleted) return new SkillDeleteResult(false, references, 0);
        var stillExists = await _skills.GetAsync(name, cancellationToken).ConfigureAwait(false) != null;
        var removed = removeReferences && !stillExists
            ? await _characters.RemoveSkillReferencesAsync(name, cancellationToken).ConfigureAwait(false)
            : 0;
        return new SkillDeleteResult(true, references, removed);
    }

    /// <summary>
    /// "Add skill" on a character profile: give <paramref name="characterId"/> the library skill
    /// <paramref name="skillName"/>, creating it first from <paramref name="createIfMissing"/>
    /// (in <paramref name="createScope"/>) when it does not exist yet. A character that could use
    /// every skill (built-in, or <c>skills: ["*"]</c>) switches to an explicit list of its own skills.
    /// </summary>
    /// <exception cref="SkillValidationException">Unknown character, invalid skill draft, or a missing skill without a draft.</exception>
    public async Task<CharacterSkillAddResult> AddSkillToCharacterAsync(
        string characterId,
        string skillName,
        bool pinned = false,
        SkillDraft? createIfMissing = null,
        SkillScope createScope = SkillScope.Global,
        CancellationToken cancellationToken = default)
    {
        skillName = (skillName ?? string.Empty).Trim();
        var character = await _characters.GetAsync(characterId, cancellationToken).ConfigureAwait(false)
            ?? throw new SkillValidationException($"Character '{characterId}' does not exist.");
        if (skillName.Length == 0)
            throw new SkillValidationException("Pick a skill from the library or enter a name for a new one.");

        var skill = await _skills.GetAsync(skillName, cancellationToken).ConfigureAwait(false);
        var created = false;
        if (skill == null)
        {
            if (createIfMissing == null)
                throw new SkillValidationException($"Skill '{skillName}' does not exist. Give it a description to create it.");
            skill = await _skills.CreateAsync(createIfMissing with { Name = skillName }, createScope, cancellationToken).ConfigureAwait(false);
            created = true;
        }

        var wasAllSkills = character.Skills == null;
        if (wasAllSkills && !character.IsBuiltIn)
            await _characters.UnassignSkillsAsync(character.Id, new[] { CharacterRegistry.AllSkillsToken }, cancellationToken).ConfigureAwait(false);
        var updated = await _characters.AssignSkillsAsync(character.Id, new[] { skill.Name }, pinned, cancellationToken).ConfigureAwait(false);
        return new CharacterSkillAddResult(updated, skill, created, wasAllSkills);
    }

    /// <summary>
    /// References written in character files that point at skills that do not
    /// exist. Each file is reported once per missing skill (inherited copies are not repeated).
    /// </summary>
    public async Task<IReadOnlyList<DanglingSkillReference>> FindDanglingReferencesAsync(CancellationToken cancellationToken = default)
    {
        var known = new HashSet<string>(
            (await _skills.ListAllAsync(cancellationToken).ConfigureAwait(false)).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
        bool Missing(string s) => s != CharacterRegistry.AllSkillsToken && !known.Contains(s);

        var result = new List<DanglingSkillReference>();
        foreach (var c in await _characters.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var s in c.DeclaredSkillReferences.Where(Missing).Distinct(StringComparer.OrdinalIgnoreCase))
                result.Add(new DanglingSkillReference(c.Id, s));
        }
        return result;
    }

}

/// <param name="WasAllSkills">The character could use every skill before; now it has an explicit list.</param>
public record CharacterSkillAddResult(CharacterInfo Character, SkillInfo Skill, bool CreatedSkill, bool WasAllSkills);

public record SkillRenameResult(SkillInfo Skill, int CharactersUpdated);

public record SkillDeleteResult(bool Deleted, IReadOnlyList<SkillReference> ReferencedBy, int CharactersUpdated);

public record DanglingSkillReference(string CharacterId, string SkillName);
