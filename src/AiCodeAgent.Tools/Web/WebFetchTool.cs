using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Web;

public class WebFetchTool : BaseTool
{
    private const int MaxRedirects = 5;
    private const int MaxBytesToRead = 2 * 1024 * 1024; // 2 MB raw bytes, before any text truncation below
    private const int MaxContentChars = 50_000;

    private readonly HttpClient _httpClient;

    public WebFetchTool(ILogger<WebFetchTool> logger, IHttpClientFactory factory)
        : base(logger)
    {
        _httpClient = factory.CreateClient("WebFetch");
    }

    public override string Name => "web_fetch";
    public override string Description =>
        "Fetch content from a URL. Useful for reading documentation, APIs, or web pages.";

    // This tool fetches arbitrary attacker-influenceable URLs and hands their
    // content straight to the model — a prompt-injected page can exfiltrate
    // data via query strings on a subsequent request, so it needs the same
    // approval gate as any other Execute-risk action rather than running
    // silently in every permission mode the way a Read tool does.
    public override RiskLevel Risk => RiskLevel.Execute;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["url"] = new() { Type = "string", Description = "URL to fetch" },
                ["extract_text"] = new() { Type = "boolean", Description = "Extract text content only (default: true)" },
                ["timeout"] = new() { Type = "integer", Description = "Timeout in seconds (default: 15)" }
            },
            Required = ["url"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var url = GetArg<string>(call, "url");
        var extractText = GetArg<bool>(call, "extract_text", true);
        var timeout = GetArg<int>(call, "timeout", 15);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Error($"Invalid URL: {url}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

        try
        {
            // Follow redirects ourselves (the HttpClient is configured with
            // AllowAutoRedirect=false) so every hop's host gets re-validated
            // against SsrfGuard before we ever connect to it — a single
            // upfront check let a redirect to an internal address sail
            // through. SsrfGuard.IsBlockedAddress is also wired into the
            // client's ConnectCallback as a second, connect-time check
            // (defends against DNS rebinding between the check and the
            // actual connection).
            HttpResponseMessage response;
            var currentUri = uri;
            var redirects = 0;
            while (true)
            {
                var validation = await ValidateUriAsync(currentUri, cts.Token);
                if (validation != null)
                    return validation;

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (!IsRedirect(response.StatusCode))
                    break;

                if (++redirects > MaxRedirects)
                {
                    response.Dispose();
                    return Error($"Too many redirects (>{MaxRedirects}) fetching {url}.");
                }

                var location = response.Headers.Location;
                response.Dispose();
                if (location == null)
                    return Error($"Redirect response from {currentUri} had no Location header.");

                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return Error($"Request to {url} failed with status {(int)response.StatusCode} {response.StatusCode}.");

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                // Cap raw bytes read, not just the text we keep afterward —
                // a huge response used to be read to completion (potentially
                // gigabytes) before the 50,000-character truncation below
                // ever kicked in.
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[81920];
                using var ms = new MemoryStream();
                int read;
                var truncated = false;
                while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                {
                    var remaining = MaxBytesToRead - (int)ms.Length;
                    if (remaining <= 0)
                    {
                        truncated = true;
                        break;
                    }
                    await ms.WriteAsync(buffer.AsMemory(0, Math.Min(read, remaining)), cts.Token);
                    if (read > remaining)
                    {
                        truncated = true;
                        break;
                    }
                }

                var content = Encoding.UTF8.GetString(ms.ToArray());
                if (truncated)
                    content += "\n... [truncated after reading the byte cap]";

                if (extractText && contentType.Contains("html"))
                    content = ExtractTextFromHtml(content);

                if (content.Length > MaxContentChars)
                    content = content[..MaxContentChars] + "\n... [Content truncated]";

                return Success($"URL: {currentUri}\nStatus: {response.StatusCode}\n\n{content}");
            }
        }
        catch (Exception ex)
        {
            return Error($"Failed to fetch URL: {ex.Message}");
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private async Task<ToolResult?> ValidateUriAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Error($"URL scheme '{uri.Scheme}' is not allowed. Only http and https are permitted.");

        if (await SsrfGuard.IsBlockedHostAsync(uri.Host, ct))
            return Error($"Access to '{uri.Host}' is blocked for security reasons.");

        return null;
    }

    private static string ExtractTextFromHtml(string html)
    {
        // Remove script and style elements
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Remove HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Decode common HTML entities
        html = html.Replace("&nbsp;", " ");
        html = html.Replace("&lt;", "<");
        html = html.Replace("&gt;", ">");
        html = html.Replace("&amp;", "&");
        // Normalize whitespace
        html = Regex.Replace(html, @"\s+", " ");
        return html.Trim();
    }
}
