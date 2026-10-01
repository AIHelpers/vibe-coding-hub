namespace AiCodeAgent.Core.Configuration;

public record ProviderConfiguration
{
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    public string DefaultModel { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 120;
    public Dictionary<string, string> Headers { get; init; } = new();
    public bool VerifySsl { get; init; } = true;
}