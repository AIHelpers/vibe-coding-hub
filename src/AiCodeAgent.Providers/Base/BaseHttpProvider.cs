using System.Net;
using System.Runtime.CompilerServices;
using AiCodeAgent.Core;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Base;

public abstract class BaseHttpProvider : IAiProvider, IDisposable
{
    protected readonly HttpClient HttpClient;
    protected readonly ProviderConfiguration Config;
    protected readonly ILogger Logger;
    private readonly bool _disposeClient;

    protected BaseHttpProvider(
        ProviderConfiguration config,
        ILogger logger,
        IHttpClientFactory? factory = null)
    {
        Config = config;
        Logger = logger;

        var handler = new HttpClientHandler
        {
            // When VerifySsl is true (default), use default certificate validation (secure).
            // When VerifySsl is explicitly set to false, allow self-signed certs for local dev.
            ServerCertificateCustomValidationCallback = config.VerifySsl
                ? null
                : HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        if (factory != null)
        {
            HttpClient = factory.CreateClient();
            HttpClient.BaseAddress = new Uri(config.BaseUrl);
            HttpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
            _disposeClient = false; // Factory manages lifecycle
        }
        else
        {
            HttpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(config.BaseUrl),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            _disposeClient = true; // We own the client, dispose it
        }

        if (!string.IsNullOrEmpty(config.ApiKey))
            HttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

        foreach (var (key, value) in config.Headers)
            HttpClient.DefaultRequestHeaders.Add(key, value);
    }

    public abstract string Name { get; }
    public abstract string[] SupportedModels { get; }

    public abstract Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);

    public abstract IAsyncEnumerable<StreamChunk> StreamAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);

    public virtual async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetAsync("/", cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "Provider {Provider} is not available", Name);
            return false;
        }
    }

    public virtual Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(SupportedModels);

    protected async IAsyncEnumerable<string> ReadSseStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (line.StartsWith("data: ")) yield return line[6..];
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
            HttpClient.Dispose();
    }
}