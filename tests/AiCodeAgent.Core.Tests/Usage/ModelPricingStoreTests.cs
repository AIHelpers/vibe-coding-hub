using AiCodeAgent.Core.Usage;

namespace AiCodeAgent.Core.Tests.Usage;

/// <summary>E4-S5: the price table editor reads and writes pricing.json through <see cref="ModelPricing"/>.</summary>
public class ModelPricingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pricing-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string PathInDir => Path.Combine(_dir, "nested", "pricing.json");

    [Fact]
    public void SaveThenLoad_RoundTripsTheRows_SortedByKey()
    {
        ModelPricing.SaveOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>("gpt-4o", new ModelPrice(2.5m, 10m, 1.25m, null)),
            new KeyValuePair<string, ModelPrice>("claude-sonnet", new ModelPrice(3m, 15m))
        }, PathInDir);

        var rows = ModelPricing.LoadOverrideRows(PathInDir);

        Assert.Equal(new[] { "claude-sonnet", "gpt-4o" }, rows.Select(r => r.Key));
        Assert.Equal(new ModelPrice(3m, 15m), rows[0].Value);
        Assert.Equal(1.25m, rows[1].Value.CacheReadPerMillion);
        Assert.Null(rows[1].Value.CacheWritePerMillion);
    }

    [Fact]
    public void TheFile_UsesTheDocumentedShape_AndOmitsEmptyCachePrices()
    {
        ModelPricing.SaveOverrides(new[] { new KeyValuePair<string, ModelPrice>("claude-sonnet", new ModelPrice(3m, 15m)) }, PathInDir);

        var json = File.ReadAllText(PathInDir);

        Assert.Contains("\"claude-sonnet\"", json);
        Assert.Contains("\"InputPerMillion\": 3", json);
        Assert.Contains("\"OutputPerMillion\": 15", json);
        Assert.DoesNotContain("CacheReadPerMillion", json);
    }

    [Fact]
    public void ARowTypedInLowerCase_IsStillReadByTheEngine()
    {
        // The engine's own loader is case-insensitive about property names, so a hand-edited file keeps working.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "pricing.json");
        File.WriteAllText(path, "{ \"m\": { \"inputPerMillion\": 1, \"outputPerMillion\": 2 } }");

        var rows = ModelPricing.LoadOverrideRows(path);

        Assert.Single(rows);
        Assert.Equal(new ModelPrice(1m, 2m), rows[0].Value);
    }

    [Fact]
    public void MissingOrBrokenFile_GivesNoRows()
    {
        Assert.Empty(ModelPricing.LoadOverrideRows(PathInDir));

        Directory.CreateDirectory(_dir);
        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.Empty(ModelPricing.LoadOverrideRows(broken));
    }

    [Fact]
    public void BlankKeysAreDropped_AndDuplicatesCollapse()
    {
        ModelPricing.SaveOverrides(new[]
        {
            new KeyValuePair<string, ModelPrice>("  ", new ModelPrice(1m, 1m)),
            new KeyValuePair<string, ModelPrice>("m", new ModelPrice(1m, 1m)),
            new KeyValuePair<string, ModelPrice>("M", new ModelPrice(2m, 2m))
        }, PathInDir);

        var rows = ModelPricing.LoadOverrideRows(PathInDir);

        Assert.Single(rows);
        Assert.Equal(2m, rows[0].Value.InputPerMillion);
    }

    [Fact]
    public void BuiltInPrices_AreListed()
    {
        Assert.True(ModelPricing.BuiltIn.ContainsKey("claude-sonnet"));
    }
}
