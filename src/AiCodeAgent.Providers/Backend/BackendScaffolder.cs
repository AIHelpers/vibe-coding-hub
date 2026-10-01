using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Backend;

/// <summary>Result of a scaffolding operation.</summary>
public sealed class BackendScaffoldResult
{
    public required string TargetPath { get; init; }
    public IReadOnlyList<BackendPrimitive> Primitives { get; init; } = [];
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];
    public IReadOnlyList<string> EnvVars { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Scaffolds backend primitives into a target app: copies template files,
/// replaces placeholder tokens, writes a <c>.env.example</c>, and appends
/// wiring snippets to a <c>backend.ts</c>/<c>backend.js</c> hook file.
/// </summary>
public sealed class BackendScaffolder
{
    private readonly BackendPrimitiveCatalog _catalog;
    private readonly ILogger<BackendScaffolder>? _logger;

    public BackendScaffolder(BackendPrimitiveCatalog catalog, ILogger<BackendScaffolder>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger;
    }

    public async Task<BackendScaffoldResult> ScaffoldAsync(
        IReadOnlyList<string> primitiveIds,
        string targetPath,
        string projectName,
        string? stack = null,
        CancellationToken ct = default)
    {
        if (primitiveIds is null || primitiveIds.Count == 0)
            throw new ArgumentException("At least one primitive id must be specified.", nameof(primitiveIds));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path is required.", nameof(targetPath));
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name is required.", nameof(projectName));

        var primitives = _catalog.Resolve(primitiveIds, stack);
        Directory.CreateDirectory(targetPath);

        var written = new List<string>();
        var warnings = new List<string>();
        var envVars = new List<string>();
        var wiring = new StringBuilder();

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PROJECT_NAME"] = projectName,
            ["PROJECT_SLUG"] = Slugify(projectName)
        };

        foreach (var p in primitives)
        {
            // Collect env vars
            foreach (var v in p.ConfigSchema)
                envVars.Add(v.Name);

            // Write inline template files (preferred, self-contained)
            if (p.InlineTemplates.Count > 0)
            {
                foreach (var kv in p.InlineTemplates)
                {
                    var dest = Path.Combine(targetPath, kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    var content = ReplaceTokens(kv.Value, tokens);
                    await File.WriteAllTextAsync(dest, content, ct);
                    written.Add(kv.Key);
                }
            }

            // Accumulate wiring snippet
            if (!string.IsNullOrWhiteSpace(p.WiringSnippet))
            {
                wiring.AppendLine($"  // {p.Id}");
                wiring.Append("  ");
                wiring.AppendLine(p.WiringSnippet);
            }
        }

        // Write .env.example
        var envPath = Path.Combine(targetPath, ".env.example");
        var envContent = BuildEnvExample(primitives);
        await File.WriteAllTextAsync(envPath, envContent, ct);
        written.Add(".env.example");

        // Append wiring to registerBackend hook file
        var hookPath = Path.Combine(targetPath, "backend.js");
        var hookContent = await EnsureBackendHook(targetPath, hookPath, wiring.ToString(), ct);
        if (hookContent != null)
        {
            await File.WriteAllTextAsync(hookPath, hookContent, ct);
            written.Add("backend.js");
        }

        _logger?.LogInformation("Scaffolded {Count} primitives into {Path}", primitives.Count, targetPath);

        return new BackendScaffoldResult
        {
            TargetPath = targetPath,
            Primitives = primitives,
            WrittenFiles = written,
            EnvVars = envVars,
            Warnings = warnings
        };
    }

    private static string BuildEnvExample(IReadOnlyList<BackendPrimitive> primitives)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Backend primitives - environment configuration");
        sb.AppendLine("# Copy this file to .env and fill in real values.");
        sb.AppendLine();
        foreach (var p in primitives)
        {
            sb.AppendLine($"# --- {p.Name} ({p.Id}) ---");
            foreach (var v in p.ConfigSchema)
            {
                if (!string.IsNullOrEmpty(v.Description))
                    sb.AppendLine($"# {v.Description}");
                if (v.Required)
                    sb.Append($"{v.Name}=");
                else
                    sb.Append($"{v.Name}={v.DefaultValue ?? ""}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string?> EnsureBackendHook(string targetPath, string hookPath, string wiring, CancellationToken ct)
    {
        const string marker = "// --- bundled backend wiring ---";

        string? existing = null;
        if (File.Exists(hookPath))
            existing = await File.ReadAllTextAsync(hookPath, ct);

        if (existing is null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated backend wiring. Call registerBackend(app) from your entry point.");
            sb.AppendLine("function registerBackend(app) {");
            sb.Append(wiring);
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("module.exports = { registerBackend };");
            return sb.ToString();
        }

        if (existing.Contains(marker, StringComparison.Ordinal))
        {
            // Replace the previously generated block
            var start = existing.IndexOf(marker, StringComparison.Ordinal);
            var end = existing.IndexOf("// --- end bundled backend wiring ---", start, StringComparison.Ordinal);
            if (end < 0) end = existing.Length;
            var block = $"{marker}\n{wiring}// --- end bundled backend wiring ---";
            return existing.Remove(start, end - start).Insert(start, block);
        }

        // Append a new block before module.exports
        var insertPos = existing.IndexOf("module.exports", StringComparison.Ordinal);
        if (insertPos < 0) insertPos = existing.Length;
        var addition = $"{marker}\n{wiring}// --- end bundled backend wiring ---\n\n";
        return existing.Insert(insertPos, addition);
    }

    private static string ReplaceTokens(string content, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var kv in tokens)
            content = content.Replace("{{" + kv.Key + "}}", kv.Value);
        return content;
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }
}