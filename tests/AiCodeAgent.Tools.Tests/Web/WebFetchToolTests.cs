using AiCodeAgent.Tools.Web;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace AiCodeAgent.Tools.Tests.Web;

public class TestHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> OnSendAsync { get; set; } = 
        (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(OnSendAsync(request, cancellationToken));
    }
}

public class WebFetchToolTests : TestHelpers.TempDirTestBase
{
    private readonly ILogger<WebFetchTool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _httpClient;
    private readonly TestHttpMessageHandler _messageHandler;
    private readonly WebFetchTool _tool;

    public WebFetchToolTests()
    {
        _logger = Substitute.For<ILogger<WebFetchTool>>();
        _messageHandler = new TestHttpMessageHandler();
        _httpClient = new HttpClient(_messageHandler);
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient("WebFetch").Returns(_httpClient);
        _tool = new WebFetchTool(_logger, _httpClientFactory);
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        Assert.Equal("web_fetch", _tool.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.NotEmpty(_tool.Description);
    }

    [Fact]
    public void Definition_HasRequiredParameters()
    {
        var definition = _tool.Definition;
        
        Assert.Equal("web_fetch", definition.Name);
        Assert.NotNull(definition.Parameters);
        Assert.NotNull(definition.Parameters.Properties);
        Assert.Contains("url", definition.Parameters.Properties);
        Assert.Contains("extract_text", definition.Parameters.Properties);
        Assert.Contains("timeout", definition.Parameters.Properties);
    }

    [Fact]
    public void Definition_UrlIsRequired()
    {
        var definition = _tool.Definition;
        
        Assert.Contains("url", definition.Parameters.Required);
    }

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsContent()
    {
        // Arrange
        _messageHandler.OnSendAsync = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello World")
        };

        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Hello World", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_HtmlContent_ExtractsText()
    {
        // Arrange
        var html = "<html><body><p>Hello <strong>World</strong></p></body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        
        _messageHandler.OnSendAsync = (_, _) => response;

        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["extract_text"] = true
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Hello World", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkError_ReturnsError()
    {
        // Arrange
        _messageHandler.OnSendAsync = (_, _) => throw new HttpRequestException("Network error");

        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("Failed to fetch", result.Content);
    }

    [Fact]
    public void ExtractTextFromHtml_RemovesScriptsAndStyles()
    {
        // Arrange
        var html = "<html><head><script>alert('x')</script><style>.foo{}</style></head><body>Text</body></html>";
        
        // Use reflection to call private method
        var method = typeof(WebFetchTool).GetMethod("ExtractTextFromHtml", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method?.Invoke(null, new object?[] { html });
        
        Assert.NotNull(result);
        var resultStr = (string)result;
        Assert.DoesNotContain("script", resultStr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", resultStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Text", resultStr);
    }

    [Fact]
    public void ExtractTextFromHtml_DecodesHtmlEntities()
    {
        // Arrange
        var html = "<div><test></div>";
        
        // Use reflection to call private method
        var method = typeof(WebFetchTool).GetMethod("ExtractTextFromHtml", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var result = method?.Invoke(null, new object?[] { html });
        
        Assert.NotNull(result);
        // < and > should be decoded to < and >
        var resultStr = (string)result;
        Assert.DoesNotContain("<", resultStr);
        Assert.DoesNotContain(">", resultStr);
    }

    [Fact]
    public async Task ExecuteAsync_TruncatesLongContent()
    {
        // Arrange
        var longContent = new string('a', 60000);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(longContent)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        
        _messageHandler.OnSendAsync = (_, _) => response;

        var context = Context();
        var call = Call(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com"
        });

        // Act
        var result = await _tool.ExecuteAsync(call, context);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("[Content truncated]", result.Content);
    }
}