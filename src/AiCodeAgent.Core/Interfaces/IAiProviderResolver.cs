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
}
