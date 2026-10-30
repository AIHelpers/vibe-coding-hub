using AiCodeAgent.Core.Network;
using System.Text.Json;

namespace AiCodeAgent.Providers;

/// <summary>A local model server to look for.</summary>
/// <param name="Key">Provider key used in config.json (<c>ollama</c>, <c>lmstudio</c>).</param>
/// <param name="UsesOllamaApi">True: ask <c>/api/tags</c>; false: ask the OpenAI-style <c>/v1/models</c>.</param>
public sealed record ProbeTarget(string Key, string DisplayName, string BaseUrl, bool UsesOllamaApi);

/// <summary>A local server that answered, with the models it offers (possibly none).</summary>
public sealed record DetectedProvider(string Key, string DisplayName, string BaseUrl, IReadOnlyList<string> Models);

public interface ILocalProviderProbe
{
    /// <summary>Asks every target at once; returns the ones that answered, in the order given. Never throws.</summary>
    Task<IReadOnlyList<DetectedProvider>> ProbeAsync(IEnumerable<ProbeTarget>? targets = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Looks for Ollama (<c>localhost:11434</c>) and LM Studio (<c>localhost:1234</c>) with a short timeout, so the
/// first-run wizard can offer them as free, private options. Sends nothing but a GET to the model-list URL.
/// </summary>
public sealed class LocalProviderProbe : ILocalProviderProbe, IDisposable
{
    public static readonly ProbeTarget Ollama = new("ollama", "Ollama", "http://localhost:11434", UsesOllamaApi: true);
    public static readonly ProbeTarget LmStudio = new("lmstudio", "LM Studio", "http://localhost:1234", UsesOllamaApi: false);

    public static IReadOnlyList<ProbeTarget> DefaultTargets { get; } = new[] { Ollama, LmStudio };

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <param name="handler">Test hook: a fake HTTP handler.</param>
    /// <param name="timeout">Per-server limit; one second by default.</param>
    public LocalProviderProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler == null ? new HttpClient(NetworkPolicyHandler.Wrap(NetworkPurpose.ProviderProbe)) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // the per-request token below is the limit
        _timeout = timeout ?? TimeSpan.FromSeconds(1);
    }

    public async Task<IReadOnlyList<DetectedProvider>> ProbeAsync(
        IEnumerable<ProbeTarget>? targets = null, CancellationToken cancellationToken = default)
    {
        var list = (targets ?? DefaultTargets).ToList();
        var results = await Task.WhenAll(list.Select(t => ProbeOneAsync(t, cancellationToken))).ConfigureAwait(false);
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    private async Task<DetectedProvider?> ProbeOneAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            var url = target.BaseUrl.TrimEnd('/') + (target.UsesOllamaApi ? "/api/tags" : "/v1/models");
            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!IsModelList(json, target.UsesOllamaApi ? "models" : "data")) return null; // some other server on that port

            var models = target.UsesOllamaApi ? ModelListParser.ParseOllama(json) : ModelListParser.ParseOpenAiCompatible(json);
            return new DetectedProvider(target.Key, target.DisplayName, target.BaseUrl, models);
        }
        catch (Exception)
        {
            return null; // refused, timed out, not HTTP, cancelled: not detected
        }
    }

    private static bool IsModelList(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var list)
                   && list.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
