using System.Net;
using System.Net.Http.Headers;

namespace AiCodeAgent.Core.Network;

/// <summary>Thrown instead of opening a connection when <see cref="NetworkPolicy"/> refuses the host.</summary>
public sealed class NetworkBlockedException : HttpRequestException
{
    public NetworkBlockedException(string message, Uri? uri) : base(message) => Uri = uri;
    public Uri? Uri { get; }
}

/// <summary>
/// E7-S4: the one place every outgoing HTTP request passes. It asks <see cref="NetworkPolicy"/>, records the host (never the
/// path, query or body) and either sends the request or throws <see cref="NetworkBlockedException"/>.
/// <para>
/// Redirects are followed here, hop by hop, so a redirect cannot lead somewhere the policy would refuse: when the inner handler
/// is an <see cref="HttpClientHandler"/> or <see cref="SocketsHttpHandler"/> that follows redirects, that is switched off and
/// this class follows up to <see cref="MaxRedirects"/> for GET and HEAD. A handler that never followed redirects keeps its behaviour.
/// </para>
/// </summary>
public sealed class NetworkPolicyHandler : DelegatingHandler
{
    public const int MaxRedirects = 5;

    private readonly NetworkPolicy _policy;
    private readonly string _purpose;
    private readonly int _redirectLimit;

    public NetworkPolicyHandler(NetworkPolicy policy, string purpose, HttpMessageHandler? inner = null) : base(inner ?? new HttpClientHandler())
    {
        _policy = policy;
        _purpose = purpose;
        _redirectLimit = TakeOverRedirects(InnerHandler!);
    }

    /// <summary>Wraps <paramref name="inner"/> (or a default handler) with the shared policy.</summary>
    public static HttpMessageHandler Wrap(string purpose, HttpMessageHandler? inner = null, NetworkPolicy? policy = null) =>
        new NetworkPolicyHandler(policy ?? NetworkPolicy.Shared, purpose, inner);

    private static int TakeOverRedirects(HttpMessageHandler inner)
    {
        switch (inner)
        {
            case HttpClientHandler h when h.AllowAutoRedirect:
                var limit = Math.Min(h.MaxAutomaticRedirections, MaxRedirects);
                h.AllowAutoRedirect = false;
                return limit;
            case SocketsHttpHandler s when s.AllowAutoRedirect:
                limit = Math.Min(s.MaxAutomaticRedirections, MaxRedirects);
                s.AllowAutoRedirect = false;
                return limit;
            default:
                return 0;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            var uri = request.RequestUri;
            var decision = _policy.Check(uri);
            if (!decision.Allowed)
            {
                if (uri is { IsAbsoluteUri: true }) _policy.Record(uri, request.Method.Method, _purpose, blocked: true);
                throw new NetworkBlockedException(decision.Reason ?? "Blocked by the network policy.", uri);
            }
            _policy.Record(uri!, request.Method.Method, _purpose, blocked: false);

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (_redirectLimit == 0 || !IsRedirect(response.StatusCode) || response.Headers.Location == null) return response;
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) return response;
            if (hop >= _redirectLimit) return response;

            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri!, response.Headers.Location);
            response.Dispose();
            request = CloneForRedirect(request, next);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage CloneForRedirect(HttpRequestMessage original, Uri target)
    {
        var clone = new HttpRequestMessage(original.Method, target) { Version = original.Version, VersionPolicy = original.VersionPolicy };
        var sameHost = string.Equals(original.RequestUri?.Host, target.Host, StringComparison.OrdinalIgnoreCase);
        foreach (var header in original.Headers)
        {
            // Credentials never follow a redirect to another host.
            if (!sameHost && (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                              header.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}
