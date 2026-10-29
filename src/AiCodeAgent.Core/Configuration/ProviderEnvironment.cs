namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Settings from the environment, for CI and containers where no config file exists. For a provider named
/// <c>openai</c>: the key from <c>AI_CODE_AGENT_OPENAI_API_KEY</c> (or <c>AIAGENT_OPENAI_API_KEY</c>), the endpoint from
/// <c>AI_CODE_AGENT_OPENAI_BASE_URL</c> (or <c>AIAGENT_OPENAI_BASE_URL</c>) — which is how a self-hosted OpenAI-compatible
/// server is used — and the default model from <c>AI_CODE_AGENT_OPENAI_MODEL</c> (or <c>AIAGENT_OPENAI_MODEL</c>).
/// They win over the config file.
/// </summary>
public static class ProviderEnvironment
{
    public static string VariableName(string provider, string suffix, bool legacy = false) =>
        $"{(legacy ? "AIAGENT" : "AI_CODE_AGENT")}_{provider.ToUpperInvariant().Replace('-', '_')}_{suffix}";

    public static ProviderConfiguration Apply(ProviderConfiguration config, string provider, Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        string? Read(string suffix)
        {
            var value = getenv(VariableName(provider, suffix)) ?? getenv(VariableName(provider, suffix, legacy: true));
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return config with
        {
            ApiKey = Read("API_KEY") ?? config.ApiKey,
            BaseUrl = Read("BASE_URL") ?? config.BaseUrl,
            DefaultModel = Read("MODEL") ?? config.DefaultModel
        };
    }
}
