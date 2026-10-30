namespace AiCodeAgent.Bench.Tests;

public class StatsTests
{
    [Theory]
    [InlineData(0, 10, 0.0, 0.2775)]
    [InlineData(5, 10, 0.2366, 0.7634)]
    [InlineData(10, 10, 0.7225, 1.0)]
    public void Wilson_MatchesTheKnownIntervals(int successes, int trials, double lo, double hi)
    {
        var (l, h) = Stats.Wilson(successes, trials);
        Assert.InRange(l, lo - 0.002, lo + 0.002);
        Assert.InRange(h, hi - 0.002, hi + 0.002);
    }

    [Fact]
    public void Wilson_WithoutTrials_IsNotANumber()
    {
        var (l, h) = Stats.Wilson(0, 0);
        Assert.True(double.IsNaN(l) && double.IsNaN(h));
    }

    [Fact]
    public void Bootstrap_IsDeterministic_AndBracketsTheMean()
    {
        var values = new[] { 0.0, 1.0, 1.0, 0.0, 1.0, 1.0, 1.0, 0.0, 1.0, 1.0 };
        var a = Stats.BootstrapMean(values);
        var b = Stats.BootstrapMean(values);
        Assert.Equal(a, b);
        Assert.Equal(0.7, a.Mean, 6);
        Assert.True(a.Lo <= a.Mean && a.Mean <= a.Hi);
        Assert.True(a.Lo > 0.3 && a.Hi < 1.0001);
        Assert.Equal(10, a.N);
    }

    [Fact]
    public void Bootstrap_OfOneValue_HasNoWidth_AndOfNone_HasNoValue()
    {
        var one = Stats.BootstrapMean(new[] { 0.5 });
        Assert.Equal(0.5, one.Lo);
        Assert.Equal(0.5, one.Hi);
        Assert.False(Stats.BootstrapMean(Array.Empty<double>()).HasValue);
    }

    [Fact]
    public void PairedDifference_UsesOnlyTasksBothHave()
    {
        var a = new Dictionary<string, double> { ["t1"] = 0, ["t2"] = 1, ["only-a"] = 1 };
        var b = new Dictionary<string, double> { ["t1"] = 1, ["t2"] = 1, ["only-b"] = 0 };
        var d = Stats.PairedDifference(a, b);
        Assert.Equal(2, d.N);
        Assert.Equal(0.5, d.Mean, 6);
    }
}
