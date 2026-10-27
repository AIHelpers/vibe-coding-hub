using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Core.Configuration;

/// <summary>What the model picker shows after asking a provider.</summary>
/// <param name="Options">Models to offer, retired ones flagged and last.</param>
/// <param name="DefaultModel">The provider's balanced default among <paramref name="Options"/>; null when none fits.</param>
/// <param name="FromAccount">True when the list is the account's real list, false for the built-in fallback.</param>
/// <param name="Notice">Set for the fallback: why the live list is not shown.</param>
public sealed record ModelDiscoveryResult(
    IReadOnlyList<ModelOption> Options,
    string? DefaultModel,
    bool FromAccount,
    string? Notice);

/// <summary>Asks a provider for its models and merges the answer with the catalog; on failure falls back to the catalog.</summary>
public sealed class ModelDiscovery
{
    private readonly ModelCatalog _catalog;

    public ModelDiscovery(ModelCatalog catalog) => _catalog = catalog;

    public async Task<ModelDiscoveryResult> DiscoverAsync(
        string providerName, IAiProvider provider, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var ids = provider is IModelLister lister
                ? await lister.ListModelIdsAsync(timeoutCts.Token).ConfigureAwait(false)
                : await provider.GetAvailableModelsAsync(timeoutCts.Token).ConfigureAwait(false);

            if (ids == null || ids.Length == 0)
                return Fallback(providerName, "the provider returned no models");

            var options = _catalog.Merge(providerName, ids);
            return new ModelDiscoveryResult(options, _catalog.DefaultModel(providerName, ids), FromAccount: true, Notice: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller gave up, not a failure
        }
        catch (OperationCanceledException)
        {
            return Fallback(providerName, $"no answer within {timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return Fallback(providerName, Describe(ex));
        }
    }

    private ModelDiscoveryResult Fallback(string providerName, string reason) =>
        new(_catalog.Fallback(providerName), _catalog.DefaultModel(providerName), FromAccount: false,
            Notice: $"Couldn't load the live model list ({reason}). Showing the built-in list.");

    private static string Describe(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } status })
            return (int)status == 401 || (int)status == 403 ? "the API key was rejected" : $"HTTP {(int)status}";
        return ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
    }
}
