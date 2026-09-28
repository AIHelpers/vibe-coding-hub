using System.Net;
using System.Net.Sockets;

namespace AiCodeAgent.Tools.Web;

/// <summary>
/// Shared SSRF defenses for the web_fetch tool.
///
/// Used at TWO points, not just one, because a single upfront hostname check
/// is not enough: <see cref="IsBlockedHostAsync"/> is a fast pre-check
/// against the caller-supplied URL, and <see cref="IsBlockedAddress"/> is
/// wired into the HttpClient's <c>SocketsHttpHandler.ConnectCallback</c> so
/// it runs again at the moment a TCP connection is actually opened — for
/// every hop, including redirects, and using whatever address DNS resolves
/// to right then. Without the second check, a redirect to
/// <c>http://127.0.0.1/...</c> or a DNS answer that changes between the
/// pre-check and the connect (DNS rebinding) would sail through.
/// </summary>
public static class SsrfGuard
{
    private static readonly HashSet<string> BlockedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
    };

    public static bool IsBlockedHostName(string host) => BlockedHostNames.Contains(host);

    /// <summary>
    /// True if <paramref name="address"/> is loopback, link-local, a private
    /// (RFC 1918) IPv4 range, a cloud metadata endpoint, or an IPv6
    /// equivalent (unique-local fc00::/7, link-local fe80::/10) — including
    /// an IPv4 address reached via an IPv4-mapped IPv6 address
    /// (<c>::ffff:127.0.0.1</c>), which the previous string-based check
    /// never matched.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10) return true;                                         // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;             // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                         // 192.168.0.0/16
            if (b[0] == 127) return true;                                        // 127.0.0.0/8
            if (b[0] == 0) return true;                                          // 0.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return true;                         // 169.254.0.0/16 (covers the 169.254.169.254 cloud metadata endpoint)
            if (b[0] == 100 && b[1] == 100 && b[2] == 100 && b[3] == 200) return true; // Alibaba Cloud metadata
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback)) return true;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;              // fc00::/7 (unique local)
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true; // fe80::/10 (link-local)
            return false;
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and checks every returned address.
    /// Used as the initial pre-check before a request is even attempted.
    /// Blocks on resolution failure — fail closed, not open.
    /// </summary>
    public static async Task<bool> IsBlockedHostAsync(string host, CancellationToken ct = default)
    {
        if (IsBlockedHostName(host))
            return true;

        if (IPAddress.TryParse(host, out var literal))
            return IsBlockedAddress(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length == 0 || addresses.Any(IsBlockedAddress);
        }
        catch
        {
            // If DNS resolution fails, block the request to be safe.
            return true;
        }
    }
}
