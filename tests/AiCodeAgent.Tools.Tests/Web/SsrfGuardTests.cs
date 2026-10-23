using System.Net;
using System.Net.Sockets;
using AiCodeAgent.Tools.Web;
using Xunit;

namespace AiCodeAgent.Tools.Tests.Web;

/// <summary>Resolver that answers from memory, so these tests never touch DNS.</summary>
public sealed class FakeHostResolver : IHostResolver
{
    public static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    public Func<string, IPAddress[]> Answer { get; set; } = _ => new[] { PublicAddress };

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        Task.FromResult(Answer(host));
}

public class SsrfGuardTests
{
    [Fact]
    public async Task UnresolvableHost_IsBlocked()
    {
        var resolver = new FakeHostResolver
        {
            Answer = _ => throw new SocketException((int)SocketError.HostNotFound)
        };

        Assert.True(await SsrfGuard.IsBlockedHostAsync("no-such-host.example", resolver));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fd00::1")]
    public async Task ResolverReturnsPrivateIp_IsBlocked(string address)
    {
        var resolver = new FakeHostResolver { Answer = _ => new[] { IPAddress.Parse(address) } };

        Assert.True(await SsrfGuard.IsBlockedHostAsync("intranet.example", resolver));
    }

    [Fact]
    public async Task ResolverReturnsPublicIp_IsAllowed()
    {
        var resolver = new FakeHostResolver();

        Assert.False(await SsrfGuard.IsBlockedHostAsync("example.com", resolver));
    }

    [Fact]
    public async Task ResolverReturnsNoAddresses_IsBlocked()
    {
        var resolver = new FakeHostResolver { Answer = _ => Array.Empty<IPAddress>() };

        Assert.True(await SsrfGuard.IsBlockedHostAsync("empty.example", resolver));
    }
}
