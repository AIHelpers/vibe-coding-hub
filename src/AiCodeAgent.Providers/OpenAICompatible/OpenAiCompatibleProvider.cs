using System.Net.Http.Json;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Providers.Base;
using AiCodeAgent.Providers.OpenAI;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.OpenAICompatible;

/// <summary>
/// Provider for any OpenAI-compatible APIs:
/// LM Studio, vLLM, LocalAI, Groq, Together AI, etc.
/// </summary>
public class OpenAiCompatibleProvider : OpenAiProvider
{
    private readonly string _name;
    private readonly string[] _models;

    public OpenAiCompatibleProvider(
        string name,
        ProviderConfiguration config,
        ILogger<OpenAiCompatibleProvider> logger)
        : base(config, logger)
    {
        _name = name;
        _models = [config.DefaultModel];
    }

    public override string Name => _name;
    public override string[] SupportedModels => _models;

    public override async Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<OpenAiModelsResponse>(
                "/v1/models", cancellationToken);
            return response?.Data.Select(m => m.Id).ToArray() ?? _models;
        }
        catch
        {
            return _models;
        }
    }
}