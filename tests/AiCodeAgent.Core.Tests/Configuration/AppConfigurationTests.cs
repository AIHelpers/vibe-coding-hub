using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Tests.Configuration;

public class AppConfigurationTests
{
    [Fact]
    public void GetDefaults_ReturnsExpectedProviders()
    {
        var config = AppConfiguration.GetDefaults();

        Assert.NotEmpty(config.Providers);
        Assert.Contains("openai", config.Providers.Keys);
        Assert.Contains("anthropic", config.Providers.Keys);
        Assert.Contains("ollama", config.Providers.Keys);
        Assert.Contains("lmstudio", config.Providers.Keys);
    }

    [Fact]
    public void GetDefaults_SetsDefaultProviderToOpenAi()
    {
        var config = AppConfiguration.GetDefaults();

        Assert.Equal("openai", config.DefaultProvider);
    }

    [Fact]
    public void GetDefaults_ConfiguresOpenAiProvider_WithExpectedValues()
    {
        var config = AppConfiguration.GetDefaults();
        var openai = config.Providers["openai"];

        Assert.Equal("https://api.openai.com", openai.BaseUrl);
        Assert.Equal("gpt-4o", openai.DefaultModel);
        Assert.Equal(120, openai.TimeoutSeconds);
    }

    [Fact]
    public void GetDefaults_ConfiguresOllamaProvider_WithLocalUrl()
    {
        var config = AppConfiguration.GetDefaults();
        var ollama = config.Providers["ollama"];

        Assert.Equal("http://localhost:11434", ollama.BaseUrl);
        Assert.Equal(300, ollama.TimeoutSeconds);
    }

    [Fact]
    public void AgentConfiguration_HasSensibleDefaults()
    {
        var agent = new AgentConfiguration();

        Assert.Equal(50, agent.MaxIterations);
        Assert.Equal(150_000, agent.MaxTokens);
        Assert.False(agent.AutoApprove);
        Assert.True(agent.EnableMemory);
    }

    [Fact]
    public void UiConfiguration_HasSensibleDefaults()
    {
        var ui = new UiConfiguration();

        Assert.Equal("dark", ui.Theme);
        Assert.True(ui.ShowTokenCount);
        Assert.True(ui.ShowToolOutput);
        Assert.True(ui.SyntaxHighlight);
    }
}