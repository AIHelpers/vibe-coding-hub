namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// On-demand capabilities (Feature 06 — Skills). The agent sees skill
/// descriptions at session start, but the full content only loads into
/// context when a skill is actually used.
/// </summary>
public interface ISkillRegistry
{
    /// <summary>
    /// Return skill names + descriptions for all visible skills. Cheap to
    /// call at session start. Manual-only skills (disable-model-invocation)
    /// and skills overridden to "hidden" are excluded.
    /// </summary>
    Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Return the full skill content (instructions) for invocation.</summary>
    Task<string> LoadAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a skill by name. Loads the full content and returns it so the
    /// caller can inject it into the agent context. Returns null when the
    /// skill is not found.
    /// </summary>
    Task<SkillInvocation?> InvokeAsync(string skillName, string? args = null, CancellationToken cancellationToken = default);

    /// <summary>Return the path to the skill file (for editing).</summary>
    string? GetSkillPath(string skillName);
}

/// <summary>Lightweight metadata about a skill (name + description).</summary>
public record SkillInfo
{
    /// <summary>Unique skill name (kebab-case, matches directory name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>One-line description shown to the agent at session start.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>True when the skill is manual-only (descriptions kept out of context until invoked).</summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>Path to the underlying SKILL.md file.</summary>
    public string? FilePath { get; init; }
}

/// <summary>The result of invoking a skill: full content ready to inject.</summary>
public record SkillInvocation
{
    public string Name { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? Args { get; init; }
}