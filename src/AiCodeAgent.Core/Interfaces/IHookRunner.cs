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

    /// <summary>
    /// Hooks defined by the project's own <c>.aiagent/settings.json</c> that were NOT loaded because the
    /// project (or its hook file contents) has not been trusted yet. A project can ship arbitrary shell
    /// commands here, so they only run after an explicit <see cref="TrustProjectAsync"/>.
    /// </summary>
    IReadOnlyList<HookDefinition> UntrustedProjectHooks { get; }

    /// <summary>Record the user's approval of the project's current hook file and reload.</summary>
    Task TrustProjectAsync(string? workingDirectory, CancellationToken cancellationToken = default);
}