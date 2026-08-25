using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Tests;

public class ModelRegistryTests
{
    [Fact]
    public void GetById_ReturnsSeededModel()
    {
        var registry = new ModelRegistry();

        var model = registry.GetById("gpt-4o");

        Assert.NotNull(model);
        Assert.Equal("openai", model.Provider);
        Assert.Equal(128_000, model.ContextWindow);
        Assert.True(model.SupportsStreaming);
    }

    [Fact]
    public void GetById_ReturnsNullForUnknownId()
    {
        var registry = new ModelRegistry();

        Assert.Null(registry.GetById("does-not-exist"));
    }

    [Fact]
    public void Resolve_ConcreteId_ReturnsDirectMatch()
    {
        var registry = new ModelRegistry();

        var model = registry.Resolve("claude-3-5-sonnet-20241022");

        Assert.NotNull(model);
        Assert.Equal("anthropic", model.Provider);
    }

    [Fact]
    public void Resolve_Alias_PrefersMatchingProvider()
    {
        var registry = new ModelRegistry();

        var model = registry.Resolve(ModelRegistry.SmartAlias, "anthropic");

        Assert.NotNull(model);
        Assert.Equal("anthropic", model.Provider);
        Assert.Equal("claude-3-5-sonnet-20241022", model.Id);
    }

    [Fact]
    public void Resolve_Alias_FallsBackToFirstWhenProviderUnknown()
    {
        var registry = new ModelRegistry();

        var model = registry.Resolve(ModelRegistry.SmartAlias, "unknown-provider");

        Assert.NotNull(model);
        Assert.Equal(ModelRegistry.SmartAlias, model.Alias);
    }

    [Fact]
    public void Resolve_Alias_FallsBackToFirstWhenProviderNull()
    {
        var registry = new ModelRegistry();

        var model = registry.Resolve(ModelRegistry.FastAlias);

        Assert.NotNull(model);
        Assert.Equal(ModelRegistry.FastAlias, model.Alias);
    }

    [Fact]
    public void Resolve_ReturnsNullForUnknownAliasOrId()
    {
        var registry = new ModelRegistry();

        Assert.Null(registry.Resolve("nope"));
    }

    [Fact]
    public void Resolve_EmptyString_ReturnsNull()
    {
        var registry = new ModelRegistry();

        Assert.Null(registry.Resolve(""));
        Assert.Null(registry.Resolve(null!));
    }

    [Fact]
    public void Register_AddsNewModelAccessibleByAllLookups()
    {
        var registry = new ModelRegistry();
        var custom = new ModelConfig
        {
            Id = "custom-model",
            Provider = "custom",
            Alias = ModelRegistry.AutoAlias,
            ContextWindow = 64_000,
            MaxOutputTokens = 8_000,
            SupportsStreaming = true
        };

        registry.Register(custom);

        Assert.Same(custom, registry.GetById("custom-model"));
        Assert.Same(custom, registry.Resolve("custom-model"));
        Assert.Contains(custom, registry.ListForAlias(ModelRegistry.AutoAlias));
        Assert.Contains(custom, registry.ListForProvider("custom"));
    }

    [Fact]
    public void Register_ReplacesExistingEntryForSameId()
    {
        var registry = new ModelRegistry();
        var updated = new ModelConfig
        {
            Id = "gpt-4o",
            Provider = "openai",
            Alias = ModelRegistry.SmartAlias,
            ContextWindow = 256_000,
            MaxOutputTokens = 32_768,
            SupportsStreaming = true
        };

        registry.Register(updated);

        var model = registry.GetById("gpt-4o");
        Assert.NotNull(model);
        Assert.Same(updated, model);
        Assert.Equal(256_000, model.ContextWindow);
    }

    [Fact]
    public void Register_ThrowsForEmptyId()
    {
        var registry = new ModelRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new ModelConfig { Id = "" }));
    }

    [Fact]
    public void ListForProvider_ReturnsOnlyMatchingProvider()
    {
        var registry = new ModelRegistry();

        var openaiModels = registry.ListForProvider("openai");

        Assert.Equal(2, openaiModels.Count);
        Assert.All(openaiModels, m => Assert.Equal("openai", m.Provider));
    }

    [Fact]
    public void ListForProvider_IsCaseInsensitive()
    {
        var registry = new ModelRegistry();

        var models = registry.ListForProvider("OPENAI");

        Assert.NotEmpty(models);
    }

    [Fact]
    public void ListForProvider_ReturnsEmptyForUnknownProvider()
    {
        var registry = new ModelRegistry();

        Assert.Empty(registry.ListForProvider("nope"));
    }

    [Fact]
    public void ListForAlias_ReturnsModelsUnderAlias()
    {
        var registry = new ModelRegistry();

        var fastModels = registry.ListForAlias(ModelRegistry.FastAlias);

        Assert.NotEmpty(fastModels);
        Assert.All(fastModels, m => Assert.Equal(ModelRegistry.FastAlias, m.Alias));
    }

    [Fact]
    public void ListForAlias_ReturnsEmptyForUnknownAlias()
    {
        var registry = new ModelRegistry();

        Assert.Empty(registry.ListForAlias("nope"));
        Assert.Empty(registry.ListForAlias(""));
    }

    [Fact]
    public void ListAliases_ContainsWellKnownAliases()
    {
        var registry = new ModelRegistry();

        var aliases = registry.ListAliases();

        Assert.Contains(ModelRegistry.AutoAlias, aliases);
        Assert.Contains(ModelRegistry.FastAlias, aliases);
        Assert.Contains(ModelRegistry.SmartAlias, aliases);
    }

    [Fact]
    public void ListIds_ContainsSeededModelIds()
    {
        var registry = new ModelRegistry();

        var ids = registry.ListIds();

        Assert.Contains("gpt-4o", ids);
        Assert.Contains("claude-3-5-sonnet-20241022", ids);
        Assert.Contains("local-model", ids);
    }
}