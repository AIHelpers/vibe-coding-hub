using System;
using AiCodeAgent.Core.GitHosting;
using Xunit;

namespace AiCodeAgent.Core.Tests.GitHosting;

public class IssueTaskTests
{
    [Theory]
    [InlineData("https://github.com/AIHelpers/vibe-coding-hub.git", "github.com", "AIHelpers", "vibe-coding-hub")]
    [InlineData("https://github.com/AIHelpers/vibe-coding-hub", "github.com", "AIHelpers", "vibe-coding-hub")]
    [InlineData("git@github.com:AIHelpers/vibe-coding-hub.git", "github.com", "AIHelpers", "vibe-coding-hub")]
    [InlineData("ssh://git@github.com/AIHelpers/vibe-coding-hub.git", "github.com", "AIHelpers", "vibe-coding-hub")]
    [InlineData("https://user:pw@GitHub.com/o/r.git", "github.com", "o", "r")]
    [InlineData("https://ghe.example.com/team/app.git", "ghe.example.com", "team", "app")]
    public void RemoteUrl_IsParsed(string url, string host, string owner, string name)
    {
        var repo = GitRemoteUrl.Parse(url);
        Assert.NotNull(repo);
        Assert.Equal(host, repo!.Host);
        Assert.Equal(owner, repo.Owner);
        Assert.Equal(name, repo.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("C:\\work\\repo")]
    public void RemoteUrl_RejectsGarbage(string url) => Assert.Null(GitRemoteUrl.Parse(url));

    private static HostIssue Issue(params HostIssueComment[] comments) => new(
        42, "Crash on save", "Steps:\n1. open\n2. save", "ann", new[] { "bug", "p1" }, "bob",
        "https://github.com/o/r/issues/42", DateTimeOffset.UnixEpoch, comments);

    [Fact]
    public void Task_ContainsTitleBodyLabelsAndComments()
    {
        var text = IssueTaskBuilder.Build(Issue(new HostIssueComment("carol", "Happens on Windows only", DateTimeOffset.UnixEpoch)));

        Assert.StartsWith("Resolve issue #42: Crash on save", text);
        Assert.Contains("https://github.com/o/r/issues/42", text);
        Assert.Contains("Labels: bug, p1", text);
        Assert.Contains("2. save", text);
        Assert.Contains("@carol: Happens on Windows only", text);
    }

    [Fact]
    public void Task_TruncatesLongDiscussions()
    {
        var big = new string('x', IssueTaskBuilder.MaxCommentChars - 10);
        var text = IssueTaskBuilder.Build(Issue(
            new HostIssueComment("a", big, DateTimeOffset.UnixEpoch),
            new HostIssueComment("b", "this one does not fit", DateTimeOffset.UnixEpoch)));

        Assert.Contains("more comments omitted", text);
        Assert.DoesNotContain("does not fit", text);
    }

    [Fact]
    public void Link_RecordsTheIssue()
    {
        var link = IssueTaskBuilder.LinkOf("github", new HostRepo("github.com", "o", "r"), Issue());
        Assert.Equal(("o", "r", 42), (link.Owner, link.Repo, link.Number));
    }
}
