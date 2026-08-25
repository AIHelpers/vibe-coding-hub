namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Describes a model the agent can use, including its provider, an alias,
/// context window size, maximum output tokens, and whether streaming is
/// supported.
/// </summary>
public record ModelConfig
{
    /// <summary>Stable unique identifier for the model (e.g. "gpt-4o").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Provider key that hosts the model (matches <see cref="ProviderConfiguration.Name"/>).</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Short human-friendly alias such as "Auto", "Fast", or "Smart". Multiple
    /// models can share an alias; the registry resolves an alias to a concrete
    /// model id based on the active/default provider.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>Maximum context window in tokens.</summary>
    public int ContextWindow { get; init; } = 128_000;

    /// <summary>Maximum number of tokens the model can emit in a single response.</summary>
    public int MaxOutputTokens { get; init; } = 4_096;

    /// <summary>Whether the provider supports streaming responses for this model.</summary>
    public bool SupportsStreaming { get; init; } = true;
}