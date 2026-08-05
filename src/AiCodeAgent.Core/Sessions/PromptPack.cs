using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// A reusable prompt template (section 5.4). Versionable in git alongside code.
/// </summary>
public sealed class PromptTemplate
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
    public List<string> Variables { get; init; } = new();
}

/// <summary>
/// Shared prompt pack — role presets + reusable prompt templates.
/// Teams share via normal PRs on this file, no server needed.
/// </summary>
public sealed class PromptPack
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<AgentRolePreset> Roles { get; init; } = new();
    public List<PromptTemplate> Templates { get; init; } = new();
}