using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.Core.Tests.Updates;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, false)]
    [InlineData("v1.2.3", 1, 2, 3, false)]
    [InlineData("V1.2.3", 1, 2, 3, false)]
    [InlineData("1.2", 1, 2, 0, false)]
    [InlineData("7", 7, 0, 0, false)]
    [InlineData("1.2.3-rc.1", 1, 2, 3, true)]
    [InlineData("0.0.0-dev.57", 0, 0, 0, true)]
    [InlineData("1.2.3+abc.5", 1, 2, 3, false)]
    [InlineData("1.2.3-beta+exp.sha.5114f85", 1, 2, 3, true)]
    [InlineData("  v2.0.0  ", 2, 0, 0, false)]
    public void Parses(string text, int major, int minor, int patch, bool pre)
    {
        Assert.True(SemanticVersion.TryParse(text, out var v));
        Assert.Equal(major, v!.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("release-1.2")]
    [InlineData("1.2.3-")]
    [InlineData("1..2")]
    [InlineData("1.2.3 beta")]
    [InlineData("99999999999.0.0")]
    public void RejectsWhatIsNotAVersion(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Parse_Throws_ForGarbage()
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("nope"));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2.9", "1.2.10")]
    [InlineData("1.2.3", "1.2.3.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.2.0-rc.1", "1.2.0")]
    [InlineData("0.0.0-dev.5", "0.0.1")]
    public void Older_ComparesBelow(string older, string newer)
    {
        var a = SemanticVersion.Parse(older);
        var b = SemanticVersion.Parse(newer);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a.CompareTo(b) < 0);
        Assert.False(a == b);
    }

    [Fact]
    public void PreReleases_FollowTheSemverOrder()
    {
        // The ordering example of semver.org, section 11.
        var ordered = new[]
        {
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"
        };
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var a = SemanticVersion.Parse(ordered[i]);
            var b = SemanticVersion.Parse(ordered[i + 1]);
            Assert.True(a < b, $"{ordered[i]} should be older than {ordered[i + 1]}");
            Assert.True(b > a);
        }
    }

    [Fact]
    public void BuildMetadata_IsIgnored_AndATagPrefixDoesNotMatter()
    {
        Assert.Equal(SemanticVersion.Parse("1.2.3+aaa"), SemanticVersion.Parse("v1.2.3+bbb"));
        Assert.True(SemanticVersion.Parse("1.2.3") == SemanticVersion.Parse("1.2.3.0"));
        Assert.Equal(SemanticVersion.Parse("1.2.3+aaa").GetHashCode(), SemanticVersion.Parse("1.2.3").GetHashCode());
    }

    [Fact]
    public void ToString_DropsThePrefix_AndKeepsPreReleaseAndBuild()
    {
        Assert.Equal("1.2.3", SemanticVersion.Parse("v1.2.3").ToString());
        Assert.Equal("1.2.0-rc.1", SemanticVersion.Parse("1.2-rc.1").ToString());
        Assert.Equal("1.2.3+abc", SemanticVersion.Parse("1.2.3+abc").ToString());
    }

    [Fact]
    public void HugeNumericPreReleaseParts_DoNotOverflow()
    {
        var a = SemanticVersion.Parse("1.0.0-rc.99999999999999999999");
        var b = SemanticVersion.Parse("1.0.0-rc.100000000000000000000");
        Assert.True(a < b);
    }
}
