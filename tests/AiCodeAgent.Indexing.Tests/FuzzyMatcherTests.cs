using AiCodeAgent.Indexing;
using AiCodeAgent.Indexing.Models;

namespace AiCodeAgent.Indexing.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("ChatViewModel.cs", "chat")]
    [InlineData("ChatViewModel.cs", "cvm")]
    [InlineData("UserService.cs", "us")]
    [InlineData("Program.cs", "prog")]
    public void IsMatch_SubsequenceMatches(string text, string query)
    {
        Assert.True(FuzzyMatcher.IsMatch(text, query));
    }

    [Theory]
    [InlineData("ChatViewModel.cs", "xyz")]
    [InlineData("Program.cs", "pogrmx")]
    public void IsMatch_NonMatch_ReturnsFalse(string text, string query)
    {
        Assert.False(FuzzyMatcher.IsMatch(text, query));
    }

    [Fact]
    public void Score_HigherForExactMatch()
    {
        var exact = FuzzyMatcher.Score("ChatViewModel.cs", "ChatViewModel.cs");
        var partial = FuzzyMatcher.Score("ChatViewModel.cs", "cvm");

        Assert.True(exact > partial);
    }

    [Fact]
    public void Score_ReturnsZeroForNoMatch()
    {
        Assert.Equal(0, FuzzyMatcher.Score("ChatViewModel.cs", "zzz"));
    }

    [Fact]
    public void Score_EmptyQuery_HasHighScore()
    {
        var score = FuzzyMatcher.Score("anything.txt", string.Empty);
        Assert.True(score > 0);
    }
}