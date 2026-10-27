using System.Net;
using System.Text;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.Core.Tests.Updates;

/// <summary>E3-S5: the GitHub "latest release" call, with a fake HTTP handler.</summary>
public class UpdateCheckerTests
{
    private static readonly UpdatePlatform Win = new(UpdateOs.Windows, UpdateArch.X64);
    private static readonly UpdatePlatform MacArm = new(UpdateOs.MacOs, UpdateArch.Arm64);
    private static readonly UpdatePlatform Linux = new(UpdateOs.Linux, UpdateArch.X64);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Release(string tag, string body = "Fixes things.", bool draft = false, bool prerelease = false, string? assets = null) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "Release {{tag}}",
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "html_url": "https://github.com/AIHelpers/vibe-coding-hub/releases/tag/{{tag}}",
          "published_at": "2026-10-20T08:30:00Z",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [{{assets ?? DefaultAssets(tag)}}]
        }
        """;

    private static string Asset(string name, long size = 1000) =>
        $$"""{ "name": "{{name}}", "size": {{size}}, "browser_download_url": "https://github.com/AIHelpers/vibe-coding-hub/releases/download/x/{{name}}" }""";

    private static string DefaultAssets(string tag)
    {
        var v = tag.TrimStart('v');
        return string.Join(",",
            Asset($"vibe-coding-hub-{v}-win-x64.zip"),
            Asset($"vibe-coding-hub-cli-{v}-win-x64.zip"),
            Asset($"vibe-coding-hub-{v}-linux-x64.tar.gz"),
            Asset($"vibe-coding-hub-{v}-osx-x64.tar.gz"),
            Asset($"vibe-coding-hub-{v}-osx-arm64.tar.gz"));
    }

    private static UpdateChecker Checker(FakeHandler handler, string? url = "https://example.test/latest") => new(handler, url);

    [Fact]
    public async Task ANewerRelease_IsAvailable_WithNotesAndTheFileForThisComputer()
    {
        var handler = new FakeHandler { Respond = _ => Json(Release("v1.3.0", "## What's new\n- Faster startup")) };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse("1.2.0"), Win);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        var release = result.Release!;
        Assert.Equal("1.3.0", release.Version.ToString());
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Equal("Release v1.3.0", release.Name);
        Assert.Contains("Faster startup", release.Notes);
        Assert.Equal("https://github.com/AIHelpers/vibe-coding-hub/releases/tag/v1.3.0", release.PageUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(5, release.Assets.Count);
        Assert.Equal("vibe-coding-hub-1.3.0-win-x64.zip", release.Download!.Name);
    }

    [Fact]
    public async Task TheSameOrAnOlderRelease_IsUpToDate()
    {
        var handler = new FakeHandler { Respond = _ => Json(Release("v1.2.0")) };

        Assert.Equal(UpdateStatus.UpToDate, (await Checker(handler).CheckAsync(SemanticVersion.Parse("1.2.0"), Win)).Status);
        Assert.Equal(UpdateStatus.UpToDate, (await Checker(handler).CheckAsync(SemanticVersion.Parse("1.9.0"), Win)).Status);
    }

    [Theory]
    [InlineData("1.3.0-rc.1", "v1.3.0", true)]   // a release candidate is older than its release
    [InlineData("1.3.0-rc.1", "v1.2.9", false)]
    [InlineData("0.0.0-dev.57", "v1.0.0", true)] // CI snapshot builds
    [InlineData("1.3.0", "v1.3.0-rc.2", false)]  // never go back to a pre-release tag
    [InlineData("1.2.0+local.build", "v1.2.0", false)]
    public async Task PreReleasesAreComparedByTheSemverRules(string current, string latestTag, bool expectUpdate)
    {
        var handler = new FakeHandler { Respond = _ => Json(Release(latestTag)) };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse(current), Win);

        Assert.Equal(expectUpdate ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task ADraftOrPreReleaseAnswer_IsNeverOffered()
    {
        var draft = new FakeHandler { Respond = _ => Json(Release("v9.0.0", draft: true)) };
        var pre = new FakeHandler { Respond = _ => Json(Release("v9.0.0", prerelease: true)) };

        Assert.Equal(UpdateStatus.Failed, (await Checker(draft).CheckAsync(SemanticVersion.Parse("1.0.0"), Win)).Status);
        Assert.Equal(UpdateStatus.Failed, (await Checker(pre).CheckAsync(SemanticVersion.Parse("1.0.0"), Win)).Status);
    }

    [Fact]
    public async Task ARepositoryWithoutReleases_IsUpToDate()
    {
        var handler = new FakeHandler { Respond = _ => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound) };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]            // rate limit
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    public async Task AServerProblem_FailsQuietly(HttpStatusCode status)
    {
        var handler = new FakeHandler { Respond = _ => Json("{}", status) };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Null(result.Release);
        Assert.Contains(((int)status).ToString(), result.Error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    [InlineData("""{ "name": "no tag" }""")]
    public async Task AnUnreadableAnswer_FailsQuietly(string body)
    {
        var handler = new FakeHandler { Respond = _ => Json(body) };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Offline_FailsQuietly_InsteadOfThrowing()
    {
        var handler = new FakeHandler { Respond = _ => throw new HttpRequestException("No such host is known.") };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("No such host", result.Error);
    }

    [Fact]
    public async Task ASlowServer_TimesOut_AsAFailure()
    {
        var handler = new SlowHandler();
        var checker = new UpdateChecker(handler, "https://example.test/latest", TimeSpan.FromMilliseconds(50));

        var result = await checker.CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task WhenTheCallerCancels_ItThrows_ItIsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Checker(new FakeHandler { Respond = _ => Json(Release("v2.0.0")) }).CheckAsync(SemanticVersion.Parse("1.0.0"), Win, cts.Token));
    }

    [Fact]
    public async Task TheRequest_GoesToTheGivenUrl_WithTheHeadersGitHubExpects()
    {
        var handler = new FakeHandler { Respond = _ => Json(Release("v1.0.0")) };

        await Checker(handler, "https://example.test/repos/x/y/releases/latest").CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://example.test/repos/x/y/releases/latest", request.RequestUri!.ToString());
        Assert.StartsWith("VibeCodingHub/", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.True(request.Headers.Contains("X-GitHub-Api-Version"));
    }

    [Fact]
    public void TheDefaultUrl_IsTheGitHubLatestReleaseOfThisRepository()
    {
        Assert.Equal("https://api.github.com/repos/AIHelpers/vibe-coding-hub/releases/latest", UpdateChecker.DefaultUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TheUrlCanBeOverriddenByTheEnvironment_ForTryingTheBannerWithoutARelease(int variant)
    {
        var previous = Environment.GetEnvironmentVariable(UpdateChecker.UrlEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateChecker.UrlEnvironmentVariable, variant == 0 ? "https://fake.test/release.json" : "not a url");
            var handler = new FakeHandler { Respond = _ => Json(Release("v1.0.0")) };

            await new UpdateChecker(handler).CheckAsync(SemanticVersion.Parse("1.0.0"), Win);

            Assert.Equal(variant == 0 ? "https://fake.test/release.json" : UpdateChecker.DefaultUrl, handler.Requests[0].RequestUri!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateChecker.UrlEnvironmentVariable, previous);
        }
    }

    // ---- which file is offered ----

    [Theory]
    [InlineData(UpdateOs.Windows, UpdateArch.X64, "vibe-coding-hub-1.3.0-win-x64.zip")]
    [InlineData(UpdateOs.MacOs, UpdateArch.Arm64, "vibe-coding-hub-1.3.0-osx-arm64.tar.gz")]
    [InlineData(UpdateOs.MacOs, UpdateArch.X64, "vibe-coding-hub-1.3.0-osx-x64.tar.gz")]
    [InlineData(UpdateOs.Linux, UpdateArch.X64, "vibe-coding-hub-1.3.0-linux-x64.tar.gz")]
    public async Task TheFileMatchesTheOsAndCpu_AndNeverTheCommandLineTool(UpdateOs os, UpdateArch arch, string expected)
    {
        var handler = new FakeHandler { Respond = _ => Json(Release("v1.3.0")) };

        var result = await Checker(handler).CheckAsync(SemanticVersion.Parse("1.0.0"), new UpdatePlatform(os, arch));

        Assert.Equal(expected, result.Release!.Download!.Name);
    }

    [Fact]
    public void AnInstaller_BeatsAnArchive()
    {
        var assets = new[]
        {
            new ReleaseAsset("vibe-coding-hub-1.3.0-win-x64.zip", "https://github.com/a", 1),
            new ReleaseAsset("VibeCodingHub-1.3.0-win-x64.msi", "https://github.com/b", 1),
            new ReleaseAsset("VibeCodingHub-1.3.0-win-x64.msi.sha256", "https://github.com/c", 1),
            new ReleaseAsset("vibe-coding-hub-1.3.0-x86_64.AppImage", "https://github.com/d", 1),
            new ReleaseAsset("VibeCodingHub-1.3.0-arm64.dmg", "https://github.com/e", 1),
            new ReleaseAsset("VibeCodingHub-1.3.0-x64.dmg", "https://github.com/f", 1)
        };

        Assert.Equal("VibeCodingHub-1.3.0-win-x64.msi", ReleaseAssetPicker.Pick(assets, Win)!.Name);
        Assert.Equal("vibe-coding-hub-1.3.0-x86_64.AppImage", ReleaseAssetPicker.Pick(assets, Linux)!.Name);
        Assert.Equal("VibeCodingHub-1.3.0-arm64.dmg", ReleaseAssetPicker.Pick(assets, MacArm)!.Name);
    }

    [Fact]
    public void NoMatchingFile_ReturnsNull_SoTheBannerOpensTheReleasePage()
    {
        var assets = new[] { new ReleaseAsset("vibe-coding-hub-1.3.0-linux-x64.tar.gz", "https://github.com/a", 1) };

        Assert.Null(ReleaseAssetPicker.Pick(assets, Win));
        Assert.Null(ReleaseAssetPicker.Pick(Array.Empty<ReleaseAsset>(), Win));
        Assert.Null(ReleaseAssetPicker.Pick(assets, new UpdatePlatform(UpdateOs.Other, UpdateArch.Other)));
    }

    [Fact]
    public void DarwinIsNotWindows()
    {
        var assets = new[] { new ReleaseAsset("vibe-coding-hub-1.3.0-darwin-x64.tar.gz", "https://github.com/a", 1) };

        Assert.Null(ReleaseAssetPicker.Pick(assets, Win));
        Assert.NotNull(ReleaseAssetPicker.Pick(assets, new UpdatePlatform(UpdateOs.MacOs, UpdateArch.X64)));
    }
}
