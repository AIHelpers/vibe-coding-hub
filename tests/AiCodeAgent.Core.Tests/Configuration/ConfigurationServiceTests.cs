using System.Text.Json;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Tests.Configuration;

public class ConfigurationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigurationService _service;

    public ConfigurationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aiagent-config-tests-" + Guid.NewGuid().ToString("N"));
        _service = new ConfigurationService(_tempDir);
    }

    [Fact]
    public async Task LoadAsync_CreatesDefaultConfig_WhenNoFileExists()
    {
        await _service.LoadAsync();

        Assert.NotNull(_service.Config);
        Assert.Contains("openai", _service.Config.Providers);
        Assert.Contains("anthropic", _service.Config.Providers);
        Assert.Contains("ollama", _service.Config.Providers);
        Assert.Contains("lmstudio", _service.Config.Providers);
    }

    [Fact]
    public async Task LoadAsync_CreatesConfigFile_WhenNoFileExists()
    {
        await _service.LoadAsync();

        Assert.True(File.Exists(Path.Combine(_tempDir, "config.json")));
    }

    [Fact]
    public async Task LoadAsync_LoadsConfigFromFile_WhenFileExists()
    {
        _service.Config.DefaultProvider = "anthropic";
        await _service.SaveAsync();

        var newService = new ConfigurationService(_tempDir);
        await newService.LoadAsync();

        Assert.Equal("anthropic", newService.Config.DefaultProvider);
    }

    [Fact]
    public async Task LoadAsync_PreservesCustomProviderSettings_WhenFileExists()
    {
        _service.Config.Providers["openai"] = _service.Config.Providers["openai"] with
        {
            BaseUrl = "https://custom.openai.com",
            DefaultModel = "gpt-5"
        };
        await _service.SaveAsync();

        var newService = new ConfigurationService(_tempDir);
        await newService.LoadAsync();

        Assert.Equal("https://custom.openai.com", newService.Config.Providers["openai"].BaseUrl);
        Assert.Equal("gpt-5", newService.Config.Providers["openai"].DefaultModel);
    }

    [Fact]
    public async Task LoadAsync_FallsBackToDefaults_WhenFileCorrupted()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        await File.WriteAllTextAsync(configPath, "{ invalid json }");

        await _service.LoadAsync();

        Assert.NotNull(_service.Config);
        Assert.Contains("openai", _service.Config.Providers);
    }

    [Fact]
    public async Task SaveAsync_WritesConfigToFile()
    {
        _service.Config.DefaultProvider = "ollama";
        await _service.SaveAsync();

        var configPath = Path.Combine(_tempDir, "config.json");
        Assert.True(File.Exists(configPath));

        var json = await File.ReadAllTextAsync(configPath);
        Assert.Contains("ollama", json);
    }

    [Fact]
    public async Task SaveAsync_WritesValidJson()
    {
        _service.Config.DefaultProvider = "anthropic";
        await _service.SaveAsync();

        var configPath = Path.Combine(_tempDir, "config.json");
        var json = await File.ReadAllTextAsync(configPath);

        var config = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions.Default);
        Assert.Equal("anthropic", config?.DefaultProvider);
    }

    [Fact]
    public void SetApiKey_SetsApiKey_ForExistingProvider()
    {
        _service.SetApiKey("openai", "test-key-123");

        var provider = _service.GetProvider("openai");
        Assert.Equal("test-key-123", provider?.ApiKey);
    }

    [Fact]
    public void SetApiKey_DoesNothing_ForUnknownProvider()
    {
        _service.SetApiKey("unknown", "test-key");

        Assert.Null(_service.GetProvider("unknown"));
    }

    [Fact]
    public void SetApiKey_DoesNotMutateOriginal_RecordImmutability()
    {
        var original = _service.GetProvider("openai");
        var originalKey = original?.ApiKey;

        _service.SetApiKey("openai", "new-key");

        var updated = _service.GetProvider("openai");
        Assert.Equal("new-key", updated?.ApiKey);
    }

    [Fact]
    public void GetProvider_ReturnsNull_ForUnknownProvider()
    {
        Assert.Null(_service.GetProvider("unknown"));
    }

    [Fact]
    public void GetProvider_ReturnsConfig_ForKnownProvider()
    {
        var provider = _service.GetProvider("openai");

        Assert.NotNull(provider);
        Assert.Equal("https://api.openai.com", provider!.BaseUrl);
        Assert.Equal("gpt-4o", provider.DefaultModel);
    }

    [Fact]
    public void Config_ReturnsDefaults_Initially()
    {
        Assert.Equal("openai", _service.Config.DefaultProvider);
        Assert.Equal(50, _service.Config.Agent.MaxIterations);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
