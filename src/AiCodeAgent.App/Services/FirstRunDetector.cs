using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Providers;

namespace AiCodeAgent.App.Services;

/// <summary>Decides whether the first-run setup wizard should open by itself.</summary>
public static class FirstRunDetector
{
    /// <summary>
    /// True when the wizard was never finished or skipped and there is nothing to chat with yet: no
    /// <c>config.json</c> existed, or no provider has a key (in config or in an environment variable) and no
    /// configured local server answers.
    /// </summary>
    public static async Task<bool> ShouldShowAsync(
        AppConfiguration config,
        bool configFileExisted,
        ILocalProviderProbe probe,
        Func<string, string?>? getEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        if (config.Ui.SetupWizardDone) return false;
        if (!configFileExisted) return true;

        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        foreach (var (name, provider) in config.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ApiKey)) return false;
            var upper = name.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(env($"AI_CODE_AGENT_{upper}_API_KEY"))
                || !string.IsNullOrWhiteSpace(env($"AIAGENT_{upper}_API_KEY")))
                return false;
        }

        var localTargets = config.Providers
            .Where(p => IsLoopback(p.Value.BaseUrl))
            .Select(p => new ProbeTarget(p.Key, p.Key, p.Value.BaseUrl, UsesOllamaApi: p.Key.Equals("ollama", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (localTargets.Count == 0) return true;

        var found = await probe.ProbeAsync(localTargets, cancellationToken).ConfigureAwait(false);
        return found.Count == 0;
    }

    private static bool IsLoopback(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
}
