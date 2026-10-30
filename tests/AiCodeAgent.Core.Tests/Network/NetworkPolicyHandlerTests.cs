using System.Net;
using System.Net.Sockets;
using AiCodeAgent.Core.Network;

namespace AiCodeAgent.Core.Tests.Network;

/// <summary>E7-S4: the handler every HttpClient sits behind lets localhost and configured providers through in offline mode and refuses the rest.</summary>
public class NetworkPolicyHandlerTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        public List<Uri> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpClient Client(NetworkPolicy policy, HttpMessageHandler inner, string purpose = NetworkPurpose.Other) =>
        new(NetworkPolicyHandler.Wrap(purpose, inner, policy));

    private static NetworkPolicy Offline()
    {
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        policy.SetOffline(true);
        return policy;
    }

    [Fact]
    public async Task Online_everything_is_allowed_and_recorded()
    {
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        var inner = new CountingHandler();
        using var client = Client(policy, inner);

        using var response = await client.GetAsync("https://api.example.com/v1/models");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(1, inner.Calls);
        var entry = Assert.Single(policy.Entries());
        Assert.Equal("api.example.com", entry.Host);
        Assert.False(entry.Blocked);
        Assert.False(entry.IsLocal);
    }

    [Theory]
    [InlineData("http://localhost:11434/api/tags")]
    [InlineData("http://127.0.0.1:1234/v1/models")]
    [InlineData("http://127.5.5.5/x")]
    [InlineData("http://[::1]:8080/x")]
    [InlineData("http://ollama.localhost/x")]
    public async Task Offline_localhost_is_allowed(string url)
    {
        var policy = Offline();
        var inner = new CountingHandler();
        using var client = Client(policy, inner);

        using var response = await client.GetAsync(url);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.True(Assert.Single(policy.Entries()).IsLocal);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/x/y/releases/latest")]
    [InlineData("https://example.com/")]
    [InlineData("http://192.168.1.20:11434/api/tags")] // a LAN host is not this computer
    [InlineData("http://localhost.evil.example/x")]
    [InlineData("http://10.0.0.5/x")]
    public async Task Offline_other_hosts_are_refused_before_any_connection(string url)
    {
        var policy = Offline();
        var inner = new CountingHandler();
        using var client = Client(policy, inner, NetworkPurpose.WebFetch);

        var ex = await Assert.ThrowsAsync<NetworkBlockedException>(() => client.GetAsync(url));

        Assert.Equal(0, inner.Calls);
        Assert.Contains("Offline mode", ex.Message);
        var entry = Assert.Single(policy.Entries());
        Assert.True(entry.Blocked);
        Assert.Equal(NetworkPurpose.WebFetch, entry.Purpose);
        Assert.Equal(0, policy.NonLocalRequests());
        Assert.Equal(1, policy.NonLocalRequests(includeBlocked: true));
    }

    [Fact]
    public async Task Offline_a_configured_provider_host_is_allowed()
    {
        var policy = Offline();
        policy.AllowProviderUrl("https://api.openai.com/v1");
        var inner = new CountingHandler();
        using var client = Client(policy, inner, NetworkPurpose.Provider);

        using var allowed = await client.GetAsync("https://api.openai.com/v1/chat/completions");
        Assert.True(allowed.IsSuccessStatusCode);

        await Assert.ThrowsAsync<NetworkBlockedException>(() => client.GetAsync("https://api.anthropic.com/v1/messages"));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Switching_offline_mode_takes_effect_for_the_next_request()
    {
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        var inner = new CountingHandler();
        using var client = Client(policy, inner);

        using (await client.GetAsync("https://example.com/a")) { }
        policy.SetOffline(true);
        await Assert.ThrowsAsync<NetworkBlockedException>(() => client.GetAsync("https://example.com/b"));
        policy.SetOffline(false);
        using (await client.GetAsync("https://example.com/c")) { }

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void The_environment_variable_forces_offline_mode_on()
    {
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(name => name == NetworkPolicy.OfflineEnvironmentVariable ? "1" : null);
        policy.SetOffline(false);

        Assert.True(policy.OfflineMode);
        Assert.True(policy.OfflineLockedByEnvironment);
        Assert.False(policy.Check(new Uri("https://example.com")).Allowed);

        policy.ReadEnvironment(_ => "no");
        Assert.False(policy.OfflineMode);
    }

    [Fact]
    public async Task Only_the_host_is_remembered_never_the_path_or_query()
    {
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        using var client = Client(policy, new CountingHandler());

        using (await client.GetAsync("https://api.example.com/secret/path?api_key=SHHH&q=my+code")) { }

        var entry = Assert.Single(policy.Entries());
        Assert.Equal("api.example.com", entry.Host);
        var text = string.Join(" ", entry.GetType().GetProperties().Select(p => p.GetValue(entry)?.ToString()));
        Assert.DoesNotContain("SHHH", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task Hosts_are_summarised_with_counts_and_purposes()
    {
        var policy = Offline();
        policy.AllowProviderHost("api.openai.com");
        using var provider = Client(policy, new CountingHandler(), NetworkPurpose.Provider);
        using var update = Client(policy, new CountingHandler(), NetworkPurpose.UpdateCheck);

        using (await provider.GetAsync("https://api.openai.com/a")) { }
        using (await provider.GetAsync("https://api.openai.com/b")) { }
        await Assert.ThrowsAsync<NetworkBlockedException>(() => update.GetAsync("https://api.github.com/x"));

        var hosts = policy.Hosts();
        var openai = Assert.Single(hosts, h => h.Host == "api.openai.com");
        Assert.Equal(2, openai.Requests);
        Assert.Equal(0, openai.Blocked);
        var github = Assert.Single(hosts, h => h.Host == "api.github.com");
        Assert.Equal(0, github.Requests);
        Assert.Equal(1, github.Blocked);
        Assert.Equal(NetworkPurpose.UpdateCheck, Assert.Single(github.Purposes));

        policy.Clear();
        Assert.Empty(policy.Hosts());
    }

    [Fact]
    public async Task An_unrecognised_request_without_an_absolute_address_is_refused()
    {
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        using var client = new HttpClient(NetworkPolicyHandler.Wrap(NetworkPurpose.Other, new CountingHandler(), policy));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/relative/only"));
    }

    // ---- redirects: followed hop by hop, so they cannot lead past the policy ----------------------------------

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public int Port { get; }
        public List<string?> AuthorizationSeen { get; } = new();
        public Func<HttpListenerRequest, (int Status, string? Location)> Respond { get; set; } = _ => (200, null);

        public LoopbackServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    lock (AuthorizationSeen) AuthorizationSeen.Add(ctx.Request.Headers["Authorization"]);
                    var (status, location) = Respond(ctx.Request);
                    ctx.Response.StatusCode = status;
                    if (location != null) ctx.Response.RedirectLocation = location;
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* stopped */ }
            _listener.Close();
        }
    }

    [Fact]
    public async Task Offline_a_redirect_to_an_outside_host_is_refused()
    {
        using var server = new LoopbackServer { Respond = _ => (302, "https://example.com/stolen") };
        var policy = Offline();
        using var client = Client(policy, new HttpClientHandler { AllowAutoRedirect = true });

        var ex = await Assert.ThrowsAsync<NetworkBlockedException>(() => client.GetAsync($"http://127.0.0.1:{server.Port}/start"));

        Assert.Equal("example.com", ex.Uri?.Host);
        Assert.Equal(0, policy.NonLocalRequests());
        Assert.Contains(policy.Entries(), e => e.Host == "example.com" && e.Blocked);
    }

    [Fact]
    public async Task A_redirect_inside_localhost_is_followed_and_credentials_do_not_cross_hosts()
    {
        using var server = new LoopbackServer();
        server.Respond = request => request.Url!.AbsolutePath == "/start"
            ? (302, $"http://localhost:{server.Port}/end")
            : (200, null);
        var policy = Offline();
        using var client = Client(policy, new HttpClientHandler { AllowAutoRedirect = true });
        client.DefaultRequestHeaders.Add("Authorization", "Bearer sekret");

        using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/start");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(2, policy.Entries().Count);
        lock (server.AuthorizationSeen)
        {
            Assert.Equal(2, server.AuthorizationSeen.Count);
            Assert.Equal("Bearer sekret", server.AuthorizationSeen[0]);
            Assert.Null(server.AuthorizationSeen[1]); // 127.0.0.1 -> localhost is another host
        }
    }

    [Fact]
    public async Task A_handler_that_never_followed_redirects_keeps_returning_them()
    {
        using var server = new LoopbackServer { Respond = _ => (302, "https://example.com/x") };
        var policy = Offline();
        using var client = Client(policy, new HttpClientHandler { AllowAutoRedirect = false });

        using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(policy.Entries());
    }
}
