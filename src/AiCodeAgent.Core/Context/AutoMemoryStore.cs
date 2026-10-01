using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Context;

/// <summary>
/// File-based implementation of <see cref="IAutoMemory"/>. Persists learned
/// preferences to <c>~/.aiagent/MEMORY.md</c> and loads a bounded slice at
/// session start (first 200 lines or 25KB, whichever comes first).
/// </summary>
public class AutoMemoryStore : IAutoMemory
{
    public const int MaxLines = 200;
    public const int MaxBytes = 25 * 1024; // 25KB

    private readonly ILogger<AutoMemoryStore>? _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<Learning> _learnings = new();
    private readonly HashSet<string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    public AutoMemoryStore(ILogger<AutoMemoryStore>? logger = null, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? GetDefaultFilePath();
    }

    private static string GetDefaultFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = string.IsNullOrEmpty(home) ? ".aiagent" : Path.Combine(home, ".aiagent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "MEMORY.md");
    }

    /// <inheritdoc />
    public string GetFilePath() => _filePath;

    /// <inheritdoc />
    public async Task CaptureAsync(string sessionId, Learning learning, CancellationToken cancellationToken = default)
    {
        if (learning is null || string.IsNullOrWhiteSpace(learning.Text))
            return;

        var fp = Fingerprint(learning.Text);
        lock (_lock)
        {
            if (_fingerprints.Contains(fp))
            {
                _logger?.LogDebug("Skipping duplicate learning: {Text}", learning.Text);
                return;
            }
            _fingerprints.Add(fp);
            _learnings.Add(learning);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Captured learning: {Text}", learning.Text);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# MEMORY.md — Auto-Learned Preferences");
            sb.AppendLine();
            sb.AppendLine("> Automatically captured by AiCodeAgent. Edit freely; redundant entries are ignored.");
            sb.AppendLine();

            List<Learning> snapshot;
            lock (_lock)
            {
                snapshot = _learnings.ToList();
            }

            foreach (var l in snapshot)
            {
                sb.AppendLine(l.ToMarkdownItem());
            }

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_filePath, sb.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save auto-memory to {Path}", _filePath);
        }
    }

    /// <inheritdoc />
    public async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Seed in-memory buffer from disk on first load.
        if (_learnings.Count == 0 && File.Exists(_filePath))
        {
            await SeedFromFileAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(_filePath))
            return string.Empty;

        try
        {
            var content = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return Bound(content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load auto-memory from {Path}", _filePath);
            return string.Empty;
        }
    }

    private async Task SeedFromFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"^\s*-\s*\[(\d{4}-\d{2}-\d{2})\]\s+(.+)$");
                if (!match.Success) continue;
                var text = match.Groups[2].Value.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var fp = Fingerprint(text);
                lock (_lock)
                {
                    if (_fingerprints.Add(fp))
                    {
                        _learnings.Add(new Learning
                        {
                            Text = text,
                            CapturedAt = DateTime.TryParse(match.Groups[1].Value, out var d) ? d : DateTime.UtcNow
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not seed auto-memory from {Path}", _filePath);
        }
    }

    /// <summary>Bound content to the first 200 lines or 25KB, whichever comes first.</summary>
    internal static string Bound(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        // Byte bound
        if (content.Length > MaxBytes)
            content = content[..MaxBytes];

        // Line bound
        var lines = content.Split('\n');
        if (lines.Length > MaxLines)
            content = string.Join('\n', lines.Take(MaxLines));

        return content;
    }

    /// <summary>Produce a normalized fingerprint for deduplication.</summary>
    internal static string Fingerprint(string text)
    {
        var normalized = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
        return normalized;
    }
}