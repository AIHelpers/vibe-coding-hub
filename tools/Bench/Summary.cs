namespace AiCodeAgent.Bench;

internal sealed record ConfigSummary(
    string Config,
    int Tasks,
    int Attempts,
    int Passed,
    int Failed,
    int Timeouts,
    int Errors,
    (double Lo, double Hi) PassWilson,
    Estimate PassRateByTask,
    Estimate CostUsd,
    Estimate Tokens,
    Estimate Seconds,
    decimal TotalCostUsd,
    double? CostPerPassUsd,
    bool AnyUnpriced,
    bool NoUsageRecorded,
    IReadOnlyList<string> Models)
{
    public double PassRate => Attempts == 0 ? double.NaN : (double)Passed / Attempts;
}

internal static class Summarizer
{
    public static ConfigSummary Summarize(string config, IReadOnlyList<Attempt> all)
    {
        var rows = all.Where(a => a.Config == config).ToList();
        var passed = rows.Count(a => a.Status == AttemptStatus.Pass);
        var byTask = rows.GroupBy(a => a.Task).ToList();

        var withCost = rows.Where(a => a.CostUsd != null).ToList();
        decimal total = withCost.Sum(a => a.CostUsd!.Value);

        return new ConfigSummary(
            config,
            byTask.Count,
            rows.Count,
            passed,
            rows.Count(a => a.Status == AttemptStatus.Fail),
            rows.Count(a => a.Status == AttemptStatus.Timeout),
            rows.Count(a => a.Status == AttemptStatus.Error),
            Stats.Wilson(passed, rows.Count),
            Stats.BootstrapMean(PerTask(rows, a => a.Passed ? 1.0 : 0.0).Values.ToList()),
            Stats.BootstrapMean(PerTask(rows.Where(a => a.CostUsd != null), a => (double)a.CostUsd!.Value).Values.ToList()),
            Stats.BootstrapMean(PerTask(rows.Where(a => a.Tokens != null), a => (double)a.Tokens!.Value).Values.ToList()),
            Stats.BootstrapMean(PerTask(rows.Where(a => a.Status != AttemptStatus.Error), a => a.AgentSeconds).Values.ToList()),
            total,
            passed > 0 && withCost.Count > 0 ? (double)total / passed : null,
            rows.Any(a => a.Unpriced),
            rows.Count > 0 && rows.All(a => a.Tokens is null or 0),
            rows.SelectMany(a => a.Models).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList());
    }

    /// <summary>The mean of one figure per task (the unit the intervals resample).</summary>
    public static Dictionary<string, double> PerTask(IEnumerable<Attempt> rows, Func<Attempt, double> figure) =>
        rows.GroupBy(a => a.Task).ToDictionary(g => g.Key, g => g.Average(figure), StringComparer.Ordinal);
}
