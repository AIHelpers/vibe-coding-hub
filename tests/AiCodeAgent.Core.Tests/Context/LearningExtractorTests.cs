using AiCodeAgent.Core.Context;

namespace AiCodeAgent.Core.Tests.Context;

public class LearningExtractorTests
{
    private readonly LearningExtractor _extractor = new();

    [Fact]
    public void Extract_Correction_NoUse()
    {
        var results = _extractor.Extract("s1", "no, don't use semicolons please");
        Assert.Contains(results, l => l.Text.Contains("semicolons"));
    }

    [Fact]
    public void Extract_Correction_Instead()
    {
        var results = _extractor.Extract("s1", "use tabs instead of spaces");
        Assert.Contains(results, l => l.Text.Contains("tabs"));
    }

    [Fact]
    public void Extract_Correction_ActuallyPrefer()
    {
        var results = _extractor.Extract("s1", "actually, prefer explicit types");
        Assert.Contains(results, l => l.Text.Contains("explicit types"));
    }

    [Fact]
    public void Extract_Preference_AlwaysUse()
    {
        var results = _extractor.Extract("s1", "always use var for locals");
        Assert.Contains(results, l => l.Text.Contains("var for locals"));
    }

    [Fact]
    public void Extract_Preference_IPrefer()
    {
        var results = _extractor.Extract("s1", "I prefer functional style");
        Assert.Contains(results, l => l.Text.Contains("functional style", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_Convention_AlwaysNever()
    {
        var results = _extractor.Extract("s1", "always write tests first");
        Assert.Contains(results, l => l.Text.StartsWith("Always"));
    }

    [Fact]
    public void Extract_ReturnsEmptyForEmptyInput()
    {
        Assert.Empty(_extractor.Extract("s1", ""));
        Assert.Empty(_extractor.Extract("s1", "   "));
    }

    [Fact]
    public void Extract_SetsSessionId()
    {
        var results = _extractor.Extract("my-session", "always use var");
        Assert.All(results, l => Assert.Equal("my-session", l.SessionId));
    }

    [Fact]
    public void Extract_SetsCapturedAt()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var results = _extractor.Extract("s1", "always use var");
        var after = DateTime.UtcNow.AddSeconds(1);
        Assert.All(results, l =>
        {
            Assert.InRange(l.CapturedAt, before, after);
        });
    }

    [Fact]
    public void Extract_FiltersShortCandidates()
    {
        // "use a instead" -> candidate "a" is too short (<3 chars after trim)
        var results = _extractor.Extract("s1", "use a instead");
        Assert.DoesNotContain(results, l => l.Text == "Prefer a");
    }
}