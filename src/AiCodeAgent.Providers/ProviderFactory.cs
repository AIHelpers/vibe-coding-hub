using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Providers.Anthropic;
using AiCodeAgent.Providers.Ollama;
using AiCodeAgent.Providers.OpenAI;
using AiCodeAgent.Providers.OpenAICompatible;
using AiCodeAgent.Providers.Scripted;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers;

/// <summary>
/// Centralized factory for creating AI providers based on provider name and configuration.
/// Supports built-in providers (OpenAI, Anthropic, Ollama) and any OpenAI-compatible
/// local provider with a custom URL (LM Studio, vLLM, LocalAI, etc.).
/// </summary>
public static class ProviderFactory
{
    /// <summary>
    /// Creates an AI provider instance for the given provider name and configuration.
    /// Unknown provider names fall back to <see cref="OpenAiCompatibleProvider"/>,
    /// which works with any OpenAI-compatible API endpoint.
    /// </summary>
    public static IAiProvider Create(
        string providerName,
        ProviderConfiguration config,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrEmpty(providerName))
            throw new ArgumentException("Provider name cannot be null or empty.", nameof(providerName));

        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (loggerFactory is null)
            throw new ArgumentNullException(nameof(loggerFactory));

        return providerName.ToLowerInvariant() switch
        {
            "openai" => new OpenAiProvider(config, loggerFactory.CreateLogger<OpenAiProvider>()),
            "anthropic" => new AnthropicProvider(config, loggerFactory.CreateLogger<AnthropicProvider>()),
            "ollama" => new OllamaProvider(config, loggerFactory.CreateLogger<OllamaProvider>()),
            ScriptedProviderSupport.ProviderName => CreateScripted(config, loggerFactory),
            _ => new OpenAiCompatibleProvider(providerName, config,
                loggerFactory.CreateLogger<OpenAiCompatibleProvider>())
        };
    }

    /// <summary>The scripted test provider exists only when it is switched on (see <see cref="ScriptedProviderSupport"/>).</summary>
    private static IAiProvider CreateScripted(ProviderConfiguration config, ILoggerFactory loggerFactory) =>
        ScriptedProviderSupport.IsEnabled
            ? new ScriptedProvider(config, loggerFactory.CreateLogger<ScriptedProvider>())
            : throw new InvalidOperationException(
                $"The 'scripted' provider is a test double. Set {ScriptedProviderSupport.EnableVariable}=1 to use it.");

    /// <summary>
    /// Creates an AI provider instance, returning a <see cref="NoOpAiProvider"/> on failure
    /// instead of throwing. Useful for DI containers where startup should not fail.
    /// </summary>
    public static IAiProvider CreateOrFallback(
        string providerName,
        ProviderConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ProviderFactory");
        try
        {
            return Create(providerName, config, loggerFactory);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create AI provider '{Provider}', using fallback", providerName);
            return new NoOpAiProvider(
                loggerFactory.CreateLogger<NoOpAiProvider>(),
                $"Provider '{providerName}' is not properly configured.");
        }
    }
}