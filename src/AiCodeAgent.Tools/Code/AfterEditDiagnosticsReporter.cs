using System.Text;
using AiCodeAgent.LanguageServices;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Code;

/// <summary>
/// Helper that, after a file edit, opens the document in the live language
/// server, notifies it of the change, waits briefly for diagnostics to be
/// published, and returns a formatted summary of any errors/warnings.
/// Used by EditFileTool / WriteFileTool to surface type errors immediately
/// after edits, satisfying the Code Intelligence feature's after-edit
/// diagnostics requirement.
/// </summary>
public sealed class AfterEditDiagnosticsReporter
{
    private readonly LanguageProviderRegistry _registry;
    private readonly ILogger<AfterEditDiagnosticsReporter> _logger;

    /// <summary>
    /// How long to wait for the language server to publish diagnostics after
    /// a didChange notification. Servers publish asynchronously.
    /// </summary>
    public TimeSpan WaitForDiagnostics { get; set; } = TimeSpan.FromMilliseconds(800);

    public AfterEditDiagnosticsReporter(
        LanguageProviderRegistry registry,
        ILogger<AfterEditDiagnosticsReporter> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Notify the language server of the edited file content and return a
    /// formatted diagnostics summary. Returns an empty string when no language
    /// server is available or no diagnostics are published.
    /// </summary>
    public async Task<string> ReportAsync(
        string filePath,
        string content,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var provider = _registry.ResolveProvider(filePath);
            if (provider == null)
                return string.Empty;

            var client = await _registry.GetOrStartClientForLangAsync(
                provider.LangId, workspaceRoot, cancellationToken);
            if (client == null)
                return string.Empty;

            var uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

            // Open (or update) the document so the server has fresh content.
            // didOpen is idempotent enough for first-edit; subsequent edits use didChange.
            // We try didChange first; if the server rejects because the doc isn't open,
            // fall back to didOpen.
            try
            {
                await client.DidChangeAsync(uri, 1, content, cancellationToken);
            }
            catch
            {
                await client.DidOpenAsync(uri, provider.LangId, 1, content, cancellationToken);
            }

            // Give the server a moment to publish diagnostics.
            try
            {
                await Task.Delay(WaitForDiagnostics, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // Swallow; proceed with whatever diagnostics are available.
            }

            var diagnostics = client.GetDiagnostics(uri);
            if (diagnostics.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[diagnostics] {diagnostics.Count} issue(s) in {filePath}:");

            foreach (var diag in diagnostics.Take(50))
            {
                var line = diag.Range.Start.Line + 1;
                var col = diag.Range.Start.Character + 1;
                sb.AppendLine($"  [{diag.Severity}] {line}:{col} {diag.Message}");
            }

            if (diagnostics.Count > 50)
                sb.AppendLine($"  ... and {diagnostics.Count - 50} more");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "After-edit diagnostics failed for {File}", filePath);
            return string.Empty;
        }
    }
}