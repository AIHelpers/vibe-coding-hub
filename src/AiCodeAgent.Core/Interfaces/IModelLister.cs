namespace AiCodeAgent.Core.Interfaces;

/// <summary>
/// A provider that can ask its service which models the account can use. Unlike
/// <see cref="IAiProvider.GetAvailableModelsAsync"/>, which hides failures behind a static list, this one throws
/// when the call fails, so the caller can tell a real list from a fallback.
/// </summary>
public interface IModelLister
{
    /// <summary>The ids the account can use. Throws <see cref="HttpRequestException"/> (and friends) on failure.</summary>
    Task<string[]> ListModelIdsAsync(CancellationToken cancellationToken = default);
}
