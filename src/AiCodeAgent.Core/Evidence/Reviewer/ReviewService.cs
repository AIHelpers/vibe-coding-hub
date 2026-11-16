using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Runs the independent review of a finished run end to end (E10-S4): reads its audit trail, picks a reviewer that did not write the
/// change, asks it, and attaches and stores the result. The app, the CLI and flow steps all go through here.
/// </summary>
public sealed class ReviewService
{
    private readonly IAiProviderResolver _resolver;
    private readonly Func<ReviewerPreference?> _preference;
    private readonly IndependentReviewer _reviewer;
    private readonly ILogger? _logger;

    public ReviewService(IAiProviderResolver resolver, Func<ReviewerPreference?>? preference = null, ILogger? logger = null)
    {
        _resolver = resolver;
        _preference = preference ?? (() => null);
        _reviewer = new IndependentReviewer(logger);
        _logger = logger;
    }

    /// <summary>Who would review a run written by these providers and models; null when no provider is configured.</summary>
    public ReviewerChoice? Pick(IReadOnlyCollection<string> usedProviders, IReadOnlyCollection<string> usedModels) =>
        ReviewerSelector.Choose(_resolver, usedProviders, usedModels, _preference());

    /// <summary>Reviews the run behind <paramref name="pack"/>. On success the review is attached to the pack and written beside its audit trail.</summary>
    public async Task<ReviewOutcome> ReviewAsync(EvidencePack pack, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pack.AuditPath) || !File.Exists(pack.AuditPath))
            return ReviewOutcome.Failed("The audit trail of this run is not available, so its changes cannot be shown to a reviewer.");

        ReviewInput input;
        try
        {
            input = ReviewInputBuilder.Build(pack, AuditExporter.Read(pack.AuditPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ReviewOutcome.Failed($"The audit trail could not be read: {ex.Message}");
        }
        if (!input.HasChanges)
            return ReviewOutcome.Failed("The run changed no files, so there is nothing to review.");

        var used = UsedBy(pack);
        var choice = Pick(used.Providers, used.Models);
        if (choice == null)
            return ReviewOutcome.Failed("No model is available to review with. Add a provider with an API key in Settings.");

        var outcome = await _reviewer.ReviewAsync(choice, input, cancellationToken).ConfigureAwait(false);
        if (outcome.Review is { } review)
        {
            pack.Review = review;
            ReviewStore.TryWrite(pack.AuditPath, review, _logger);
        }
        return outcome;
    }

    /// <summary>The providers and models that wrote the change. A chat run used the session's provider; a flow records its steps' models.</summary>
    private (IReadOnlyList<string> Providers, IReadOnlyList<string> Models) UsedBy(EvidencePack pack)
    {
        var providers = new List<string>();
        if (_resolver.DefaultProviderName is { } def) providers.Add(def);
        var models = new List<string>();
        if (!string.IsNullOrWhiteSpace(pack.Model)) models.Add(pack.Model!);
        return (providers, models);
    }
}
