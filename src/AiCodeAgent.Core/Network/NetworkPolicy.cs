using System.Collections.Concurrent;
using System.Net;

namespace AiCodeAgent.Core.Network;

/// <summary>What a request was sent for. Shown next to each host in Settings → Network activity.</summary>
public static class NetworkPurpose
{
    public const string Provider = "model provider";
    public const string Mcp = "MCP server";
    public const string WebFetch = "web_fetch";
    public const string UpdateCheck = "update check";
    public const string GitHost = "GitHub (issues)";
    public const string ProviderProbe = "local provider detection";
    public const string Other = "other";
}

/// <summary>The answer of <see cref="NetworkPolicy.Check"/>.</summary>
public readonly record struct NetworkDecision(bool Allowed, string? Reason)
{
    public static NetworkDecision Allow { get; } = new(true, null);
    public static NetworkDecision Deny(string reason) => new(false, reason);
}

/// <summary>One outgoing request as it is remembered: where, why and whether it was let through. Never the path, query or body.</summary>
public sealed record NetworkActivityEntry(
    DateTimeOffset Time, string Host, int Port, string Scheme, string Method, string Purpose, bool IsLocal, bool Blocked);

/// <summary>All requests to one host this session (what Settings shows).</summary>
public sealed record NetworkHostSummary(
    string Host, bool IsLocal, int Requests, int Blocked, DateTimeOffset Last, IReadOnlyList<string> Purposes)
{
    public string PurposesText => string.Join(", ", Purposes);
}

/// <summary>
/// E7-S4: decides whether this process may talk to a host, and remembers every attempt. Every <see cref="HttpClient"/> the
/// product creates sits behind a <see cref="NetworkPolicyHandler"/> that asks this class first.
/// <para>
/// <b>Offline mode</b> allows only loopback (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>) and the hosts of model providers
/// that were set up (see <see cref="AllowProviderHost"/>). Everything else is refused before a connection is opened.
/// </para>
/// </summary>
public sealed class NetworkPolicy
{
    /// <summary>Environment variable that forces offline mode on (<c>1</c>, <c>true</c>, <c>yes</c>, <c>on</c>) regardless of the saved setting.</summary>
    public const string OfflineEnvironmentVariable = "AIAGENT_OFFLINE";

    /// <summary>Most requests kept; older ones drop out.</summary>
    public const int MaxEntries = 1000;

    /// <summary>The policy the product's own HTTP clients use. Tests build their own instances.</summary>
    public static NetworkPolicy Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Queue<NetworkActivityEntry> _entries = new();
    private readonly ConcurrentDictionary<string, byte> _providerHosts = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _offline;
    private volatile bool _offlineFromEnvironment;

    /// <summary>Raised (on the requesting thread) after an entry was added or the log was cleared.</summary>
    public event EventHandler? Changed;

    public NetworkPolicy() => ReadEnvironment();

    /// <summary>True while only localhost and the configured provider(s) may be reached.</summary>
    public bool OfflineMode => _offline || _offlineFromEnvironment;

    /// <summary>True when <see cref="OfflineEnvironmentVariable"/> is what switched it on (the setting cannot turn it off then).</summary>
    public bool OfflineLockedByEnvironment => _offlineFromEnvironment;

    /// <summary>The saved setting (Settings → Network activity → Offline mode).</summary>
    public void SetOffline(bool value) => _offline = value;

    /// <summary>Re-reads <see cref="OfflineEnvironmentVariable"/> (<paramref name="getVariable"/> is a test hook).</summary>
    public void ReadEnvironment(Func<string, string?>? getVariable = null) =>
        _offlineFromEnvironment = IsTruthy((getVariable ?? Environment.GetEnvironmentVariable)(OfflineEnvironmentVariable));

    /// <summary>A provider that is set up: its host stays reachable in offline mode.</summary>
    public void AllowProviderHost(string? host)
    {
        if (!string.IsNullOrWhiteSpace(host)) _providerHosts[host.Trim().Trim('[', ']')] = 0;
    }

    /// <summary>Same, from a base URL; ignores text that is not an absolute URL.</summary>
    public void AllowProviderUrl(string? baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) AllowProviderHost(uri.Host);
    }

    public IReadOnlyCollection<string> ProviderHosts => _providerHosts.Keys.ToList();

    public void ClearProviderHosts() => _providerHosts.Clear();

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().TrimEnd('.').Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())));
    }

    /// <summary>Would a request to this address be let through right now?</summary>
    public NetworkDecision Check(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || string.IsNullOrEmpty(uri.Host))
            return NetworkDecision.Deny("The request has no absolute address.");
        if (!OfflineMode) return NetworkDecision.Allow;
        if (IsLocalHost(uri.Host)) return NetworkDecision.Allow;
        if (_providerHosts.ContainsKey(uri.Host.Trim('[', ']'))) return NetworkDecision.Allow;
        return NetworkDecision.Deny(
            $"Offline mode is on: '{uri.Host}' is neither localhost nor a configured model provider. " +
            "Turn it off in Settings → Advanced → Network activity" +
            (OfflineLockedByEnvironment ? $" (and unset {OfflineEnvironmentVariable})" : "") + ".");
    }

    /// <summary>Remembers one attempt.</summary>
    public void Record(Uri uri, string method, string purpose, bool blocked)
    {
        var entry = new NetworkActivityEntry(
            DateTimeOffset.UtcNow, uri.Host.Trim('[', ']'), uri.Port, uri.Scheme, method, purpose, IsLocalHost(uri.Host), blocked);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every remembered request, oldest first.</summary>
    public IReadOnlyList<NetworkActivityEntry> Entries()
    {
        lock (_gate) return _entries.ToList();
    }

    /// <summary>One row per host, most recent first.</summary>
    public IReadOnlyList<NetworkHostSummary> Hosts()
    {
        return Entries()
            .GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NetworkHostSummary(
                g.Key, g.First().IsLocal, g.Count(e => !e.Blocked), g.Count(e => e.Blocked), g.Max(e => e.Time),
                g.Select(e => e.Purpose).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(h => h.Last)
            .ToList();
    }

    /// <summary>Requests that went (or were refused) to a host that is not localhost.</summary>
    public int NonLocalRequests(bool includeBlocked = false) =>
        Entries().Count(e => !e.IsLocal && (includeBlocked || !e.Blocked));

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsTruthy(string? value) =>
        value != null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
