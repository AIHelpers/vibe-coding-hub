using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.GitHosting;
using AiCodeAgent.Tools.GitHosting;
using Xunit;

namespace AiCodeAgent.Tools.Tests.GitHosting;

/// <summary>E6-S4(a): GitHub provider against recorded response bodies (no network).</summary>
public class GitHubHostProviderTests
{
    private const string IssueList = """
    [
      { "number": 7, "title": "Crash on save", "body": "It crashes", "html_url": "https://github.com/o/r/issues/7",
        "updated_at": "2026-10-20T10:00:00Z", "user": { "login": "ann" }, "assignee": { "login": "bob" },
        "labels": [ { "name": "bug" }, { "name": "p1" } ] },
      { "number": 8, "title": "A pull request", "body": null, "html_url": "https://github.com/o/r/pull/8",
        "updated_at": "2026-10-19T10:00:00Z", "user": { "login": "ann" }, "assignee": null, "labels": [],
        "pull_request": { "url": "https://api.github.com/repos/o/r/pulls/8" } },
      { "number": 9, "title": "No body", "body": null, "html_url": "https://github.com/o/r/issues/9",
        "updated_at": "2026-10-18T10:00:00Z", "user": { "login": "cy" }, "assignee": null, "labels": [] }
    ]
    """;

    private const string Comments = """
    [ { "user": { "login": "carol" }, "body": "Windows only", "created_at": "2026-10-21T09:00:00Z" } ]
    """;

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    private static readonly HostRepo Repo = new("github.com", "o", "r");

    private static GitHubHostProvider Provider(Handler h, string? token = "tok") =>
        new(new HttpClient(h), _ => Task.FromResult(token));

    [Fact]
    public async Task ListIssues_SkipsPullRequests_AndReadsFields()
    {
        var h = new Handler(_ => Json(IssueList));
        var issues = await Provider(h).ListIssuesAsync(Repo, new IssueFilter());

        Assert.Equal(new[] { 7, 9 }, issues.Select(i => i.Number).ToArray());
        var first = issues[0];
        Assert.Equal("Crash on save", first.Title);
        Assert.Equal(new[] { "bug", "p1" }, first.Labels);
        Assert.Equal("bob", first.Assignee);
        Assert.Equal("ann", first.Author);
        Assert.Equal(string.Empty, issues[1].Body);
    }

    [Fact]
    public async Task ListIssues_SendsFiltersAndToken()
    {
        var h = new Handler(_ => Json("[]"));
        await Provider(h).ListIssuesAsync(Repo, new IssueFilter(Label: "good first issue", Assignee: "bob"));

        var request = Assert.Single(h.Requests);
        var url = request.RequestUri!.AbsoluteUri;
        Assert.Contains("/repos/o/r/issues?", url);
        Assert.Contains("state=open", url);
        Assert.Contains("labels=good%20first%20issue", url);
        Assert.Contains("assignee=bob", url);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ListIssues_AssigneeMe_UsesTheSignedInLogin()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath == "/user" ? Json("""{ "login": "dave" }""") : Json("[]"));
        await Provider(h).ListIssuesAsync(Repo, new IssueFilter(Assignee: "me"));

        Assert.Contains("assignee=dave", h.Requests.Last().RequestUri!.Query);
    }

    [Fact]
    public async Task ListIssues_HonoursTheLimit()
    {
        var h = new Handler(_ => Json(IssueList));
        var issues = await Provider(h).ListIssuesAsync(Repo, new IssueFilter(Limit: 1));
        Assert.Single(issues);
    }

    [Fact]
    public async Task GetIssue_IncludesComments()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/comments")
            ? Json(Comments)
            : Json("""
              { "number": 7, "title": "Crash on save", "body": "It crashes", "html_url": "https://github.com/o/r/issues/7",
                "updated_at": "2026-10-20T10:00:00Z", "user": { "login": "ann" }, "assignee": null, "labels": [ { "name": "bug" } ] }
              """));

        var issue = await Provider(h).GetIssueAsync(Repo, 7);

        Assert.Equal("It crashes", issue.Body);
        var c = Assert.Single(issue.Comments);
        Assert.Equal(("carol", "Windows only"), (c.Author, c.Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "tok", GitHostError.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, "tok", GitHostError.NotFound)]
    [InlineData(HttpStatusCode.NotFound, null, GitHostError.NotSignedIn)]
    [InlineData((HttpStatusCode)429, "tok", GitHostError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, "tok", GitHostError.Other)]
    public async Task Errors_AreClassified(HttpStatusCode status, string? token, GitHostError expected)
    {
        var h = new Handler(_ => Json("""{ "message": "nope" }""", status));
        var ex = await Assert.ThrowsAsync<GitHostException>(() => Provider(h, token).ListIssuesAsync(Repo, new IssueFilter()));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Forbidden_WithNoRateLimitLeft_IsRateLimited()
    {
        var h = new Handler(_ =>
        {
            var r = Json("""{ "message": "rate limit" }""", HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            return r;
        });
        var ex = await Assert.ThrowsAsync<GitHostException>(() => Provider(h).ListIssuesAsync(Repo, new IssueFilter()));
        Assert.Equal(GitHostError.RateLimited, ex.Kind);
    }

    [Fact]
    public void ParseRemote_OnlyAcceptsGitHub()
    {
        var p = Provider(new Handler(_ => Json("[]")));
        Assert.NotNull(p.ParseRemote("git@github.com:o/r.git"));
        Assert.Null(p.ParseRemote("https://gitlab.com/o/r.git"));
    }

    [Fact]
    public async Task Token_EnvironmentWinsOverGh()
    {
        var called = false;
        var resolver = new GitHubTokenResolver(n => n == "GH_TOKEN" ? "from-env" : null, _ => { called = true; return Task.FromResult<string?>("from-gh"); });
        var t = await resolver.ResolveAsync();
        Assert.Equal(("from-env", "GH_TOKEN"), (t!.Value, t.Source));
        Assert.False(called);
    }

    [Fact]
    public async Task Token_FallsBackToGh_AndToNull()
    {
        var viaGh = await new GitHubTokenResolver(_ => null, _ => Task.FromResult<string?>("  from-gh\n")).ResolveAsync();
        Assert.Equal(("from-gh", "gh auth token"), (viaGh!.Value, viaGh.Source));

        Assert.Null(await new GitHubTokenResolver(_ => null, _ => Task.FromResult<string?>(null)).ResolveAsync());
        Assert.Null(await new GitHubTokenResolver(_ => null, _ => throw new InvalidOperationException("gh missing")).ResolveAsync());
    }
}
