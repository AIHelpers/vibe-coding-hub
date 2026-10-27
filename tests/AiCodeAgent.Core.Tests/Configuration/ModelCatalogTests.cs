using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Tests.Configuration;

/// <summary>E3-S1: model metadata, merge with the account's list, retired filtering, tier mapping, defaults.</summary>
public sealed class ModelCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private const string Json = """
    { "models": [
      { "id": "old-sonnet",  "provider": "acme", "tier": "balanced", "contextWindow": 100000, "retired": true },
      { "id": "new-sonnet",  "provider": "acme", "tier": "balanced", "contextWindow": 200000, "inputPerMillion": 3, "outputPerMillion": 15 },
      { "id": "new-sonnet-b","provider": "acme", "tier": "balanced" },
      { "id": "big",         "provider": "acme", "tier": "frontier" },
      { "id": "small",       "provider": "acme", "tier": "fast" },
      { "id": "only-fast",   "provider": "other", "tier": "fast" }
    ] }
    """;

    private static ModelCatalog Catalog() => ModelCatalog.Parse(Json);

    // ---- parsing -------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsAllFields()
    {
        var m = Catalog().Find("acme", "new-sonnet")!;

        Assert.Equal(ModelTier.Balanced, m.Tier);
        Assert.Equal(200_000, m.ContextWindow);
        Assert.Equal(3m, m.InputPerMillion);
        Assert.Equal(15m, m.OutputPerMillion);
        Assert.False(m.Retired);
        Assert.True(Catalog().Find("acme", "old-sonnet")!.Retired);
    }

    [Fact]
    public void Parse_SkipsEntriesWithoutIdOrProvider_AndRejectsGarbage()
    {
        var catalog = ModelCatalog.Parse("""{ "models": [ { "provider": "a" }, { "id": "x" }, 5, { "id": "ok", "provider": "A" } ] }""");

        Assert.Equal("ok", Assert.Single(catalog.Models).Id);
        Assert.Equal("a", catalog.Models[0].Provider); // provider keys are lower-cased
        Assert.Throws<FormatException>(() => ModelCatalog.Parse("not json"));
        Assert.Throws<FormatException>(() => ModelCatalog.Parse("""{ "nope": [] }"""));
    }

    // ---- retired filtering and defaults ---------------------------------------------------------------------------

    [Fact]
    public void ForProvider_HidesRetiredUnlessAsked()
    {
        var c = Catalog();

        Assert.DoesNotContain(c.ForProvider("acme"), m => m.Retired);
        Assert.Contains(c.ForProvider("acme", includeRetired: true), m => m.Id == "old-sonnet");
    }

    [Fact]
    public void IsRetired_KnowsRetiredModels_AndDatedSnapshots_ButNotUnknownOnes()
    {
        var c = Catalog();

        Assert.True(c.IsRetired("acme", "old-sonnet"));
        Assert.True(c.IsRetired("ACME", "OLD-SONNET"));
        Assert.True(c.IsRetired("acme", "old-sonnet-20240101")); // a dated snapshot of a retired model
        Assert.False(c.IsRetired("acme", "new-sonnet"));
        Assert.False(c.IsRetired("acme", "never-heard-of-it"));
        Assert.False(c.IsRetired("acme", null));
    }

    [Fact]
    public void DefaultModel_IsTheFirstBalancedModel_NeverARetiredOne()
    {
        // old-sonnet comes first in the file but is retired.
        Assert.Equal("new-sonnet", Catalog().DefaultModel("acme"));
    }

    [Fact]
    public void DefaultModel_IsTheFirstBalancedModelTheAccountActuallyLists()
    {
        var c = Catalog();

        Assert.Equal("new-sonnet-b", c.DefaultModel("acme", new[] { "small", "new-sonnet-b", "old-sonnet" }));
        Assert.Equal("new-sonnet", c.DefaultModel("acme", new[] { "new-sonnet-b", "new-sonnet" })); // catalog order, not list order
    }

    [Fact]
    public void DefaultModel_WithOnlyARetiredBalancedModelListed_FallsBackToAnotherKnownLiveModel_OrNull()
    {
        var c = Catalog();

        Assert.Equal("small", c.DefaultModel("acme", new[] { "old-sonnet", "small" }));
        Assert.Null(c.DefaultModel("acme", new[] { "old-sonnet" }));
        Assert.Null(c.DefaultModel("acme", Array.Empty<string>()));
        Assert.Null(c.DefaultModel("unknown-provider"));
    }

    [Fact]
    public void DefaultModel_FindsADatedSnapshotOfACatalogModel()
    {
        Assert.Equal("new-sonnet-20250929", Catalog().DefaultModel("acme", new[] { "new-sonnet-20250929" }));
    }

    // ---- merge ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Merge_AnnotatesWithMetadata_PutsLiveKnownFirst_ThenUnknown_ThenRetired()
    {
        var merged = Catalog().Merge("acme", new[] { "zzz-custom", "old-sonnet", "small", "new-sonnet", "aaa-custom", "big" });

        Assert.Equal(new[] { "new-sonnet", "big", "small", "zzz-custom", "aaa-custom", "old-sonnet" }, merged.Select(m => m.Id));
        Assert.Equal(ModelTier.Frontier, merged.Single(m => m.Id == "big").Tier);
        Assert.Null(merged.Single(m => m.Id == "zzz-custom").Tier);
        Assert.True(merged.Single(m => m.Id == "old-sonnet").Retired);
        Assert.Equal(200_000, merged.Single(m => m.Id == "new-sonnet").ContextWindow);
    }

    [Fact]
    public void Merge_ListsOnlyWhatTheAccountHas_WithoutDuplicates()
    {
        var merged = Catalog().Merge("acme", new[] { "small", "SMALL", " small ", "" });

        Assert.Equal("small", Assert.Single(merged).Id);
    }

    [Fact]
    public void Merge_LabelsRetiredModels()
    {
        var merged = Catalog().Merge("acme", new[] { "old-sonnet", "small" });

        Assert.Equal("small", merged[0].Label);
        Assert.Contains("retired", merged[1].Label);
        Assert.StartsWith("old-sonnet", merged[1].Label);
    }

    [Fact]
    public void Fallback_IsTheNonRetiredCatalogList()
    {
        var fallback = Catalog().Fallback("acme");

        Assert.Equal(new[] { "new-sonnet", "new-sonnet-b", "big", "small" }, fallback.Select(m => m.Id));
        Assert.All(fallback, m => Assert.False(m.Retired));
    }

    // ---- tiers: Auto / Fast / Smart ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Auto", ModelTier.Balanced)]
    [InlineData("auto", ModelTier.Balanced)]
    [InlineData("Fast", ModelTier.Fast)]
    [InlineData("Smart", ModelTier.Frontier)]
    [InlineData("SMART", ModelTier.Frontier)]
    public void Aliases_MapToTiers(string alias, ModelTier expected)
    {
        Assert.True(ModelCatalog.TryTierOfAlias(alias, out var tier));
        Assert.Equal(expected, tier);
    }

    [Fact]
    public void Aliases_RejectOtherText()
    {
        Assert.False(ModelCatalog.TryTierOfAlias("gpt-4o", out _));
        Assert.False(ModelCatalog.TryTierOfAlias(null, out _));
    }

    [Fact]
    public void ResolveAlias_PicksTheTierModel_Available_AndNeverRetired()
    {
        var c = Catalog();

        Assert.Equal("new-sonnet", c.ResolveAlias("Auto", "acme"));
        Assert.Equal("small", c.ResolveAlias("Fast", "acme"));
        Assert.Equal("big", c.ResolveAlias("Smart", "acme"));
        Assert.Equal("new-sonnet-b", c.ResolveAlias("Auto", "acme", new[] { "new-sonnet-b", "old-sonnet" }));
        Assert.Null(c.ResolveAlias("gpt-4o", "acme"));
    }

    [Fact]
    public void ResolveAlias_FallsBackToANeighbourTier_WhenTheTierIsMissing()
    {
        var c = Catalog();

        Assert.Equal("only-fast", c.ResolveAlias("Smart", "other"));
        Assert.Equal("only-fast", c.ResolveAlias("Auto", "other"));
        Assert.Equal("new-sonnet", c.ResolveAlias("Smart", "acme", new[] { "new-sonnet", "small" })); // no frontier listed -> balanced
    }

    [Fact]
    public void ModelForRun_AutoMeansProviderDefault_TiersMapToModels_IdsPassThrough()
    {
        var c = Catalog();

        Assert.Null(c.ModelForRun("Auto", "acme"));
        Assert.Null(c.ModelForRun(null, "acme"));
        Assert.Equal("small", c.ModelForRun("Fast", "acme"));
        Assert.Equal("big", c.ModelForRun("Smart", "acme"));
        Assert.Equal("my-model", c.ModelForRun("my-model", "acme"));
        Assert.Null(c.ModelForRun("old-sonnet", "acme")); // a retired id would only fail
    }

    // ---- overlay (~/.aiagent/models.json) ------------------------------------------------------------------------

    [Fact]
    public void Overlay_ReplacesEntries_KeepsTheirPosition_AndAddsNewOnes()
    {
        var user = ModelCatalog.Parse("""
        { "models": [ { "id": "new-sonnet", "provider": "acme", "tier": "balanced", "retired": true },
                      { "id": "mine", "provider": "acme", "tier": "frontier" } ] }
        """);

        var merged = Catalog().Overlay(user);

        Assert.True(merged.Find("acme", "new-sonnet")!.Retired);
        Assert.Equal("new-sonnet-b", merged.DefaultModel("acme")); // the user retired the first balanced one
        Assert.NotNull(merged.Find("acme", "mine"));
    }

    [Fact]
    public void Loader_AppliesTheUserFile_AndIgnoresABrokenOne()
    {
        Directory.CreateDirectory(_dir);
        var good = Path.Combine(_dir, "good.json");
        File.WriteAllText(good, """{ "models": [ { "id": "my-private-model", "provider": "openai", "tier": "fast" } ] }""");
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ nope");

        var builtIn = ModelCatalogLoader.LoadBuiltIn();

        Assert.NotNull(ModelCatalogLoader.Load(good).Find("openai", "my-private-model"));
        Assert.Equal(builtIn.Models.Count, ModelCatalogLoader.Load(bad).Models.Count);
        Assert.Equal(builtIn.Models.Count, ModelCatalogLoader.Load(Path.Combine(_dir, "missing.json")).Models.Count);
    }

    // ---- the shipped models.json -----------------------------------------------------------------------------------

    [Fact]
    public void BuiltInCatalog_IsEmbeddedAndHasEveryProviderWeShip()
    {
        var c = ModelCatalogLoader.LoadBuiltIn();

        Assert.NotEmpty(c.Models);
        foreach (var provider in new[] { "openai", "anthropic", "ollama", "lmstudio" })
            Assert.NotNull(c.DefaultModel(provider));
    }

    [Fact]
    public void BuiltInCatalog_KnowsTheModelsThatUsedToBeShippedAndAreRetired()
    {
        var c = ModelCatalogLoader.LoadBuiltIn();

        foreach (var (provider, id) in new[]
        {
            ("anthropic", "claude-3-5-sonnet-20241022"), ("anthropic", "claude-3-5-haiku-20241022"),
            ("anthropic", "claude-3-opus-20240229"), ("openai", "gpt-3.5-turbo")
        })
            Assert.True(c.IsRetired(provider, id), $"{id} should be marked retired");
    }

    [Fact]
    public void FreshConfig_NeverSelectsARetiredModel()
    {
        var catalog = ModelCatalogLoader.Default;

        foreach (var (name, provider) in AppConfiguration.GetDefaults().Providers)
            Assert.False(catalog.IsRetired(name, provider.DefaultModel), $"{name} defaults to retired {provider.DefaultModel}");
    }

    [Fact]
    public void FreshConfig_UsesTheCatalogsBalancedDefault_ForTheCloudProviders()
    {
        var defaults = AppConfiguration.GetDefaults();
        var catalog = ModelCatalogLoader.Default;

        foreach (var provider in new[] { "openai", "anthropic" })
        {
            var model = defaults.Providers[provider].DefaultModel;
            Assert.Equal(ModelTier.Balanced, catalog.Lookup(provider, model!)!.Tier);
        }
    }

    [Fact]
    public void EveryTierHasAModel_ForTheCloudProviders()
    {
        var c = ModelCatalogLoader.LoadBuiltIn();

        foreach (var provider in new[] { "openai", "anthropic" })
            foreach (var tier in Enum.GetValues<ModelTier>())
                Assert.NotNull(c.ModelForTier(provider, tier));
    }

    [Fact]
    public void ModelRegistry_ResolvesTheTierAliases_FromTheCatalog()
    {
        var registry = new ModelRegistry(Catalog());

        Assert.Equal("new-sonnet", registry.Resolve("Auto", "acme")!.Id);
        Assert.Equal("small", registry.Resolve("Fast", "acme")!.Id);
        Assert.Equal("big", registry.Resolve("Smart", "acme")!.Id);
        Assert.Equal(200_000, registry.GetById("new-sonnet")!.ContextWindow);
        Assert.Null(registry.GetById("old-sonnet")!.Alias);
    }

    // ---- discovery -----------------------------------------------------------------------------------------------

    private sealed class FakeLister : IAiProvider, IModelLister
    {
        public Func<CancellationToken, Task<string[]>> List { get; init; } = _ => Task.FromResult(Array.Empty<string>());
        public Task<string[]> ListModelIdsAsync(CancellationToken cancellationToken = default) => List(cancellationToken);
        public string Name => "fake";
        public string[] SupportedModels => Array.Empty<string>();
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<string>());
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Discovery_MergesTheAccountsList_AndPicksItsBalancedDefault()
    {
        var provider = new FakeLister { List = _ => Task.FromResult(new[] { "old-sonnet", "small", "new-sonnet-b" }) };

        var result = await new ModelDiscovery(Catalog()).DiscoverAsync("acme", provider, TimeSpan.FromSeconds(3));

        Assert.True(result.FromAccount);
        Assert.Null(result.Notice);
        Assert.Equal("new-sonnet-b", result.DefaultModel);
        Assert.Equal(new[] { "new-sonnet-b", "small", "old-sonnet" }, result.Options.Select(o => o.Id));
    }

    [Fact]
    public async Task Discovery_OnFailure_FallsBackToTheCatalog_WithANotice()
    {
        var provider = new FakeLister { List = _ => throw new HttpRequestException("nope", null, System.Net.HttpStatusCode.Unauthorized) };

        var result = await new ModelDiscovery(Catalog()).DiscoverAsync("acme", provider, TimeSpan.FromSeconds(3));

        Assert.False(result.FromAccount);
        Assert.Contains("built-in", result.Notice);
        Assert.Contains("API key", result.Notice);
        Assert.Equal("new-sonnet", result.DefaultModel);
        Assert.DoesNotContain(result.Options, o => o.Retired);
    }

    [Fact]
    public async Task Discovery_OnTimeout_FallsBack()
    {
        var provider = new FakeLister { List = async ct => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); } };

        var result = await new ModelDiscovery(Catalog()).DiscoverAsync("acme", provider, TimeSpan.FromMilliseconds(80));

        Assert.False(result.FromAccount);
        Assert.Contains("no answer", result.Notice);
    }

    [Fact]
    public async Task Discovery_EmptyAnswer_FallsBack()
    {
        var result = await new ModelDiscovery(Catalog()).DiscoverAsync("acme", new FakeLister(), TimeSpan.FromSeconds(3));

        Assert.False(result.FromAccount);
        Assert.NotNull(result.Notice);
    }

    [Fact]
    public async Task Discovery_CallerCancellation_IsNotAFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new FakeLister { List = async ct => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelDiscovery(Catalog()).DiscoverAsync("acme", provider, TimeSpan.FromSeconds(3), cts.Token));
    }
}
