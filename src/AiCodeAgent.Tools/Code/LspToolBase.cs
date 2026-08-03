using AiCodeAgent.Core.Models;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Code;

/// <summary>
/// Base class for LSP-backed tools. Resolves a file path, obtains (or lazily
/// starts) the language client for it, and opens the document so subsequent
/// LSP requests (references/definition/diagnostics) work against live content.
/// </summary>
public abstract class LspToolBase : BaseTool
{
    protected readonly LanguageProviderRegistry Registry;

    protected LspToolBase(ILogger logger, LanguageProviderRegistry registry) : base(logger)
    {
        Registry = registry;
    }

    /// <summary>Resolve path and get the LSP client + URI for a file.</summary>
    protected async Task<(string FilePath, string Uri, LspClient? Client)> PrepareAsync(
        string path,
        AgentExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path, context);
        if (!File.Exists(resolved))
            return (resolved, string.Empty, null);

        var provider = Registry.ResolveProvider(resolved);
        if (provider == null)
            return (resolved, string.Empty, null);

        var client = await Registry.GetOrStartClientForLangAsync(provider.LangId, context.WorkingDirectory, cancellationToken);
        if (client == null)
            return (resolved, string.Empty, null);

        var uri = new Uri(Path.GetFullPath(resolved)).AbsoluteUri;

        // Open the document so the server has full content context
        try
        {
            var text = await File.ReadAllTextAsync(resolved, cancellationToken);
            await client.DidOpenAsync(uri, provider.LangId, 1, text, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "LSP didOpen failed for {File}", resolved);
        }

        return (resolved, uri, client);
    }

    protected static string FormatLocation(Location loc)
    {
        var path = UriToPath(loc.Uri);
        var line = (loc.Range?.Start.Line ?? 0) + 1;
        var col = (loc.Range?.Start.Character ?? 0) + 1;
        return $"{path}:{line}:{col}";
    }

    protected static string UriToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri[7..]);
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
                path = path[1..];
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        return uri;
    }
}