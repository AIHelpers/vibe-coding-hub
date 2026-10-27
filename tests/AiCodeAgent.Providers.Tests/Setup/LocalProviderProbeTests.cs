using System.Diagnostics;
using System.Net;
using AiCodeAgent.Providers;

namespace AiCodeAgent.Providers.Tests.Setup;

/// <summary>E3-S2: finding Ollama and LM Studio on this computer (fake HTTP handler, no network).</summary>
public class LocalProviderProbeTests
{
    private const string OllamaTags = """{ "models": [ { "name": "llama3.1:latest" }, { "name": "qwen2.5-coder:7b" } ] }""";
    private const string LmStudioModels = """{ "object": "list", "data": [ { "id": "qwen2.5-coder-7b-instruct", "object": "model" } ] }""";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public List<string> Requests { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add($"{request.Method} {request.RequestUri}");
            return _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static Task<HttpResponseMessage> Both(HttpRequestMessage r, CancellationToken _) =>
        Task.FromResult(r.RequestUri!.Port == 11434 ? Json(OllamaTags) : Json(LmStudioModels));

    [Fact]
    public async Task FindsBoth_InOrder_WithTheirModels()
    {
        var handler = new FakeHandler(Both);

        var found = await new LocalProviderProbe(handler).ProbeAsync();

        Assert.Equal(new[] { "ollama", "lmstudio" }, found.Select(f => f.Key));
        Assert.Equal(new[] { "llama3.1:latest", "qwen2.5-coder:7b" }, found[0].Models);
        Assert.Equal(new[] { "qwen2.5-coder-7b-instruct" }, found[1].Models);
        Assert.Equal("http://localhost:11434", found[0].BaseUrl);
    }

    [Fact]
    public async Task AsksTheRightUrls_WithGet()
    {
        var handler = new FakeHandler(Both);

        await new LocalProviderProbe(handler).ProbeAsync();

        Assert.Contains("GET http://localhost:11434/api/tags", handler.Requests);
        Assert.Contains("GET http://localhost:1234/v1/models", handler.Requests);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OnlyOllamaRunning_FindsOnlyOllama()
    {
        var handler = new FakeHandler((r, _) => r.RequestUri!.Port == 11434
            ? Task.FromResult(Json(OllamaTags))
            : throw new HttpRequestException("Connection refused"));

        var found = await new LocalProviderProbe(handler).ProbeAsync();

        Assert.Equal("ollama", Assert.Single(found).Key);
    }

    [Fact]
    public async Task NothingRunning_FindsNothing_AndDoesNotThrow()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("Connection refused"));

        Assert.Empty(await new LocalProviderProbe(handler).ProbeAsync());
    }

    [Fact]
    public async Task ServerThatDoesNotAnswer_IsGivenUpOnAfterTheTimeout_AndServersAreAskedInParallel()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        var probe = new LocalProviderProbe(handler, TimeSpan.FromMilliseconds(250));

        var clock = Stopwatch.StartNew();
        var found = await probe.ProbeAsync();
        clock.Stop();

        Assert.Empty(found);
        Assert.True(clock.ElapsedMilliseconds < 1500, $"took {clock.ElapsedMilliseconds} ms; two 250 ms probes should overlap");
    }

    [Fact]
    public async Task OllamaWithoutModels_IsStillDetected()
    {
        var handler = new FakeHandler((r, _) => Task.FromResult(r.RequestUri!.Port == 11434 ? Json("""{ "models": [] }""") : Json("", HttpStatusCode.NotFound)));

        var found = await new LocalProviderProbe(handler).ProbeAsync();

        var ollama = Assert.Single(found);
        Assert.Empty(ollama.Models);
    }

    [Theory]
    [InlineData("<html>Hello</html>")]
    [InlineData("""{ "something": "else" }""")]
    [InlineData("[]")]
    public async Task SomeOtherServerOnThePort_IsNotMistakenForAModelServer(string body)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(body)));

        Assert.Empty(await new LocalProviderProbe(handler).ProbeAsync());
    }

    [Fact]
    public async Task ErrorStatus_IsNotDetected()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.InternalServerError)));

        Assert.Empty(await new LocalProviderProbe(handler).ProbeAsync());
    }

    [Fact]
    public async Task CustomTargets_AreProbed_WithTheirOwnApi()
    {
        var handler = new FakeHandler((r, _) => Task.FromResult(Json(LmStudioModels)));
        var targets = new[] { new ProbeTarget("vllm", "vLLM", "http://localhost:8000/", UsesOllamaApi: false) };

        var found = await new LocalProviderProbe(handler).ProbeAsync(targets);

        Assert.Equal("vllm", Assert.Single(found).Key);
        Assert.Contains("GET http://localhost:8000/v1/models", handler.Requests);
    }

    [Fact]
    public async Task Cancellation_ReturnsWithoutDetection()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHandler(Both);

        Assert.Empty(await new LocalProviderProbe(handler).ProbeAsync(null, cts.Token));
    }
}
