using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Providers.Tests.Setup;

/// <summary>E3-S2: the app's provider follows the saved settings, so the wizard's choice works without a restart.</summary>
public class ConfiguredAiProviderTests
{
    private static ProviderConfiguration Cfg(string key = "sk-1", string model = "m", string url = "https://api.openai.com") =>
        new() { Name = "x", BaseUrl = url, ApiKey = key, DefaultModel = model };

    [Fact]
    public void FollowsTheSettings_WhenTheProviderChanges()
    {
        var current = ("openai", Cfg());
        var provider = new ConfiguredAiProvider(() => current, NullLoggerFactory.Instance);

        Assert.Equal("OpenAI", provider.Name);

        current = ("anthropic", Cfg(url: "https://api.anthropic.com"));
        Assert.Equal("Anthropic", provider.Name);

        current = ("ollama", Cfg(url: "http://localhost:11434"));
        Assert.Equal("Ollama", provider.Name);
    }

    [Fact]
    public void KeepsTheSameProvider_WhileTheSettingsStayTheSame()
    {
        var provider = new ConfiguredAiProvider(() => ("openai", Cfg()), NullLoggerFactory.Instance);

        Assert.Same(provider.Current, provider.Current);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("model")]
    [InlineData("url")]
    public void BuildsANewProvider_WhenKeyModelOrUrlChange(string what)
    {
        var current = ("openai", Cfg());
        var provider = new ConfiguredAiProvider(() => current, NullLoggerFactory.Instance);
        var before = provider.Current;

        current = ("openai", what switch
        {
            "key" => Cfg(key: "sk-2"),
            "model" => Cfg(model: "other"),
            _ => Cfg(url: "https://example.com")
        });

        Assert.NotSame(before, provider.Current);
    }

    [Fact]
    public void NothingConfigured_GivesTheFriendlyNoOpProvider()
    {
        var provider = new ConfiguredAiProvider(() => null, NullLoggerFactory.Instance);

        Assert.Equal("NoOpProvider", provider.Name);
    }

    [Fact]
    public async Task NoOpAnswersWithAnErrorMessage_InsteadOfThrowing()
    {
        var provider = new ConfiguredAiProvider(() => null, NullLoggerFactory.Instance);

        var response = await provider.CompleteAsync(new AiCodeAgent.Core.Models.CompletionRequest());

        Assert.Equal("error", response.StopReason);
    }
}
