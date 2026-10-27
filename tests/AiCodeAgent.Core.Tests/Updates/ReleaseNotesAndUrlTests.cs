using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.Core.Tests.Updates;

public class ReleaseNotesFormatterTests
{
    [Fact]
    public void Empty_GivesEmpty()
    {
        Assert.Equal(string.Empty, ReleaseNotesFormatter.Format(null));
        Assert.Equal(string.Empty, ReleaseNotesFormatter.Format("  \n "));
    }

    [Fact]
    public void MarkdownBecomesPlainText()
    {
        var text = ReleaseNotesFormatter.Format(
            "## What's new\r\n<!-- internal -->\r\n- **Faster** startup\r\n* See [the docs](https://example.com/docs)\r\n\r\n\r\n\r\n### Fixes\r\n- A crash");

        Assert.Equal("What's new\n\n• Faster startup\n• See the docs\n\nFixes\n• A crash", text);
    }

    [Fact]
    public void LongNotes_AreCutAtALineEnd_WithAnEllipsis()
    {
        var notes = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- change number {i}"));

        var text = ReleaseNotesFormatter.Format(notes, 100);

        Assert.True(text.Length <= 102, text.Length.ToString());
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("change number 200", text);
        Assert.StartsWith("• change number 1", text);
    }
}

public class UpdateUrlGuardTests
{
    [Theory]
    [InlineData("https://github.com/AIHelpers/vibe-coding-hub/releases/tag/v1.3.0", true)]
    [InlineData("https://github.com/AIHelpers/vibe-coding-hub/releases/download/v1.3.0/app.zip", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://evil.example/github.com", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://notgithub.com/x", false)]
    [InlineData("https://user:pw@github.com/x", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("calc.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpsGitHubLinks(string? url, bool expected)
    {
        Assert.Equal(expected, UpdateUrlGuard.IsSafe(url));
    }
}
