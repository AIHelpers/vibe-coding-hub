using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Reads the reviewer's answer. Tolerant of fences and prose around the JSON and of missing fields, strict about what it keeps:
/// a finding needs a title or detail, a file must be one the diff showed, and counts and lengths are capped so a rambling or
/// hostile answer cannot flood the pack.
/// </summary>
public static class ReviewerResponseParser
{
    public const int MaxFindings = 12;
    private const int MaxTitle = 160, MaxDetail = 1200, MaxSummary = 1200, MaxNote = 300;

    /// <returns>Null when no usable JSON object with a verdict could be found.</returns>
    public static ParsedReview? Parse(string? text, IReadOnlyCollection<string> changedFiles, IReadOnlyCollection<string> criterionIds)
    {
        if (JsonObjectIn(text) is not { } root) return null;
        var verdictText = Str(root, "verdict")?.Trim().ToLowerInvariant();
        ReviewVerdict? verdict = verdictText switch
        {
            "approve" or "approved" => ReviewVerdict.Approve,
            "concerns" or "concern" => ReviewVerdict.Concerns,
            "reject" or "rejected" => ReviewVerdict.Reject,
            _ => null
        };
        if (verdict == null) return null;

        var files = new HashSet<string>(changedFiles.Select(Norm), StringComparer.OrdinalIgnoreCase);
        var findings = new List<ReviewFinding>();
        if (root["findings"] is JsonArray array)
        {
            foreach (var node in array.OfType<JsonObject>())
            {
                var title = Cap(Str(node, "title"), MaxTitle);
                var detail = Cap(Str(node, "detail"), MaxDetail);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(detail)) continue;
                var file = Str(node, "file") is { Length: > 0 } f && files.Contains(Norm(f)) ? Display(f, changedFiles) : null;
                var line = file != null && node["line"] is JsonValue lv && lv.TryGetValue<int>(out var n) && n > 0 ? n : (int?)null;
                var criterion = Str(node, "criterion")?.Trim() is { Length: > 0 } c && criterionIds.Contains(c, StringComparer.OrdinalIgnoreCase) ? c.ToUpperInvariant() : null;
                findings.Add(new ReviewFinding(Severity(Str(node, "severity")), string.IsNullOrWhiteSpace(title) ? Cap(detail, 80)! : title!, detail ?? string.Empty, file, line, criterion));
                if (findings.Count == MaxFindings) break;
            }
        }

        // Worst first; the model's own order is kept within a severity.
        findings = findings.Select((f, i) => (f, i)).OrderByDescending(x => x.f.Severity).ThenBy(x => x.i).Select(x => x.f).ToList();

        var criteria = new List<CriterionReview>();
        if (root["criteria"] is JsonArray items)
        {
            foreach (var node in items.OfType<JsonObject>())
            {
                var id = Str(node, "id")?.Trim();
                if (string.IsNullOrEmpty(id) || !criterionIds.Contains(id, StringComparer.OrdinalIgnoreCase) || criteria.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                var assessment = Str(node, "assessment")?.Trim().ToLowerInvariant().Replace(" ", "_") switch
                {
                    "satisfied" or "met" => CriterionAssessment.Satisfied,
                    "not_satisfied" or "not_met" or "unmet" => CriterionAssessment.NotSatisfied,
                    _ => CriterionAssessment.Unclear
                };
                criteria.Add(new CriterionReview(id.ToUpperInvariant(), assessment, Cap(Str(node, "note"), MaxNote) ?? string.Empty));
            }
        }

        // "approve" with a high finding contradicts itself: the finding wins, because it is the part that can be checked.
        var result = verdict.Value;
        if (findings.Any(f => f.Severity == FindingSeverity.High) && result == ReviewVerdict.Approve) result = ReviewVerdict.Concerns;
        return new ParsedReview(result, Cap(Str(root, "summary"), MaxSummary) ?? string.Empty, findings, criteria);
    }

    public sealed record ParsedReview(ReviewVerdict Verdict, string Summary, IReadOnlyList<ReviewFinding> Findings, IReadOnlyList<CriterionReview> Criteria);

    private static JsonObject? JsonObjectIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // The first balanced object, found by scanning: models add fences and a sentence before it.
        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var end = MatchingBrace(text, start);
            if (end < 0) continue;
            try
            {
                if (JsonNode.Parse(text[start..(end + 1)], documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is JsonObject obj) return obj;
            }
            catch (JsonException) { /* try the next brace */ }
        }
        return null;
    }

    private static int MatchingBrace(string s, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static FindingSeverity Severity(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "high" or "critical" or "blocker" or "major" => FindingSeverity.High,
        "low" or "minor" or "nit" or "info" => FindingSeverity.Low,
        _ => FindingSeverity.Medium
    };

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Cap(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static string Norm(string path) => path.Trim().Replace('\\', '/').TrimStart('.', '/');

    private static string Display(string path, IReadOnlyCollection<string> changedFiles) =>
        changedFiles.FirstOrDefault(f => string.Equals(Norm(f), Norm(path), StringComparison.OrdinalIgnoreCase)) ?? path;
}
