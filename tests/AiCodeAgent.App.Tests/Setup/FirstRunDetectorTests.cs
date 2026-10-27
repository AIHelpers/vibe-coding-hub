using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Providers;

namespace AiCodeAgent.App.Tests.Setup;

/// <summary>E3-S2: when the wizard opens by itself.</summary>
public class FirstRunDetectorTests
{
    private sealed class FakeProbe : ILocalProviderProbe
    {
        public bool Finds { get; init; }
        public List<ProbeTarget> Asked { get; } = new();
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DetectedProvider>> ProbeAsync(IEnumerable<ProbeTarget>? targets = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var list = (targets ?? LocalProviderProbe.DefaultTargets).ToList();
            Asked.AddRange(list);
            IReadOnlyList<DetectedProvider> found = Finds
                ? new[] { new DetectedProvider(list[0].Key, list[0].DisplayName, list[0].BaseUrl, Array.Empty<string>()) }
                : Array.Empty<DetectedProvider>();
            return Task.FromResult(found);
        }
    }

    private static string? NoEnv(string _) => null;

    [Fact]
    public async Task NoConfigFile_ShowsTheWizard()
    {
        var probe = new FakeProbe { Finds = true };

        Assert.True(await FirstRunDetector.ShouldShowAsync(AppConfiguration.GetDefaults(), configFileExisted: false, probe, NoEnv));
        Assert.Equal(0, probe.Calls); // no need to look
    }

    [Fact]
    public async Task AlreadyDoneOrSkipped_NeverShows_EvenWithoutAnyProvider()
    {
        var config = AppConfiguration.GetDefaults();
        config.Ui.SetupWizardDone = true;

        Assert.False(await FirstRunDetector.ShouldShowAsync(config, configFileExisted: false, new FakeProbe(), NoEnv));
        Assert.False(await FirstRunDetector.ShouldShowAsync(config, configFileExisted: true, new FakeProbe(), NoEnv));
    }

    [Fact]
    public async Task ExistingConfigWithAKey_DoesNotShow()
    {
        var config = AppConfiguration.GetDefaults();
        config.Providers["openai"] = config.Providers["openai"] with { ApiKey = "sk-1" };

        Assert.False(await FirstRunDetector.ShouldShowAsync(config, true, new FakeProbe(), NoEnv));
    }

    [Theory]
    [InlineData("AI_CODE_AGENT_ANTHROPIC_API_KEY")]
    [InlineData("AIAGENT_OPENAI_API_KEY")]
    public async Task AKeyInTheEnvironment_CountsToo(string variable)
    {
        string? Env(string name) => name == variable ? "sk-env" : null;

        Assert.False(await FirstRunDetector.ShouldShowAsync(AppConfiguration.GetDefaults(), true, new FakeProbe(), Env));
    }

    [Fact]
    public async Task NoKey_ButAConfiguredLocalServerAnswers_DoesNotShow()
    {
        var probe = new FakeProbe { Finds = true };

        Assert.False(await FirstRunDetector.ShouldShowAsync(AppConfiguration.GetDefaults(), true, probe, NoEnv));
        Assert.Contains(probe.Asked, t => t.Key == "ollama" && t.UsesOllamaApi);
        Assert.Contains(probe.Asked, t => t.Key == "lmstudio" && !t.UsesOllamaApi);
        Assert.DoesNotContain(probe.Asked, t => t.Key == "openai"); // cloud providers are not probed
    }

    [Fact]
    public async Task NoKey_AndNoLocalServerAnswers_Shows()
    {
        Assert.True(await FirstRunDetector.ShouldShowAsync(AppConfiguration.GetDefaults(), true, new FakeProbe { Finds = false }, NoEnv));
    }

    [Fact]
    public async Task NoKey_AndNoLocalProviderConfigured_ShowsWithoutProbing()
    {
        var config = AppConfiguration.GetDefaults();
        config.Providers.Remove("ollama");
        config.Providers.Remove("lmstudio");
        var probe = new FakeProbe { Finds = true };

        Assert.True(await FirstRunDetector.ShouldShowAsync(config, true, probe, NoEnv));
        Assert.Equal(0, probe.Calls);
    }
}
