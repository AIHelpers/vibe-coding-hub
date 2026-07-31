using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Web;

public class WebFetchTool : BaseTool
{
    private readonly HttpClient _httpClient;

    // Blocked IP ranges to prevent SSRF attacks
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "0.0.0.0",
        "169.254.169.254", // AWS/GCP/Azure metadata endpoint
        "metadata.google.internal",
        "100.100.100.200", // Alibaba Cloud metadata
    };

    private static readonly string[] BlockedCidrPatterns =
    [
        "^10\\.",       // 10.0.0.0/8
        "^172\\.(1[6-9]|2[0-9]|3[01])\\.", // 172.16.0.0/12
        "^192\\.168\\.", // 192.168.0.0/16
        "^127\\.",       // 127.0.0.0/8
        "^0\\.",         // 0.0.0.0/8
        "^169\\.254\\.", // 169.254.0.0/16
    ];

    public WebFetchTool(ILogger<WebFetchTool> logger, IHttpClientFactory factory)
        : base(logger)
    {
        _httpClient = factory.CreateClient("WebFetch");
    }

    public override string Name => "web_fetch";
    public override string Description =>
        "Fetch content from a URL. Useful for reading documentation, APIs, or web pages.";
    public override RiskLevel Risk => RiskLevel.Read;

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

        // Validate URL to prevent SSRF
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Error($"Invalid URL: {url}");

        // Only allow HTTP and HTTPS schemes
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Error($"URL scheme '{uri.Scheme}' is not allowed. Only http and https are permitted.");

        // Block access to internal/private hosts
        if (IsBlockedHost(uri.Host))
            return Error($"Access to '{uri.Host}' is blocked for security reasons.");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var response = await _httpClient.GetAsync(uri, cts.Token);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (extractText && contentType.Contains("html"))
                content = ExtractTextFromHtml(content);

            if (content.Length > 50000)
                content = content[..50000] + "\n... [Content truncated]";

            return Success($"URL: {url}\nStatus: {response.StatusCode}\n\n{content}");
        }
        catch (Exception ex)
        {
            return Error($"Failed to fetch URL: {ex.Message}");
        }
    }

    private static bool IsBlockedHost(string host)
    {
        // Check exact blocked hosts
        if (BlockedHosts.Contains(host))
            return true;

        // Try to resolve the host to check if it's a private IP
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(host);
            foreach (var address in addresses)
            {
                var ipString = address.ToString();
                if (BlockedHosts.Contains(ipString))
                    return true;

                foreach (var pattern in BlockedCidrPatterns)
                {
                    if (Regex.IsMatch(ipString, pattern))
                        return true;
                }
            }
        }
        catch
        {
            // If DNS resolution fails, block the request to be safe
            return true;
        }

        return false;
    }

    private static string ExtractTextFromHtml(string html)
    {
        // Remove script and style elements
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Remove HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Decode common HTML entities
        html = html.Replace("\u0026nbsp;", " ");
        html = html.Replace("\u0026lt;", "<");
        html = html.Replace("\u0026gt;", ">");
        html = html.Replace("\u0026amp;", "\u0026");
        // Normalize whitespace
        html = Regex.Replace(html, @"\s+", " ");
        return html.Trim();
    }
}