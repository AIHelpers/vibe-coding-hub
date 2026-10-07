namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Local skills registry (Feature 06 — Skills). Skills are Markdown files
/// (<c>&lt;dir&gt;/&lt;name&gt;/SKILL.md</c>) in a global and a project directory.
/// The agent sees skill descriptions at session start; the full content only
/// loads into context when a skill is used (<c>use_skill</c> tool, <c>/skill</c>)
/// or when a character pins it.
/// </summary>
public interface ISkillRegistry
{
    /// <summary>
    /// Return skill names + descriptions for all model-visible skills. Cheap to
    /// call at session start. Manual-only skills (disable-model-invocation),
    /// skills overridden to "hidden" and invalid skills are excluded.
    /// </summary>
    Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Return every skill on disk (the effective one per name — project wins),
    /// including manual-only, hidden and invalid ones. Meant for management UIs.
    /// </summary>
    Task<IReadOnlyList<SkillInfo>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Metadata of one skill (effective scope), or null when it does not exist.</summary>
    Task<SkillInfo?> GetAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Return the full skill file content (frontmatter + instructions), or empty when missing.</summary>
    Task<string> LoadAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a skill by name. Loads the instructions (body without frontmatter)
    /// so the caller can inject them into the agent context. Returns null when the
    /// skill is not found.
    /// </summary>
    Task<SkillInvocation?> InvokeAsync(string skillName, string? args = null, CancellationToken cancellationToken = default);

    /// <summary>Return the path to the skill file (for editing).</summary>
    string? GetSkillPath(string skillName);

    /// <summary>Create a new skill. Throws <see cref="SkillValidationException"/> when the draft is invalid or the name is taken in that scope.</summary>
    Task<SkillInfo> CreateAsync(SkillDraft draft, SkillScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the full content (frontmatter + body) of an existing skill. The
    /// frontmatter <c>name</c> must keep matching the skill's folder; use
    /// <see cref="RenameAsync"/> to rename. Throws <see cref="SkillValidationException"/> on invalid content.
    /// </summary>
    Task<SkillInfo> UpdateAsync(string skillName, string content, SkillScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Rename a skill (folder and frontmatter <c>name</c>). Throws <see cref="SkillValidationException"/> on an invalid or taken name.</summary>
    Task<SkillInfo> RenameAsync(string oldName, string newName, SkillScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a skill folder. With <paramref name="scope"/> null the effective
    /// (winning) copy is deleted, which can reveal a global skill shadowed by a
    /// project one. Returns false when nothing was deleted.
    /// </summary>
    Task<bool> DeleteAsync(string skillName, SkillScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Raised after the skill set changed (CRUD through this registry, or a file change when watching).</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Forces a rescan of the skills directories on the next call to
    /// <see cref="ListAsync"/>/<see cref="LoadAsync"/>/etc. Call after
    /// editing skill files outside the registry when not watching.
    /// </summary>
    void Refresh();

    /// <summary>The global skills directory (e.g. <c>~/.aiagent/skills</c>) this instance scans.</summary>
    string GlobalSkillsDirectory { get; }

    /// <summary>The project skills directory (e.g. <c>.aiagent/skills</c>) this instance scans, or null if none was configured.</summary>
    string? ProjectSkillsDirectory { get; }
}

/// <summary>Where a skill (or character) file lives.</summary>
public enum SkillScope
{
    /// <summary>User-wide: <c>~/.aiagent/...</c>.</summary>
    Global = 0,
    /// <summary>Per repository: <c>&lt;workdir&gt;/.aiagent/...</c>. Overrides a global entry with the same name.</summary>
    Project = 1,
    /// <summary>Shipped with the app; read-only (used for built-in characters).</summary>
    BuiltIn = 2
}

/// <summary>Lightweight metadata about a skill.</summary>
public record SkillInfo
{
    /// <summary>Unique skill name (kebab-case). Always equals the skill's folder name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>One-line description shown to the agent at session start.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>True when the skill is manual-only (descriptions kept out of context until invoked).</summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>Path to the underlying SKILL.md file.</summary>
    public string? FilePath { get; init; }

    /// <summary>Folder that holds SKILL.md and any bundled resources.</summary>
    public string? DirectoryPath { get; init; }

    /// <summary>Which directory the effective copy came from.</summary>
    public SkillScope Scope { get; init; } = SkillScope.Global;

    /// <summary>True when a global skill with the same name is hidden by this project skill.</summary>
    public bool ShadowsGlobal { get; init; }

    /// <summary>Free-form tags for filtering (frontmatter <c>tags</c>).</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Optional version number (frontmatter <c>version</c>).</summary>
    public int? Version { get; init; }

    /// <summary>Tools the skill expects (frontmatter <c>allowed-tools</c>). Advisory only — never grants tools.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>Approximate size of the instructions in tokens (chars / 4).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Problems that make the skill unusable (it is hidden from the model until fixed).</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();

    /// <summary>Non-fatal problems (e.g. very large skill).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool IsValid => ValidationErrors.Count == 0;
}

/// <summary>The result of invoking a skill: instructions ready to inject.</summary>
public record SkillInvocation
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Skill instructions (the SKILL.md body, without frontmatter).</summary>
    public string Content { get; init; } = string.Empty;
    public string? Args { get; init; }
    /// <summary>Folder holding the skill and any bundled resources (templates, scripts).</summary>
    public string? DirectoryPath { get; init; }

    /// <summary>Formats the invocation as a block suitable for injecting into a prompt or tool result.</summary>
    public string ToPromptBlock()
    {
        var header = $"<skill name=\"{Name}\"" + (string.IsNullOrEmpty(DirectoryPath) ? "" : $" dir=\"{DirectoryPath}\"") + ">";
        var args = string.IsNullOrWhiteSpace(Args) ? string.Empty : $"\nArguments: {Args}\n";
        return $"{header}{args}\n{Content.Trim()}\n</skill>";
    }

    /// <summary>
    /// Prefixes a user message with explicitly activated skills (CLI <c>/skill</c>,
    /// desktop chat <c>/skill</c>), so the model receives the instructions with the request.
    /// </summary>
    public static string ComposeUserMessage(IEnumerable<SkillInvocation> skills, string userMessage)
    {
        var list = skills.ToList();
        if (list.Count == 0) return userMessage;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("The user activated the following skill(s) for this request. Follow their instructions.");
        foreach (var skill in list)
            sb.AppendLine(skill.ToPromptBlock());
        sb.AppendLine();
        sb.Append(userMessage);
        return sb.ToString();
    }
}

/// <summary>Input for creating a skill.</summary>
public record SkillDraft
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Instructions (Markdown body). A short template is used when empty.</summary>
    public string? Body { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public bool DisableModelInvocation { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
}

/// <summary>Thrown when a skill or character create/update/rename is rejected.</summary>
public class SkillValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public SkillValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors)) => Errors = errors;

    public SkillValidationException(string error) : this(new[] { error }) { }
}
