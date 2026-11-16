using AiCodeAgent.Core.Interfaces;

namespace AiCodeAgent.Core.Evidence;

/// <summary>The reviewer chosen for a run, and how independent it is.</summary>
public sealed record ReviewerChoice(IAiProvider Provider, string ProviderName, string Model, ReviewIndependence Independence);

/// <summary>What the user fixed in the settings, if anything. Both null means "pick for me".</summary>
public sealed record ReviewerPreference(string? Provider, string? Model);

/// <summary>
/// Picks the model that reviews a change (E10-S4): a different provider when one is configured, else another model of the
/// same provider, else the same model with a plain warning. Pure apart from asking the resolver, so it is unit-testable.
/// </summary>
public static class ReviewerSelector
{
    /// <param name="usedProviders">Providers that wrote the change (the session's provider, or each flow step's).</param>
    /// <param name="usedModels">Models that wrote the change.</param>
    /// <param name="preference">A reviewer fixed in the settings; it wins even when it is not independent.</param>
    public static ReviewerChoice? Choose(
        IAiProviderResolver resolver, IReadOnlyCollection<string> usedProviders, IReadOnlyCollection<string> usedModels, ReviewerPreference? preference = null)
    {
        var providers = new HashSet<string>(usedProviders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Norm), StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(usedModels.Where(m => !string.IsNullOrWhiteSpace(m)), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preference?.Provider))
        {
            var name = Norm(preference!.Provider!);
            var provider = resolver.Resolve(name);
            var model = string.IsNullOrWhiteSpace(preference.Model) ? resolver.DefaultModel(name) : preference.Model;
            return provider == null || string.IsNullOrWhiteSpace(model) ? null : Make(provider, name, model!, providers, models);
        }

        // The session's own provider with a model the user named for it, when only the model is fixed.
        var candidates = resolver.ProviderNames.Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (resolver.DefaultProviderName is { } def && !candidates.Contains(def, StringComparer.OrdinalIgnoreCase)) candidates.Add(def);

        if (!string.IsNullOrWhiteSpace(preference?.Model))
        {
            var name = resolver.DefaultProviderName ?? candidates.FirstOrDefault();
            return name != null && resolver.Resolve(name) is { } p ? Make(p, name, preference!.Model!, providers, models) : null;
        }

        // 1) another provider, 2) the same provider with another model, 3) the same model.
        foreach (var name in candidates.Where(c => !providers.Contains(c)))
            if (Usable(resolver, name) is { } found) return Make(found.Provider, name, found.Model, providers, models);

        foreach (var name in candidates)
        {
            if (Usable(resolver, name) is not { } found) continue;
            var other = AlternativeModel(found.Provider, found.Model, models);
            if (other != null) return Make(found.Provider, name, other, providers, models);
        }

        foreach (var name in candidates)
            if (Usable(resolver, name) is { } found) return Make(found.Provider, name, found.Model, providers, models);
        return null;
    }

    public static ReviewIndependence Classify(string providerName, string model, IReadOnlySet<string> usedProviders, IReadOnlySet<string> usedModels) =>
        !usedProviders.Contains(Norm(providerName)) ? ReviewIndependence.DifferentProvider
        : !usedModels.Contains(model) ? ReviewIndependence.DifferentModel
        : ReviewIndependence.SameModel;

    private static ReviewerChoice Make(IAiProvider provider, string name, string model, IReadOnlySet<string> providers, IReadOnlySet<string> models) =>
        new(provider, name, model, Classify(name, model, providers, models));

    private static (IAiProvider Provider, string Model)? Usable(IAiProviderResolver resolver, string name)
    {
        var provider = resolver.Resolve(name);
        var model = resolver.DefaultModel(name);
        return provider == null || string.IsNullOrWhiteSpace(model) ? null : (provider, model!);
    }

    private static string? AlternativeModel(IAiProvider provider, string preferred, IReadOnlySet<string> used)
    {
        if (!used.Contains(preferred)) return preferred;
        return provider.SupportedModels?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m) && !used.Contains(m));
    }

    private static string Norm(string name) => name.Trim().ToLowerInvariant();
}
