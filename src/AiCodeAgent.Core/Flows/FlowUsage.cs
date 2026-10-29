using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Flows;

/// <summary>Tokens, cost and time of one run of one step (one loop round counts as one run).</summary>
/// <param name="Round">1 = the first run of the step, 2+ = loop rounds.</param>
/// <param name="Model">The model the step asked for; null = the provider's configured default (its price is unknown, so it is unpriced).</param>
/// <param name="PromptTokens">Input tokens that were not served from the prompt cache.</param>
/// <param name="Unpriced">The model has no known price, so <see cref="CostUsd"/> is 0 and understates the spend.</param>
public sealed record FlowStepRunUsage(
    string NodeId,
    int Round,
    string? Model,
    string? Provider,
    int PromptTokens,
    int CompletionTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    decimal CostUsd,
    bool Unpriced,
    TimeSpan Duration)
{
    /// <summary>All input tokens, cached or not.</summary>
    public int TokensIn => PromptTokens + CacheReadTokens + CacheWriteTokens;
    public int TokensOut => CompletionTokens;
    public int Tokens => TokensIn + TokensOut;
}

/// <summary>A sum of step runs: one node over its rounds, or the whole flow run.</summary>
public sealed record FlowUsageTotals(
    int PromptTokens, int CompletionTokens, int CacheReadTokens, int CacheWriteTokens, decimal CostUsd, bool HasUnpriced)
{
    public static FlowUsageTotals Zero { get; } = new(0, 0, 0, 0, 0m, false);

    public int TokensIn => PromptTokens + CacheReadTokens + CacheWriteTokens;
    public int TokensOut => CompletionTokens;
    public int Tokens => TokensIn + TokensOut;

    public static FlowUsageTotals Sum(IEnumerable<FlowStepRunUsage> runs)
    {
        var t = Zero;
        foreach (var r in runs)
            t = new FlowUsageTotals(t.PromptTokens + r.PromptTokens, t.CompletionTokens + r.CompletionTokens,
                t.CacheReadTokens + r.CacheReadTokens, t.CacheWriteTokens + r.CacheWriteTokens,
                t.CostUsd + r.CostUsd, t.HasUnpriced || r.Unpriced);
        return t;
    }
}

/// <summary>A step run finished: its usage, what its node has used so far, and the run total so far.</summary>
public sealed record FlowUsageEvent(string NodeId, FlowStepRunUsage Step, FlowUsageTotals Node, FlowUsageTotals Run, decimal? BudgetUsd) : AgentEvent;

/// <summary>Text for usage: badges, the table of step runs and the machine-readable JSON.</summary>
public static class FlowUsageFormat
{
    /// <summary>"950", "1.2k", "3.4M".</summary>
    public static string Tokens(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
    };

    /// <summary>"$0.004", "$1.20"; "$0" when nothing was spent.</summary>
    public static string Cost(decimal usd) =>
        usd <= 0 ? "$0" : "$" + usd.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>Node badge: "1.2k tok · $0.004" ("~" before the cost when some model has no known price).</summary>
    public static string Badge(FlowUsageTotals totals) =>
        $"{Tokens(totals.Tokens)} tok · {(totals.HasUnpriced && totals.CostUsd <= 0 ? "unpriced" : Cost(totals.CostUsd))}";

    /// <summary>Header text: "Run: 5.4k tok · $0.021 of $1.00".</summary>
    public static string Header(FlowUsageTotals run, decimal? budgetUsd) =>
        "Run: " + Badge(run) + (budgetUsd is > 0 ? $" of {Cost(budgetUsd.Value)}" : string.Empty);

    /// <summary>Wire name of a status: "succeeded", "failed", "stopped", "cancelled", "budget_exceeded".</summary>
    public static string StatusName(FlowRunStatus status) => status switch
    {
        FlowRunStatus.BudgetExceeded => "budget_exceeded",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>Plain-text table: step, round, model, tokens in/out, cost, duration, and a total row.</summary>
    public static string Table(FlowRunResult result)
    {
        if (result.StepRuns.Count == 0) return string.Empty;
        var rows = result.StepRuns.Select(r => new[]
        {
            r.NodeId, r.Round.ToString(CultureInfo.InvariantCulture), r.Model ?? "(default)",
            Tokens(r.TokensIn), Tokens(r.TokensOut), r.Unpriced ? "unpriced" : Cost(r.CostUsd),
            Seconds(r.Duration)
        }).ToList();
        var total = result.Usage;
        rows.Add(new[]
        {
            "total", "", "", Tokens(total.TokensIn), Tokens(total.TokensOut),
            total.HasUnpriced && total.CostUsd <= 0 ? "unpriced" : Cost(total.CostUsd),
            Seconds(TimeSpan.FromTicks(result.StepRuns.Sum(r => r.Duration.Ticks)))
        });
        var header = new[] { "step", "round", "model", "in", "out", "cost", "time" };
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i is 1 or 3 or 4 or 5 or 6 ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();

        var sb = new StringBuilder();
        sb.AppendLine(Line(header));
        sb.AppendLine(new string('-', widths.Sum() + 2 * (widths.Length - 1)));
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == rows.Count - 1) sb.AppendLine(new string('-', widths.Sum() + 2 * (widths.Length - 1)));
            sb.AppendLine(Line(rows[i]));
        }
        if (total.HasUnpriced)
            sb.AppendLine("Some steps used a model without a known price (cost counted as $0). Add it to ~/.aiagent/pricing.json.");
        return sb.ToString().TrimEnd();
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";

    /// <summary>The result as JSON for scripts: status, message, budget, totals and one entry per step run.</summary>
    public static string ToJson(string flowName, FlowRunResult result)
    {
        var total = result.Usage;
        var payload = new
        {
            flow = flowName,
            status = StatusName(result.Status),
            message = result.Message,
            budgetUsd = result.BudgetUsd,
            totals = new
            {
                tokensIn = total.TokensIn,
                tokensOut = total.TokensOut,
                cacheReadTokens = total.CacheReadTokens,
                cacheWriteTokens = total.CacheWriteTokens,
                costUsd = total.CostUsd,
                hasUnpricedModels = total.HasUnpriced
            },
            steps = result.StepRuns.Select(r => new
            {
                step = r.NodeId,
                round = r.Round,
                model = r.Model,
                provider = r.Provider,
                tokensIn = r.TokensIn,
                tokensOut = r.TokensOut,
                cacheReadTokens = r.CacheReadTokens,
                cacheWriteTokens = r.CacheWriteTokens,
                costUsd = r.CostUsd,
                unpriced = r.Unpriced,
                durationSeconds = Math.Round(r.Duration.TotalSeconds, 2)
            }).ToList(),
            nodes = result.Nodes.Select(n => new { step = n.NodeId, state = n.State.ToString().ToLowerInvariant(), runs = n.Runs, outcome = n.Outcome, detail = n.Detail }).ToList()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
