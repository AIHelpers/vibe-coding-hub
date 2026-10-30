using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Network;

/// <summary>Keeps <see cref="NetworkPolicy"/> in step with <c>config.json</c> (offline mode).</summary>
public static class NetworkPolicyConfiguration
{
    /// <summary>Applies the saved setting now and again after every save.</summary>
    public static void Bind(ConfigurationService configuration, NetworkPolicy? policy = null)
    {
        policy ??= NetworkPolicy.Shared;
        Apply(configuration.Config, policy);
        configuration.Saved += (_, _) => Apply(configuration.Config, policy);
    }

    public static void Apply(AppConfiguration config, NetworkPolicy policy) =>
        policy.SetOffline(config.Network?.OfflineMode ?? false);
}
