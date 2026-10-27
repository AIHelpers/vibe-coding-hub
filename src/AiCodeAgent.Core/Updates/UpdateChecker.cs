using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiCodeAgent.Core.Updates;

/// <summary>Asks where the newest release is. Replaceable in tests.</summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(SemanticVersion current, UpdatePlatform platform, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads <c>https://api.github.com/repos/AIHelpers/vibe-coding-hub/releases/latest</c> (the newest published release that is
/// neither a draft nor a pre-release) and tells whether it is newer than the running build. It never throws for a network
/// problem: offline, a proxy that says no, a rate limit or an unreadable answer all come back as
/// <see cref="UpdateStatus.Failed"/>, which the app treats as "nothing to show".
/// </summary>
/// <remarks>
/// Proxies: the default handler uses the system proxy (Windows settings, <c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c> elsewhere) and
/// the signed-in user's credentials for proxies that ask for them. To try the banner without publishing a release, point
/// <c>AIAGENT_UPDATE_URL</c> at any URL that returns the same JSON as GitHub's "latest release" call.
/// </remarks>
public sealed class UpdateChecker : IUpdateChecker
{
    public const string DefaultUrl = "https://api.github.com/repos/AIHelpers/vibe-coding-hub/releases/latest";
    public const string UrlEnvironmentVariable = "AIAGENT_UPDATE_URL";

    private const int MaxResponseBytes = 1_000_000;

    private readonly HttpMessageHandler? _handler;
    private readonly Uri _url;
    private readonly TimeSpan _timeout;
    private readonly string _userAgentVersion;

    /// <param name="handler">A fake handler in tests; null uses a real one that honors the system proxy.</param>
    /// <param name="url">The "latest release" address; null uses the environment variable, then GitHub.</param>
    /// <param name="timeout">How long to wait for an answer (default 10 s).</param>
    /// <param name="userAgentVersion">Sent in the User-Agent header (GitHub requires one).</param>
    public UpdateChecker(HttpMessageHandler? handler = null, string? url = null, TimeSpan? timeout = null, string userAgentVersion = "1")
    {
        _handler = handler;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _userAgentVersion = userAgentVersion;
        var configured = url ?? Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
        _url = Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : new Uri(DefaultUrl);
    }

    public async Task<UpdateCheckResult> CheckAsync(SemanticVersion current, UpdatePlatform platform, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            // A repository with no published release answers 404: nothing newer exists.
            if (response.StatusCode == HttpStatusCode.NotFound) return UpdateCheckResult.UpToDate();
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"The update server answered {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = Parse(json, platform);
            if (release == null) return UpdateCheckResult.Failed("The update server's answer was not a release.");
            return release.Version > current ? UpdateCheckResult.Available(release) : UpdateCheckResult.UpToDate(release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed("The update server did not answer in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    private HttpClient CreateClient()
    {
        var handler = _handler ?? new SocketsHttpHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        };
        var client = new HttpClient(handler, disposeHandler: _handler == null)
        {
            Timeout = _timeout,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VibeCodingHub", _userAgentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>Reads GitHub's release JSON. Returns null for a draft, a pre-release, or anything that is not a release.</summary>
    internal static ReleaseInfo? Parse(string json, UpdatePlatform platform)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;

        var tag = Text(root, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var version)) return null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var name = Text(item, "name");
                var url = Text(item, "browser_download_url");
                if (name.Length == 0 || url.Length == 0) continue;
                assets.Add(new ReleaseAsset(name, url, item.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0));
            }
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(Text(root, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when : null;
        var name2 = Text(root, "name");
        var page = Text(root, "html_url");
        return new ReleaseInfo(
            version!,
            tag,
            name2.Length > 0 ? name2 : tag,
            Text(root, "body"),
            page,
            published,
            assets,
            ReleaseAssetPicker.Pick(assets, platform));
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
