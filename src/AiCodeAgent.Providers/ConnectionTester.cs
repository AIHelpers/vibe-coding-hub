using System.Net;
using System.Net.Sockets;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Providers;

/// <summary>Outcome of "Test connection".</summary>
/// <param name="Message">One plain sentence for the user: what worked, or what to fix.</param>
public sealed record ConnectionTestResult(bool Success, IReadOnlyList<ModelOption> Models, string? DefaultModel, string Message);

public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(string providerKey, ProviderConfiguration config, CancellationToken cancellationToken = default);
}

/// <summary>Lists the models with the given settings: proves that the URL is right and the key is accepted.</summary>
public sealed class ConnectionTester : IConnectionTester
{
    private readonly Func<string, ProviderConfiguration, IAiProvider> _factory;
    private readonly ModelCatalog _catalog;
    private readonly TimeSpan _timeout;

    public ConnectionTester(
        Func<string, ProviderConfiguration, IAiProvider>? factory = null, ModelCatalog? catalog = null, TimeSpan? timeout = null)
    {
        _factory = factory ?? ((key, cfg) => ProviderFactory.Create(key, cfg, NullLoggerFactory.Instance));
        _catalog = catalog ?? ModelCatalogLoader.Default;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<ConnectionTestResult> TestAsync(string providerKey, ProviderConfiguration config, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            var provider = _factory(providerKey, config);
            var ids = provider is IModelLister lister
                ? await lister.ListModelIdsAsync(cts.Token).ConfigureAwait(false)
                : await provider.GetAvailableModelsAsync(cts.Token).ConfigureAwait(false);

            if (ids == null || ids.Length == 0)
                return Fail(NoModelsMessage(providerKey, config));

            var options = _catalog.Merge(providerKey, ids);
            var live = options.Count(o => !o.Retired);
            var defaultModel = _catalog.DefaultModel(providerKey, ids)
                               ?? options.FirstOrDefault(o => !o.Retired)?.Id
                               ?? options[0].Id;
            return new ConnectionTestResult(true, options, defaultModel,
                $"Connected. {live} model{(live == 1 ? "" : "s")} available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ConnectionErrors.Describe(ex, config.BaseUrl, _timeout));
        }
    }

    private static string NoModelsMessage(string providerKey, ProviderConfiguration config) =>
        string.Equals(providerKey, "ollama", StringComparison.OrdinalIgnoreCase)
            ? "Connected to Ollama, but no model is installed yet. Run \"ollama pull llama3.1\" in a terminal, then test again."
            : $"Connected to {config.BaseUrl}, but the server lists no models. Load a model there, then test again.";

    private static ConnectionTestResult Fail(string message) =>
        new(false, Array.Empty<ModelOption>(), null, message);
}

/// <summary>Turns connection failures into one plain sentence that says what to do.</summary>
public static class ConnectionErrors
{
    public static string Describe(Exception ex, string baseUrl, TimeSpan? timeout = null)
    {
        switch (ex)
        {
            case HttpRequestException { StatusCode: { } status }:
                return DescribeStatus(status);

            case OperationCanceledException:
                return timeout is { } t
                    ? $"No answer from {baseUrl} within {t.TotalSeconds:0} seconds. Check the URL and your network."
                    : $"No answer from {baseUrl}. Check the URL and your network.";

            case HttpRequestException http:
                if (Inner<SocketException>(http) is { SocketErrorCode: SocketError.ConnectionRefused })
                    return $"Can't connect to {baseUrl}: nothing is listening there. Is the server running?";
                if (Inner<SocketException>(http) is { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain })
                    return $"Can't find {baseUrl}: check the address for typos.";
                if (Inner<System.Security.Authentication.AuthenticationException>(http) != null)
                    return $"The secure connection to {baseUrl} failed (certificate problem). For a local server with a self-signed certificate, turn off \"Verify SSL\" in Settings.";
                return $"Can't connect to {baseUrl}: {FirstLine(http.Message)}";

            case UriFormatException:
            case InvalidOperationException io when io.Message.Contains("URI", StringComparison.OrdinalIgnoreCase):
                return $"\"{baseUrl}\" is not a valid address. Use something like http://localhost:1234.";

            default:
                return FirstLine(ex.Message);
        }
    }

    public static string DescribeStatus(HttpStatusCode status) => (int)status switch
    {
        401 => "401 Unauthorized: check the key.",
        403 => "403 Forbidden: this key may not list models. Check its permissions.",
        404 => "404 Not Found: check the URL. It should be the server address (for example http://localhost:1234), not a full API path.",
        429 => "429 Too Many Requests: the provider is limiting you. Wait a minute and test again.",
        >= 500 => $"{(int)status} {status}: the provider has a problem right now. Try again later.",
        _ => $"{(int)status} {status}: the provider refused the request."
    };

    private static T? Inner<T>(Exception ex) where T : Exception
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
