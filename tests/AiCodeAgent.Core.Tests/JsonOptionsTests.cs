using System.Text.Json;
using AiCodeAgent.Core;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Tests;

public class JsonOptionsTests
{
    [Fact]
    public void Default_Options_HaveCorrectSettings()
    {
        var options = JsonOptions.Default;

        Assert.NotNull(options.PropertyNamingPolicy);
        Assert.Equal("testName", options.PropertyNamingPolicy!.ConvertName("TestName"));
        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.False(options.WriteIndented);
    }

    [Fact]
    public void Pretty_Options_HaveCorrectSettings()
    {
        var options = JsonOptions.Pretty;

        Assert.NotNull(options.PropertyNamingPolicy);
        Assert.Equal("testName", options.PropertyNamingPolicy!.ConvertName("TestName"));
        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.True(options.WriteIndented);
    }

    [Fact]
    public void Default_Serializes_WithCamelCase()
    {
        var config = new ProviderConfiguration { Name = "TestProvider", BaseUrl = "http://test.com" };

        var json = JsonSerializer.Serialize(config, JsonOptions.Default);

        Assert.Contains("\"name\"", json);
        Assert.Contains("\"baseUrl\"", json);
    }

    [Fact]
    public void Pretty_Serializes_WithIndentation()
    {
        var config = new ProviderConfiguration { Name = "TestProvider" };

        var json = JsonSerializer.Serialize(config, JsonOptions.Pretty);

        Assert.Contains("\n", json);
    }

    [Fact]
    public void Default_Deserializes_WithCaseInsensitive()
    {
        var json = """{"NAME":"test","BASEURL":"http://test.com"}""";

        var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, JsonOptions.Default);

        Assert.Equal("test", config?.Name);
        Assert.Equal("http://test.com", config?.BaseUrl);
    }

    [Fact]
    public void Default_IgnoresNullValues()
    {
        var config = new ProviderConfiguration { Name = "test", ApiKey = null };

        var json = JsonSerializer.Serialize(config, JsonOptions.Default);

        Assert.DoesNotContain("apiKey", json);
    }

    [Fact]
    public void Pretty_IgnoresNullValues()
    {
        var config = new ProviderConfiguration { Name = "test", ApiKey = null };

        var json = JsonSerializer.Serialize(config, JsonOptions.Pretty);

        Assert.DoesNotContain("apiKey", json);
    }

    [Fact]
    public void Default_SerializesAndDeserializes_RoundTrip()
    {
        var config = new AppConfiguration
        {
            DefaultProvider = "openai",
            Providers = new()
            {
                ["openai"] = new() { Name = "openai", BaseUrl = "https://api.openai.com", DefaultModel = "gpt-4o" }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions.Default);

        Assert.Equal("openai", deserialized?.DefaultProvider);
        Assert.Equal("https://api.openai.com", deserialized?.Providers["openai"].BaseUrl);
    }
}
