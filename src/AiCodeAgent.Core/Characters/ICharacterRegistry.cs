using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Characters;

/// <summary>
/// Registry of characters: agent personas the user writes as Markdown files
/// (<c>~/.aiagent/characters/&lt;id&gt;.md</c> or <c>.aiagent/characters/&lt;id&gt;.md</c>).
///
/// Every character has its own, individual skills picked from the shared skill library.
/// A character's effective skillset is
/// <c>skills of the template it extends</c> + <c>its own skills</c> − <c>its remove-skills</c>.
/// For example <c>dana-dotnet</c> extends the <c>software-developer</c> template (common
/// skills and persona) and adds <c>csharp</c> and <c>clean-architecture</c> of its own.
/// Built-in role presets (planner, implementer, …) appear as read-only built-in characters.
/// </summary>
public interface ICharacterRegistry
{
    /// <summary>Every character (effective copy per id: project &gt; global &gt; built-in), resolved, sorted by id.</summary>
    Task<IReadOnlyList<CharacterInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One resolved character (effective copy), or null when unknown.</summary>
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
    /// Add skills to a character's own lists (<c>skills</c>, or <c>pinned-skills</c> when
    /// <paramref name="pinned"/>), and take them out of <c>remove-skills</c>.
    /// Assigning to a built-in character first creates a global override file for it.
    /// </summary>
    Task<CharacterInfo> AssignSkillsAsync(string id, IEnumerable<string> skills, bool pinned = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove skills from a character. Own skills are deleted from its lists; skills it
    /// gets from its template are added to <c>remove-skills</c>.
    /// </summary>
    Task<CharacterInfo> UnassignSkillsAsync(string id, IEnumerable<string> skills, CancellationToken cancellationToken = default);

    /// <summary>Make a character extend a template (or another character), or stop extending with null.</summary>
    Task<CharacterInfo> SetExtendsAsync(string id, string? parentId, CancellationToken cancellationToken = default);

    /// <summary>Mark a character as a template (a base for others; not offered as a chat/subagent character).</summary>
    Task<CharacterInfo> SetTemplateAsync(string id, bool isTemplate, CancellationToken cancellationToken = default);

    /// <summary>Character files (all scopes) that reference <paramref name="skillName"/>.</summary>
    Task<IReadOnlyList<SkillReference>> FindSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Rewrite every reference to <paramref name="oldName"/> (after a skill rename). Returns the number of files changed.</summary>
    Task<int> RenameSkillReferencesAsync(string oldName, string newName, CancellationToken cancellationToken = default);

    /// <summary>Remove every reference to <paramref name="skillName"/> (after a skill delete). Returns the number of files changed.</summary>
    Task<int> RemoveSkillReferencesAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Raised after characters changed.</summary>
    event EventHandler? Changed;

    /// <summary>Force a rescan on next access.</summary>
    void Refresh();

    string GlobalCharactersDirectory { get; }
    string? ProjectCharactersDirectory { get; }
}

/// <summary>A character (agent persona), with its effective (resolved) settings and where its skills come from.</summary>
public record CharacterInfo
{
    /// <summary>Unique id (kebab-case), equals the file name without <c>.md</c>.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Human-friendly name, e.g. "Alex — Architect". Falls back to the id.</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Short visual marker (emoji or initials).</summary>
    public string? Avatar { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>A template is a base for other characters (<c>template: true</c>); it is not offered in chat/subagent pickers.</summary>
    public bool IsTemplate { get; init; }
    /// <summary>The character this one extends (frontmatter <c>extends</c>), if any.</summary>
    public string? Extends { get; init; }
    /// <summary>Ancestors from nearest to farthest (e.g. ["software-developer", "implementer"]).</summary>
    public IReadOnlyList<string> InheritanceChain { get; init; } = Array.Empty<string>();
    /// <summary>Inherited skills this character drops (frontmatter <c>remove-skills</c>).</summary>
    public IReadOnlyList<string> RemoveSkills { get; init; } = Array.Empty<string>();

    /// <summary>Effective role preset (own, else inherited). Null = generic agent.</summary>
    public string? BaseRole { get; init; }
    /// <summary>Effective model override (own, else inherited).</summary>
    public string? Model { get; init; }
    /// <summary>Effective permission mode (own, else inherited).</summary>
    public PermissionMode? PermissionMode { get; init; }
    /// <summary>Effective explicit tool list replacing the base role's tools (frontmatter <c>tools: [a, b]</c>).</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>Effective tools added to the base role's tools (union along the inheritance chain).</summary>
    public IReadOnlyList<string> ToolsAdd { get; init; } = Array.Empty<string>();
    /// <summary>Effective tools removed from the base role's tools (union along the inheritance chain).</summary>
    public IReadOnlyList<string> ToolsRemove { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Effective skills listed for the agent and loadable on demand (template + own − removed).
    /// Null = all skills (built-in characters, or <c>skills: ["*"]</c>); empty = no skills.
    /// </summary>
    public IReadOnlyList<string>? Skills { get; init; }
    /// <summary>Effective skills whose full instructions are always in context.</summary>
    public IReadOnlyList<string> PinnedSkills { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Where each effective skill comes from: <c>own</c> or <c>template:&lt;id&gt;</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkillSources { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Skills contributed by the template (before <c>remove-skills</c>), with their source.</summary>
    public IReadOnlyDictionary<string, string> InheritedSkillSources { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character's own <c>skills</c> list as written in its file (null when it is <c>["*"]</c>).</summary>
    public IReadOnlyList<string>? OwnSkills { get; init; }
    /// <summary>The character's own <c>pinned-skills</c> list as written in its file.</summary>
    public IReadOnlyList<string> OwnPinnedSkills { get; init; } = Array.Empty<string>();

    /// <summary>Effective persona: inherited persona followed by this character's own text.</summary>
    public string Persona { get; init; } = string.Empty;
    /// <summary>This character's own persona text (its file body).</summary>
    public string OwnPersona { get; init; } = string.Empty;

    public SkillScope Scope { get; init; } = SkillScope.Global;
    public bool IsBuiltIn => Scope == SkillScope.BuiltIn;
    /// <summary>True when this user character overrides a built-in or global character with the same id.</summary>
    public bool Overrides { get; init; }
    public string? FilePath { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsValid => ValidationErrors.Count == 0;

    /// <summary>All effective skill names (listed + pinned).</summary>
    public IEnumerable<string> AllSkillReferences => (Skills ?? Array.Empty<string>()).Concat(PinnedSkills);

    /// <summary>Skill names written in this character's own file (own lists + remove-skills).</summary>
    public IEnumerable<string> DeclaredSkillReferences => (OwnSkills ?? Array.Empty<string>()).Concat(OwnPinnedSkills);

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
    /// <summary>Template (or other character) to inherit from.</summary>
    public string? Extends { get; init; }
    /// <summary>Create the character as a template for others.</summary>
    public bool IsTemplate { get; init; }
    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PinnedSkills { get; init; } = Array.Empty<string>();
    /// <summary>Persona text (Markdown body). A short template is used when empty.</summary>
    public string? Persona { get; init; }
}

/// <summary>A character file that references a skill.</summary>
public record SkillReference(string CharacterId, SkillScope Scope, string FilePath, bool Pinned)
{
    public string Label => CharacterId;
}
