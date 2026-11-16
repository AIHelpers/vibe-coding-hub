namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// Finds the AI provider for a name, so one flow can run its steps on different providers (E5-S3). The default
/// provider stays the orchestrator's own; this only serves steps that name another one.
/// </summary>
public interface IAiProviderResolver
{
    /// <summary>Name of the session's default provider (lower case), or null when none is configured.</summary>
    string? DefaultProviderName { get; }

    /// <summary>The provider configured under <paramref name="providerName"/>, or null when it is unknown or has no settings.</summary>
    IAiProvider? Resolve(string providerName);

    /// <summary>
    /// Providers that are ready to use (they have a key, or run locally). Used to pick a reviewer from a provider other than the
    /// one that wrote the change (E10-S4). Empty by default: resolvers that cannot list their providers only serve named lookups.
    /// </summary>
    IReadOnlyList<string> ProviderNames => Array.Empty<string>();

    /// <summary>The model a provider uses when none is named, or null when unknown.</summary>
    string? DefaultModel(string providerName) => null;
}
