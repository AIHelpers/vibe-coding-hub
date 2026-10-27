using System.Net;
using System.Net.Sockets;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers;

namespace AiCodeAgent.Providers.Tests.Setup;

/// <summary>E3-S2: "Test connection" lists models, and every failure becomes one plain sentence.</summary>
public class ConnectionTesterTests
{
    private sealed class FakeProvider : IAiProvider, IModelLister
    {
        public Func<CancellationToken, Task<string[]>> List { get; init; } = _ => Task.FromResult(Array.Empty<string>());
        public Task<string[]> ListModelIdsAsync(CancellationToken cancellationToken = default) => List(cancellationToken);
        public string Name => "fake";
        public string[] SupportedModels => Array.Empty<string>();
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => List(cancellationToken);
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static ConnectionTester Tester(Func<CancellationToken, Task<string[]>> list, TimeSpan? timeout = null) =>
        new((_, _) => new FakeProvider { List = list }, ModelCatalogLoader.LoadBuiltIn(), timeout);

    private static ProviderConfiguration Cfg(string url = "https://api.openai.com") => new() { Name = "openai", BaseUrl = url, ApiKey = "sk-x" };

    [Fact]
    public async Task Success_ListsModels_AndPicksTheBalancedDefault()
    {
        var tester = Tester(_ => Task.FromResult(new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-3.5-turbo" }));

        var result = await tester.TestAsync("openai", Cfg());

        Assert.True(result.Success);
        Assert.Equal("gpt-5.6-terra", result.DefaultModel);
        Assert.Contains("2 models available", result.Message); // the retired one is not counted
        Assert.True(result.Models.Single(m => m.Id == "gpt-3.5-turbo").Retired);
    }

    [Fact]
    public async Task WrongKey_SaysSoInPlainWords()
    {
        var tester = Tester(_ => throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized));

        var result = await tester.TestAsync("openai", Cfg());

        Assert.False(result.Success);
        Assert.Equal("401 Unauthorized: check the key.", result.Message);
        Assert.Empty(result.Models);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "403 Forbidden")]
    [InlineData(HttpStatusCode.NotFound, "404 Not Found: check the URL")]
    [InlineData(HttpStatusCode.TooManyRequests, "429 Too Many Requests")]
    [InlineData(HttpStatusCode.BadGateway, "502 BadGateway: the provider has a problem")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503 ServiceUnavailable")]
    [InlineData(HttpStatusCode.Conflict, "409 Conflict: the provider refused")]
    public void StatusCodes_AreExplained(HttpStatusCode status, string expectedStart) =>
        Assert.StartsWith(expectedStart, ConnectionErrors.DescribeStatus(status));

    [Fact]
    public async Task NothingListening_SaysTheServerIsNotRunning()
    {
        var refused = new HttpRequestException("No connection could be made", new SocketException((int)SocketError.ConnectionRefused));
        var tester = Tester(_ => throw refused);

        var result = await tester.TestAsync("lmstudio", Cfg("http://localhost:1234"));

        Assert.False(result.Success);
        Assert.Contains("http://localhost:1234", result.Message);
        Assert.Contains("Is the server running?", result.Message);
    }

    [Fact]
    public async Task UnknownHost_AsksToCheckTheAddress()
    {
        var tester = Tester(_ => throw new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));

        var result = await tester.TestAsync("custom", Cfg("http://no-such-host.example"));

        Assert.Contains("check the address", result.Message);
    }

    [Fact]
    public async Task NoAnswer_SaysHowLongWeWaited()
    {
        var tester = Tester(async ct => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); }, TimeSpan.FromMilliseconds(100));

        var result = await tester.TestAsync("openai", Cfg());

        Assert.False(result.Success);
        Assert.StartsWith("No answer from https://api.openai.com", result.Message);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tester = Tester(async ct => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tester.TestAsync("openai", Cfg(), cts.Token));
    }

    [Fact]
    public async Task OllamaWithoutModels_TellsHowToPullOne()
    {
        var tester = Tester(_ => Task.FromResult(Array.Empty<string>()));

        var result = await tester.TestAsync("ollama", Cfg("http://localhost:11434"));

        Assert.False(result.Success);
        Assert.Contains("ollama pull", result.Message);
    }

    [Fact]
    public async Task OtherServerWithoutModels_SaysTheListIsEmpty()
    {
        var tester = Tester(_ => Task.FromResult(Array.Empty<string>()));

        var result = await tester.TestAsync("custom", Cfg("http://localhost:8000"));

        Assert.False(result.Success);
        Assert.Contains("lists no models", result.Message);
    }

    [Fact]
    public async Task BadAddress_IsReported()
    {
        var tester = new ConnectionTester((_, _) => throw new UriFormatException("Invalid URI"), ModelCatalogLoader.LoadBuiltIn());

        var result = await tester.TestAsync("custom", Cfg("not a url"));

        Assert.False(result.Success);
        Assert.Contains("not a valid address", result.Message);
    }

    [Fact]
    public async Task UnknownLocalModels_PickTheFirstOne()
    {
        var tester = Tester(_ => Task.FromResult(new[] { "my-finetune:latest", "other:7b" }));

        var result = await tester.TestAsync("ollama", Cfg("http://localhost:11434"));

        Assert.True(result.Success);
        Assert.Equal("my-finetune:latest", result.DefaultModel);
    }
}
