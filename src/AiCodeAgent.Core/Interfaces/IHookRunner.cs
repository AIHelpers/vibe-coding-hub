using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Executes configured hooks for lifecycle events.
/// </summary>
public interface IHookRunner
{
    /// <summary>Run all hooks matching the event in <paramref name="context"/>.</summary>
    Task<HookRunResult> RunAsync(HookEvent hookEvent, HookContext context, CancellationToken cancellationToken = default);

    /// <summary>Return all configured hook definitions (for /hooks listing).</summary>
    IReadOnlyList<HookDefinition> ListHooks();
}

/// <summary>
/// Loads hook definitions from settings (global ~/.aiagent/settings.json
/// and project .aiagent/settings.json).
/// </summary>
public interface IHookRegistry
{
    /// <summary>Load hook definitions for the given working directory.</summary>
    Task<IReadOnlyList<HookDefinition>> LoadAsync(string? workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>In-memory list (already loaded).</summary>
    IReadOnlyList<HookDefinition> Hooks { get; }
}