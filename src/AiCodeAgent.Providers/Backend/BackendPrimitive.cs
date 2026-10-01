using System;
using System.Collections.Generic;

namespace AiCodeAgent.Providers.Backend;

/// <summary>
/// Descriptor for a bundled backend primitive (auth, database, storage, etc.)
/// that the agent can scaffold into a generated app.
/// </summary>
public sealed class BackendPrimitive
{
    /// <summary>Stable identifier, e.g. <c>auth</c>, <c>db</c>, <c>storage</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name shown in chat/UI.</summary>
    public required string Name { get; init; }

    /// <summary>Short description of what the primitive provides.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Relative path (under the catalog's templates root) to the folder of
    /// template files to copy. May be null for pure-config primitives.
    /// </summary>
    public string? TemplatePath { get; init; }

    /// <summary>Ids of other primitives that must be scaffolded before this one.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Environment variables the primitive expects (written to .env.example).</summary>
    public IReadOnlyList<BackendConfigVar> ConfigSchema { get; init; } = [];

    /// <summary>Optional stack filter (e.g. "node", "python"). Null = any stack.</summary>
    public string? Stack { get; init; }

    /// <summary>Wiring snippet appended to the app's registerBackend hook (optional).</summary>
    public string? WiringSnippet { get; init; }

    /// <summary>
    /// Inline template files keyed by relative path (e.g. "auth.js"). When present,
    /// the scaffolder writes these instead of reading from disk.
    /// </summary>
    public IReadOnlyDictionary<string, string> InlineTemplates { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One environment variable expected by a backend primitive.</summary>
public sealed class BackendConfigVar
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? DefaultValue { get; init; }
    public bool Required { get; init; }
}