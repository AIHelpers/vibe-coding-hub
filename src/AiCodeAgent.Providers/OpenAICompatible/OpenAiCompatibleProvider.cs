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

    /// <inheritdoc />
    public override async Task<string[]> ListModelIdsAsync(CancellationToken cancellationToken = default)
    {
        var json = await HttpClient.GetStringAsync("/v1/models", cancellationToken);
        return ModelListParser.ParseOpenAiCompatible(json); // unfiltered: a local server's ids are not OpenAI's
    }

    public override async Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = await ListModelIdsAsync(cancellationToken);
            return ids.Length > 0 ? ids : _models;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return _models;
        }
    }
}
