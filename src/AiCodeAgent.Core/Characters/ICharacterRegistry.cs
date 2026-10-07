using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Registry of characters: agent personas the user writes as Markdown files
/// (<c>~/.aiagent/characters/&lt;id&gt;.md</c> or <c>.aiagent/characters/&lt;id&gt;.md</c>).
/// A character has a persona (the file body), a base role (a built-in or custom
/// role preset that supplies tools and permissions), optional overrides, and the
/// set of skills it may use. Characters run the workflow: pipeline stages,
/// parallel groups and subagents can all be cast to a character.
/// Built-in role presets (planner, implementer, …) appear as read-only built-in characters.
/// </summary>
public interface ICharacterRegistry
{
    /// <summary>Every character (effective copy per id: project &gt; global &gt; built-in), sorted by id.</summary>
    Task<IReadOnlyList<CharacterInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One character (effective copy), or null when unknown.</summary>
    Task<CharacterInfo?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Full file content of a user character, or empty for built-ins/unknown ids.</summary>
    Task<string> LoadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Create a character file. Throws <see cref="SkillValidationException"/> on an invalid draft or a taken id.</summary>
    Task<CharacterInfo> CreateAsync(CharacterDraft draft, SkillScope scope, CancellationToken cancellationToken = default);

    /// <summary>Replace the full content of a user character. The frontmatter <c>id</c> must keep matching the file name.</summary>
    Task<CharacterInfo> UpdateAsync(string id, string content, SkillScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Delete a user character file (built-ins cannot be deleted). Returns false when nothing was deleted.</summary>
    Task<bool> DeleteAsync(string id, SkillScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add skills to a character (<c>skills</c>, or <c>pinned-skills</c> when <paramref name="pinned"/>).
    /// A skill moves between the two lists rather than appearing in both.
    /// Assigning to a built-in character first creates a global override file for it.
    /// </summary>
    Task<CharacterInfo> AssignSkillsAsync(string id, IEnumerable<string> skills, bool pinned = false, CancellationToken cancellationToken = default);

    /// <summary>Remove skills from both the <c>skills</c> and <c>pinned-skills</c> lists of a character.</summary>
    Task<CharacterInfo> UnassignSkillsAsync(string id, IEnumerable<string> skills, CancellationToken cancellationToken = default);

    /// <summary>User characters (all scopes, including shadowed copies) that reference <paramref name="skillName"/>.</summary>
    Task<IReadOnlyList<SkillReference>> FindSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Rewrite every reference to <paramref name="oldName"/> (after a skill rename). Returns the number of files changed.</summary>
    Task<int> RenameSkillReferencesAsync(string oldName, string newName, CancellationToken cancellationToken = default);

    /// <summary>Remove every reference to <paramref name="skillName"/> (after a skill delete). Returns the number of files changed.</summary>
    Task<int> RemoveSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Raised after the character set changed.</summary>
    event EventHandler? Changed;

    /// <summary>Force a rescan on next access.</summary>
    void Refresh();

    string GlobalCharactersDirectory { get; }
    string? ProjectCharactersDirectory { get; }
}

/// <summary>A character (agent persona).</summary>
public record CharacterInfo
{
    /// <summary>Unique id (kebab-case), equals the file name without <c>.md</c>.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Human-friendly name, e.g. "Alex — Architect". Falls back to the id.</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Short visual marker (emoji or initials).</summary>
    public string? Avatar { get; init; }
    public string Description { get; init; } = string.Empty;
    /// <summary>Role preset the character builds on (tools, permission mode, role instructions). Null = generic agent.</summary>
    public string? BaseRole { get; init; }
    public string? Model { get; init; }
    public PermissionMode? PermissionMode { get; init; }
    /// <summary>Explicit tool list replacing the base role's tools (frontmatter <c>tools: [a, b]</c>).</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>Tools added to the base role's tools (frontmatter <c>tools: { add: [...] }</c>).</summary>
    public IReadOnlyList<string> ToolsAdd { get; init; } = Array.Empty<string>();
    /// <summary>Tools removed from the base role's tools (frontmatter <c>tools: { remove: [...] }</c>).</summary>
    public IReadOnlyList<string> ToolsRemove { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Skills listed for the agent and loadable on demand. Null = all skills
    /// (built-in characters, or <c>skills: ["*"]</c>); empty = no skills.
    /// </summary>
    public IReadOnlyList<string>? Skills { get; init; }
    /// <summary>Skills whose full instructions are always in context.</summary>
    public IReadOnlyList<string> PinnedSkills { get; init; } = Array.Empty<string>();
    /// <summary>Persona instructions (the Markdown body).</summary>
    public string Persona { get; init; } = string.Empty;
    public SkillScope Scope { get; init; } = SkillScope.Global;
    public bool IsBuiltIn => Scope == SkillScope.BuiltIn;
    /// <summary>True when this user character overrides a built-in or global character with the same id.</summary>
    public bool Overrides { get; init; }
    public string? FilePath { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsValid => ValidationErrors.Count == 0;

    /// <summary>All skill names the character references (listed + pinned).</summary>
    public IEnumerable<string> AllSkillReferences => (Skills ?? Array.Empty<string>()).Concat(PinnedSkills);

    /// <summary>"🧭 Alex — Architect" style label.</summary>
    public string Label => string.IsNullOrEmpty(Avatar) ? DisplayName : $"{Avatar} {DisplayName}";
}

/// <summary>Input for creating a character.</summary>
public record CharacterDraft
{
    public string Id { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Avatar { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? BaseRole { get; init; }
    public string? Model { get; init; }
    public PermissionMode? PermissionMode { get; init; }
    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PinnedSkills { get; init; } = Array.Empty<string>();
    /// <summary>Persona text (Markdown body). A short template is used when empty.</summary>
    public string? Persona { get; init; }
}

/// <summary>A character file that references a skill.</summary>
public record SkillReference(string CharacterId, SkillScope Scope, string FilePath, bool Pinned);
