using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.GitHosting;

namespace AiCodeAgent.Tools.GitHosting;

/// <summary>GitHub (and GitHub Enterprise via <c>apiBase</c>) through the REST API.</summary>
public sealed class GitHubHostProvider : IGitHostProvider
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _token;
    private readonly string _apiBase;
    private readonly string _webHost;

    public GitHubHostProvider(HttpClient http, Func<CancellationToken, Task<string?>> token,
        string apiBase = "https://api.github.com", string webHost = "github.com")
    {
        _http = http;
        _token = token;
        _apiBase = apiBase.TrimEnd('/');
        _webHost = webHost.ToLowerInvariant();
    }

    public string Id => "github";
    public string DisplayName => "GitHub";

    public HostRepo? ParseRemote(string remoteUrl)
    {
        var repo = GitRemoteUrl.Parse(remoteUrl);
        return repo != null && repo.Host == _webHost ? repo : null;
    }

    public async Task<IReadOnlyList<HostIssue>> ListIssuesAsync(HostRepo repo, IssueFilter filter, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { "state=open", "sort=updated", "direction=desc", "per_page=100" };
        if (!string.IsNullOrWhiteSpace(filter.Label)) query.Add("labels=" + Uri.EscapeDataString(filter.Label.Trim()));
        var assignee = filter.Assignee?.Trim();
        if (!string.IsNullOrEmpty(assignee))
        {
            if (assignee.Equals("me", StringComparison.OrdinalIgnoreCase)) assignee = await CurrentLoginAsync(cancellationToken).ConfigureAwait(false);
            query.Add("assignee=" + Uri.EscapeDataString(assignee));
        }

        using var doc = await GetAsync($"{RepoUrl(repo)}/issues?{string.Join("&", query)}", cancellationToken).ConfigureAwait(false);
        var issues = new List<HostIssue>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("pull_request", out _)) continue; // the issues endpoint also returns PRs
            issues.Add(ReadIssue(item, Array.Empty<HostIssueComment>()));
            if (issues.Count >= Math.Max(1, filter.Limit)) break;
        }
        return issues;
    }

    public async Task<HostIssue> GetIssueAsync(HostRepo repo, int number, CancellationToken cancellationToken = default)
    {
        using var issueDoc = await GetAsync($"{RepoUrl(repo)}/issues/{number}", cancellationToken).ConfigureAwait(false);
        using var commentsDoc = await GetAsync($"{RepoUrl(repo)}/issues/{number}/comments?per_page=100", cancellationToken).ConfigureAwait(false);
        var comments = commentsDoc.RootElement.EnumerateArray().Select(c => new HostIssueComment(
            LoginOf(c, "user"), Str(c, "body"), Date(c, "created_at"))).ToList();
        return ReadIssue(issueDoc.RootElement, comments);
    }

    private string RepoUrl(HostRepo repo) => $"{_apiBase}/repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

    private async Task<string> CurrentLoginAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{_apiBase}/user", ct).ConfigureAwait(false);
        return Str(doc.RootElement, "login");
    }

    private static HostIssue ReadIssue(JsonElement e, IReadOnlyList<HostIssueComment> comments)
    {
        var labels = e.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : Str(x, "name")).Where(s => s.Length > 0).ToList()
            : new List<string>();
        string? assignee = e.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : null;
        return new HostIssue(
            e.GetProperty("number").GetInt32(), Str(e, "title"), Str(e, "body"), LoginOf(e, "user"),
            labels, assignee, Str(e, "html_url"), Date(e, "updated_at"), comments);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string LoginOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "login") : string.Empty;

    private static DateTimeOffset Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), out var d) ? d : DateTimeOffset.MinValue;

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        var token = await _token(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("vibe-coding-hub", "1.0"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHostException(GitHostError.Network, "Could not reach GitHub: " + ex.Message, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(text);

            var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) ? rem.FirstOrDefault() : null;
            var code = (int)response.StatusCode;
            GitHostError kind;
            if (code == 429 || (code == 403 && remaining == "0")) kind = GitHostError.RateLimited;
            else if (code == 401 || code == 403) kind = GitHostError.Unauthorized;
            else if (code == 404) kind = string.IsNullOrEmpty(token) ? GitHostError.NotSignedIn : GitHostError.NotFound;
            else kind = GitHostError.Other;
            var detail = TryMessage(text);
            throw new GitHostException(kind, kind switch
            {
                GitHostError.Unauthorized => "GitHub rejected the token (" + detail + "). Set a valid GITHUB_TOKEN or run `gh auth login`.",
                GitHostError.NotSignedIn => "GitHub says the repository was not found. For a private repository, set GITHUB_TOKEN or run `gh auth login`.",
                GitHostError.NotFound => "GitHub could not find that repository or issue, or the token has no access to it.",
                GitHostError.RateLimited => "GitHub's rate limit is used up. Sign in with a token for a higher limit, or wait and try again.",
                _ => $"GitHub answered {(int)response.StatusCode}: {detail}"
            });
        }
    }

    private static string TryMessage(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            return Str(d.RootElement, "message");
        }
        catch { return "no details"; }
    }
}
