using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Providers;
using AiCodeAgent.Providers.Anthropic;
using AiCodeAgent.Providers.Ollama;
using AiCodeAgent.Providers.OpenAI;
using AiCodeAgent.Providers.OpenAICompatible;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Tests;

public class ProviderFactoryTests
{
    private static readonly ILoggerFactory LoggerFactoryInstance =
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
        });

    private static ProviderConfiguration CreateConfig(string baseUrl = "http://localhost:1234") => new()
    {
        Name = "test",
        BaseUrl = baseUrl,
        DefaultModel = "test-model",
        ApiKey = "test-key",
        TimeoutSeconds = 30,
        VerifySsl = false
    };

    [Fact]
    public void Create_WithOpenAi_ReturnsOpenAiProvider()
    {
        var provider = ProviderFactory.Create("openai", CreateConfig("https://api.openai.com"), LoggerFactoryInstance);
        Assert.IsType<OpenAiProvider>(provider);
        Assert.Equal("OpenAI", provider.Name);
    }

    [Fact]
    public void Create_WithAnthropic_ReturnsAnthropicProvider()
    {
        var provider = ProviderFactory.Create("anthropic", CreateConfig("https://api.anthropic.com"), LoggerFactoryInstance);
        Assert.IsType<AnthropicProvider>(provider);
        Assert.Equal("Anthropic", provider.Name);
    }

    [Fact]
    public void Create_WithOllama_ReturnsOllamaProvider()
    {
        var provider = ProviderFactory.Create("ollama", CreateConfig("http://localhost:11434"), LoggerFactoryInstance);
        Assert.IsType<OllamaProvider>(provider);
        Assert.Equal("Ollama", provider.Name);
    }

    [Fact]
    public void Create_WithUnknownProvider_ReturnsOpenAiCompatibleProvider()
    {
        var provider = ProviderFactory.Create("lmstudio", CreateConfig("http://localhost:1234"), LoggerFactoryInstance);
        var compatible = Assert.IsType<OpenAiCompatibleProvider>(provider);
        Assert.Equal("lmstudio", compatible.Name);
    }

    [Fact]
    public void Create_WithCustomLocalProvider_ReturnsOpenAiCompatibleProvider()
    {
        var provider = ProviderFactory.Create("vllm", CreateConfig("http://localhost:8000"), LoggerFactoryInstance);
        var compatible = Assert.IsType<OpenAiCompatibleProvider>(provider);
        Assert.Equal("vllm", compatible.Name);
    }

    [Fact]
    public void Create_WithCaseInsensitiveName_ReturnsCorrectProvider()
    {
        var provider = ProviderFactory.Create("OpenAI", CreateConfig("https://api.openai.com"), LoggerFactoryInstance);
        Assert.IsType<OpenAiProvider>(provider);
    }

    [Fact]
    public void Create_WithNullOrEmptyName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ProviderFactory.Create("", CreateConfig(), LoggerFactoryInstance));
        Assert.Throws<ArgumentException>(() =>
            ProviderFactory.Create(null!, CreateConfig(), LoggerFactoryInstance));
    }

    [Fact]
    public void Create_WithNullConfig_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProviderFactory.Create("openai", null!, LoggerFactoryInstance));
    }

    [Fact]
    public void Create_WithNullLoggerFactory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProviderFactory.Create("openai", CreateConfig(), null!));
    }

    [Fact]
    public void CreateOrFallback_OnSuccess_ReturnsProvider()
    {
        var provider = ProviderFactory.CreateOrFallback("openai", CreateConfig("https://api.openai.com"), LoggerFactoryInstance);
        Assert.IsType<OpenAiProvider>(provider);
    }

    [Fact]
    public void CreateOrFallback_OnFailure_ReturnsNoOpProvider()
    {
        // Invalid URL will cause the provider constructor to potentially fail
        // or we can test with a config that has an empty BaseUrl
        var badConfig = new ProviderConfiguration
        {
            Name = "bad",
            BaseUrl = "",  // Invalid URL
            DefaultModel = "test"
        };

        var provider = ProviderFactory.CreateOrFallback("custom", badConfig, LoggerFactoryInstance);
        Assert.IsType<NoOpAiProvider>(provider);
    }

    [Fact]
    public void Create_WithCustomUrl_PreservesBaseUrl()
    {
        const string customUrl = "http://192.168.1.100:8080";
        var config = CreateConfig(customUrl);
        var provider = ProviderFactory.Create("localai", config, LoggerFactoryInstance);
        Assert.IsType<OpenAiCompatibleProvider>(provider);
        Assert.Equal("localai", provider.Name);
    }
}