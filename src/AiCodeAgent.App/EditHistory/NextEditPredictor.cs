using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.EditHistory;

/// <summary>
/// Produces a <see cref="NextEditPrediction"/> by prompting the AI with the
/// recent edit history and current cursor context. Debouncing/cancellation is
/// owned by the caller (e.g. <see cref="ViewModels.EditorPaneViewModel"/>).
/// </summary>
public sealed class NextEditPredictor
{
    private const string SystemPrompt = """
You are a predictive code-editing assistant. Based on the user's recent edits
and the current file contents at the cursor, predict the SINGLE most likely
next edit the user is about to make.

Respond ONLY with a JSON object matching this schema (no prose, no markdown):
{
  "spans": [
    {
      "startLine": <1-based int>,
      "endLine": <1-based int, equals startLine for inserts>,
      "newText": "<text to insert or replace the range with>",
      "isInsert": <true for insertion, false for replacement>,
      "confidence": <0..1>
    }
  ],
  "confidence": <0..1>
}

Rules:
- Keep predictions minimal and high-confidence.
- Prefer an insertion at or near the cursor line.
- Never repeat text already present verbatim at the target location.
- If no confident prediction exists, return {"spans":[],"confidence":0}.
""";

    private readonly IAiProvider _provider;
    private readonly string _model;

    public NextEditPredictor(IAiProvider provider, string? model = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model ?? provider.SupportedModels.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Produce a prediction for the given cursor position and history.</summary>
    public async Task<NextEditPrediction> PredictAsync(
        string filePath,
        string content,
        int line,
        int column,
        IReadOnlyList<EditRecord> recentEdits,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
            return Empty(filePath);

        var sw = Stopwatch.StartNew();

        var userPrompt = BuildUserPrompt(filePath, content, line, column, recentEdits);
        var request = new CompletionRequest
        {
            SystemPrompt = SystemPrompt,
            Messages = new List<Message>
            {
                new() { Role = MessageRole.User, Content = userPrompt }
            },
            Options = new CompletionOptions
            {
                Temperature = 0.2f,
                MaxTokens = 512,
                Stream = false,
                Model = _model
            }
        };

        var response = await _provider.CompleteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var prediction = Parse(response.Content, filePath);
        sw.Stop();
        return prediction with { LatencyMs = sw.ElapsedMilliseconds };
    }

    private static string BuildUserPrompt(
        string filePath,
        string content,
        int line,
        int column,
        IReadOnlyList<EditRecord> recentEdits)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"File: {filePath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Cursor: line {line}, column {column}");
        sb.AppendLine();

        if (recentEdits is { Count: > 0 })
        {
            sb.AppendLine("Recent edits (oldest first):");
            foreach (var edit in recentEdits)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- {edit.Timestamp:O} {(edit.StartLine?.ToString(CultureInfo.InvariantCulture) ?? "?")}:");
                if (!string.IsNullOrEmpty(edit.Before))
                    sb.AppendLine("  before: " + Truncate(edit.Before, 120));
                if (!string.IsNullOrEmpty(edit.After))
                    sb.AppendLine("  after:  " + Truncate(edit.After, 120));
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Recent edits: (none)");
            sb.AppendLine();
        }

        sb.AppendLine("Current file contents:");
        sb.AppendLine(string.IsNullOrEmpty(content) ? "(empty)" : content);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static NextEditPrediction Parse(string raw, string filePath)
    {
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return Empty(filePath);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var spans = new List<NextEditSpan>();
            if (root.TryGetProperty("spans", out var spansEl) && spansEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in spansEl.EnumerateArray())
                {
                    spans.Add(new NextEditSpan
                    {
                        StartLine = GetInt(item, "startLine", 1),
                        EndLine = GetInt(item, "endLine", GetInt(item, "startLine", 1)),
                        NewText = GetString(item, "newText", string.Empty),
                        IsInsert = GetBool(item, "isInsert", true),
                        Confidence = GetDouble(item, "confidence", 0)
                    });
                }
            }

            return new NextEditPrediction
            {
                FilePath = filePath,
                Spans = spans,
                Confidence = GetDouble(root, "confidence", 0)
            };
        }
        catch (JsonException)
        {
            return Empty(filePath);
        }
    }

    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
            return null;
        return raw.Substring(start, end - start + 1);
    }

    private static NextEditPrediction Empty(string filePath) =>
        new() { FilePath = filePath, Spans = Array.Empty<NextEditSpan>(), Confidence = 0 };

    private static int GetInt(JsonElement el, string name, int dflt) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : dflt;

    private static bool GetBool(JsonElement el, string name, bool dflt) =>
        el.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean() : dflt;

    private static double GetDouble(JsonElement el, string name, double dflt) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble() : dflt;

    private static string GetString(JsonElement el, string name, string dflt) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? dflt : dflt;
}