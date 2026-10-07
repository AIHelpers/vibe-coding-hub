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

    /// <summary>Skill references in characters that point at skills that do not exist.</summary>
    public async Task<IReadOnlyList<DanglingSkillReference>> FindDanglingReferencesAsync(CancellationToken cancellationToken = default)
    {
        var known = new HashSet<string>(
            (await _skills.ListAllAsync(cancellationToken).ConfigureAwait(false)).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<DanglingSkillReference>();
        foreach (var c in await _characters.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var s in c.AllSkillReferences.Where(s => s != CharacterRegistry.AllSkillsToken && !known.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase))
                result.Add(new DanglingSkillReference(c.Id, s));
        }
        return result;
    }
}

public record SkillRenameResult(SkillInfo Skill, int CharactersUpdated);

public record SkillDeleteResult(bool Deleted, IReadOnlyList<SkillReference> ReferencedBy, int CharactersUpdated);

public record DanglingSkillReference(string CharacterId, string SkillName);
