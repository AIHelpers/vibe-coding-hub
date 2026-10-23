using System.Net;

namespace AiCodeAgent.Tools.Web;

/// <summary>
/// Resolves a host name to IP addresses. Production uses real DNS; tests inject a
/// fake so they never depend on the network.
/// </summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>Real DNS lookup through the operating system resolver.</summary>
public sealed class DnsHostResolver : IHostResolver
{
    public static readonly DnsHostResolver Default = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        Dns.GetHostAddressesAsync(host, ct);
}
