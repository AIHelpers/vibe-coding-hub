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

    /// <summary>
    /// Sends a request with retry + exponential backoff on transient
    /// failures (HTTP 429 rate-limit, 529 overloaded, and any 5xx). Only
    /// meant to wrap a request whose response hasn't started being consumed
    /// by the caller yet — for streaming calls that means retrying the
    /// initial connect/headers phase, never a partially-read stream.
    /// Honors a `Retry-After` header when the server sends one.
    /// </summary>
    protected async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        int maxAttempts = 4)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
                response?.Dispose();

            response = await send().ConfigureAwait(false);

            if (response.IsSuccessStatusCode || !IsRetryableStatus(response.StatusCode) || attempt == maxAttempts)
                return response;

            var delay = GetRetryDelay(response, attempt);
            Logger.LogWarning(
                "{Provider} request failed with {Status} (attempt {Attempt}/{Max}); retrying in {DelayMs}ms",
                Name, (int)response.StatusCode, attempt, maxAttempts, delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return response!;
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        (int)status == 429 || (int)status == 529 || (int)status >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            if (diff > TimeSpan.Zero) return diff;
        }

        // Exponential backoff with jitter: ~500ms, ~1s, ~2s, ...
        var baseDelayMs = 500 * Math.Pow(2, attempt - 1);
        var jitterMs = Random.Shared.NextDouble() * 250;
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }

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