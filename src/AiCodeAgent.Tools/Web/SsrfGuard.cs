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

    public static bool IsBlockedHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.TrimEnd('.');
        return BlockedHostNames.Contains(host) ||
               host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

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
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;                 // 100.64.0.0/10 carrier-grade NAT
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;              // 192.0.0.0/24 IETF protocol assignments
            if (b[0] == 198 && (b[1] & 0xFE) == 18) return true;                 // 198.18.0.0/15 benchmarking
            if (b[0] >= 224) return true;                                        // multicast, reserved, broadcast
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return true;
            var b = address.GetAddressBytes();
            if (b[0] == 0xFF) return true;                                         // ff00::/8 multicast
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return true;                // fec0::/10 site-local
            // 64:ff9b::/96 (NAT64) and 2002::/16 (6to4) embed an IPv4 address — judge that one instead.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
                return IsBlockedAddress(new IPAddress(new[] { b[12], b[13], b[14], b[15] }));
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsBlockedAddress(new IPAddress(new[] { b[2], b[3], b[4], b[5] }));
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
