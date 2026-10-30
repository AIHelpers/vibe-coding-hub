using System.Text.Json;

namespace AiCodeAgent.Bench;

/// <summary>What was read from a finished agent run.</summary>
internal sealed record UsageInfo(long? Tokens, decimal? CostUsd, bool Unpriced, IReadOnlyList<string> Models, string? RunId, string? Status)
{
    public static UsageInfo Unknown { get; } = new(null, null, false, Array.Empty<string>(), null, null);
}

internal static class UsageParsers
{
    /// <summary>
    /// The JSON of <c>aiagent pipeline run --non-interactive --output json</c> (E5-S4): totals.tokensIn/tokensOut/cacheRead/cacheWrite,
    /// totals.costUsd, totals.hasUnpricedModels, steps[].model, runId, status. Progress goes to stderr, so stdout is the JSON, but the
    /// text between the first '{' and the last '}' is taken to be safe against a stray line.
    /// </summary>
    public static UsageInfo FromFlowJson(string stdout, out int? exitCode)
    {
        exitCode = null;
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start) return UsageInfo.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(stdout[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.TryGetProperty("exitCode", out var ec) && ec.TryGetInt32(out var code)) exitCode = code;

            long? tokens = null;
            decimal? cost = null;
            var unpriced = false;
            if (root.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object)
            {
                long sum = 0;
                var any = false;
                foreach (var name in new[] { "tokensIn", "tokensOut" })
                    if (totals.TryGetProperty(name, out var v) && v.TryGetInt64(out var n)) { sum += n; any = true; }
                if (any) tokens = sum;
                if (totals.TryGetProperty("costUsd", out var c) && c.TryGetDecimal(out var usd)) cost = usd;
                if (totals.TryGetProperty("hasUnpricedModels", out var u) && u.ValueKind == JsonValueKind.True) unpriced = true;
            }

            var models = new SortedSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                foreach (var step in steps.EnumerateArray())
                    if (step.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                        models.Add(m.GetString()!);

            var runId = root.TryGetProperty("runId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return new UsageInfo(tokens, cost, unpriced, models.ToList(), runId, status);
        }
        catch (JsonException)
        {
            return UsageInfo.Unknown;
        }
    }

    /// <summary>
    /// The audit file of a chat run (<c>~/.aiagent/runs/*.audit.jsonl</c>, E7-S2): every line is
    /// <c>{"seq","prev","hash","entry":{"kind","model","data":{...}}}</c>; the <c>run.end</c> entry carries tokens, costUsd and status.
    /// </summary>
    public static UsageInfo FromAuditLines(IEnumerable<string> lines, string? runId = null)
    {
        long? tokens = null;
        decimal? cost = null;
        string? status = null;
        var models = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                    models.Add(m.GetString()!);
                if (entry.TryGetProperty("kind", out var k) && k.GetString() == "run.end" &&
                    entry.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("tokens", out var t) && t.TryGetInt64(out var tk)) tokens = tk;
                    if (data.TryGetProperty("costUsd", out var c) && c.TryGetDecimal(out var usd)) cost = usd;
                    if (data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) status = s.GetString();
                }
            }
            catch (JsonException) { }
        }
        return new UsageInfo(tokens, cost, Unpriced: false, models.ToList(), runId, status);
    }
}
