using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Tests.Configuration;

public class ProviderConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ProviderConfiguration();

        Assert.Equal(string.Empty, config.Name);
        Assert.Equal(string.Empty, config.BaseUrl);
        Assert.Null(config.ApiKey);
        Assert.Equal(string.Empty, config.DefaultModel);
        Assert.Equal(120, config.TimeoutSeconds);
        Assert.Empty(config.Headers);
        Assert.True(config.VerifySsl);
    }

    [Fact]
    public void WithApiKey_ReturnsNewInstance_WithApiKeySet()
    {
        var config = new ProviderConfiguration { Name = "openai" };

        var updated = config with { ApiKey = "secret-key" };

        Assert.Equal("secret-key", updated.ApiKey);
        Assert.Null(config.ApiKey);
    }

    [Fact]
    public void WithBaseUrl_ReturnsNewInstance_WithBaseUrlSet()
    {
        var config = new ProviderConfiguration { Name = "openai" };

        var updated = config with { BaseUrl = "https://custom.api.com" };

        Assert.Equal("https://custom.api.com", updated.BaseUrl);
        Assert.Equal(string.Empty, config.BaseUrl);
    }

    [Fact]
    public void WithTimeoutSeconds_ReturnsNewInstance_WithTimeoutSet()
    {
        var config = new ProviderConfiguration();

        var updated = config with { TimeoutSeconds = 300 };

        Assert.Equal(300, updated.TimeoutSeconds);
        Assert.Equal(120, config.TimeoutSeconds);
    }

    [Fact]
    public void WithHeaders_ReturnsNewInstance_WithHeadersSet()
    {
        var config = new ProviderConfiguration();

        var updated = config with { Headers = new Dictionary<string, string> { ["X-Custom"] = "value" } };

        Assert.Single(updated.Headers);
        Assert.Equal("value", updated.Headers["X-Custom"]);
        Assert.Empty(config.Headers);
    }

    [Fact]
    public void WithVerifySslFalse_ReturnsNewInstance_WithSslDisabled()
    {
        var config = new ProviderConfiguration();

        var updated = config with { VerifySsl = false };

        Assert.False(updated.VerifySsl);
        Assert.True(config.VerifySsl);
    }

    [Fact]
    public void FullConfiguration_HasAllPropertiesSet()
    {
        var config = new ProviderConfiguration
        {
            Name = "custom",
            BaseUrl = "https://api.custom.com",
            ApiKey = "key-123",
            DefaultModel = "model-1",
            TimeoutSeconds = 60,
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
            VerifySsl = false
        };

        Assert.Equal("custom", config.Name);
        Assert.Equal("https://api.custom.com", config.BaseUrl);
        Assert.Equal("key-123", config.ApiKey);
        Assert.Equal("model-1", config.DefaultModel);
        Assert.Equal(60, config.TimeoutSeconds);
        Assert.Equal("Bearer token", config.Headers["Authorization"]);
        Assert.False(config.VerifySsl);
    }
}
