using System.Globalization;
using System.Text;

namespace AiCodeAgent.Bench;

/// <summary>Everything the report says about how the numbers were produced.</summary>
internal sealed record ReportContext(
    TaskSuite Suite,
    Options Options,
    IReadOnlyList<BenchConfig> Configs,
    IReadOnlyList<string> AllTaskIds,
    int PlannedAttempts,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string AgentVersion,
    string HarnessDescription);

internal static class ReportBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ CSV

    public const string CsvHeader = "task,config,seed,status,agent_exit,test_exit,tokens,cost_usd,unpriced,agent_seconds,test_seconds,models,started_utc,run_id,note";

    public static string Csv(IEnumerable<Attempt> attempts)
    {
        var sb = new StringBuilder(CsvHeader).Append("\r\n");
        foreach (var a in attempts.OrderBy(a => a.Task, StringComparer.Ordinal).ThenBy(a => a.Config, StringComparer.Ordinal).ThenBy(a => a.Seed))
        {
            sb.Append(string.Join(',', new[]
            {
                Field(a.Task), Field(a.Config), a.Seed.ToString(Inv), a.Status,
                a.AgentExitCode?.ToString(Inv) ?? "", a.TestExitCode?.ToString(Inv) ?? "",
                a.Tokens?.ToString(Inv) ?? "", a.CostUsd?.ToString("0.######", Inv) ?? "", a.Unpriced ? "true" : "false",
                a.AgentSeconds.ToString("0.##", Inv), a.TestSeconds.ToString("0.##", Inv),
                Field(string.Join(" ", a.Models)), a.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv),
                Field(a.RunId ?? ""), Field(a.Note ?? "")
            })).Append("\r\n");
        }
        return sb.ToString();
    }

    internal static string Field(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ------------------------------------------------------------------ Markdown

    public static string Markdown(ReportContext ctx, IReadOnlyList<Attempt> attempts)
    {
        var sb = new StringBuilder();
        var configNames = ctx.Configs.Select(c => c.Name).ToList();
        var summaries = configNames.Select(c => Summarizer.Summarize(c, attempts)).ToList();
        var complete = attempts.Count >= ctx.PlannedAttempts;

        sb.AppendLine("# Benchmark report: single agent vs flows").AppendLine();
        sb.AppendLine($"Suite **{ctx.Suite.Name}**: {ctx.AllTaskIds.Count} task(s), {string.Join(", ", configNames.Select(c => $"`{c}`"))}, {ctx.Options.Seeds} attempt(s) per task and configuration.").AppendLine();
        if (!string.IsNullOrWhiteSpace(ctx.Suite.Description)) sb.AppendLine($"> {ctx.Suite.Description.ReplaceLineEndings(" ")}").AppendLine();
        if (!complete)
            sb.AppendLine($"> **Incomplete run:** {attempts.Count} of {ctx.PlannedAttempts} planned attempts finished. The numbers below cover only those; do not compare them with a complete run.").AppendLine();
        if (ctx.Options.Only.Count > 0)
            sb.AppendLine($"> **Subset:** only the tasks {string.Join(", ", ctx.Options.Only.Select(t => $"`{t}`"))} were run (`--only`).").AppendLine();
        foreach (var s in summaries.Where(x => x.NoUsageRecorded))
            sb.AppendLine($"> **Warning:** no tokens were recorded for `{s.Config}`. Check the provider settings and the attempts' notes in the CSV before reading its cost or pass rate.").AppendLine();

        // ---- methodology
        sb.AppendLine("## Methodology").AppendLine();
        sb.AppendLine($"- **Dates:** started {ctx.StartedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC, finished {ctx.FinishedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
        sb.AppendLine($"- **Model requested for every configuration:** `{ctx.Options.Model}`" + (string.IsNullOrWhiteSpace(ctx.Options.Provider) ? " (provider: the agent's configured one)." : $" (provider `{ctx.Options.Provider}`)."));
        foreach (var s in summaries)
            sb.AppendLine($"- **Models seen in the run reports, `{s.Config}`:** " + (s.Models.Count == 0 ? "none reported (a model the run report does not name is the provider's configured default)" : string.Join(", ", s.Models.Select(m => $"`{m}`"))) + ".");
        var seen = summaries.SelectMany(s => s.Models).Distinct().ToList();
        if (seen.Count > 1 || (seen.Count == 1 && !string.Equals(seen[0], ctx.Options.Model, StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- **Warning:** the models seen differ from the requested one or from each other (a flow step may pin its own model). Configurations that ran on different models are not a like-for-like comparison.");
        sb.AppendLine($"- **Agent:** {ctx.AgentVersion}. **Harness:** {ctx.HarnessDescription}.");
        sb.AppendLine("- **Configurations:** " + string.Join("; ", ctx.Configs.Select(c => c.IsSingle
            ? "`single` = one agent in chat mode (`aiagent run`, tools auto-approved)"
            : $"`{c.Name}` = the `{c.Pipeline}` flow (`aiagent pipeline run`, non-interactive, permission FullAuto)")) + ".");
        sb.AppendLine("- **Tasks:** each is a repository snapshot, the task text and a hidden test command. Both kinds of run get exactly the same text, in a fresh copy of the snapshot (a git repository with the snapshot committed). The test command is never shown to the agent; hidden test patches are applied only after the agent has finished.");
        sb.AppendLine("- **Verdict:** pass means the hidden test command exits with 0 on the workspace the agent left behind. A run that hit the time limit (" + ctx.Options.TimeoutSeconds.ToString(Inv) + " s) is a failure without running the test. Harness or setup errors count as failures and are listed separately.");
        sb.AppendLine($"- **Repetitions:** {ctx.Options.Seeds} per task and configuration, run one at a time in the order seed, task, configuration, so slow periods of the API spread evenly. The seed numbers the repetition (`AIAGENT_BENCH_SEED`); the agent does not promise reproducible sampling, so the repetitions are independent samples, not replays.");
        sb.AppendLine("- **Cost and tokens** are what the agent itself reports (the flow report's totals, or the chat run's audit record), priced with the agent's own price table. Tokens count input (including cached) and output. Time is the wall-clock time of the agent run only; the hidden test is not included." +
                      (summaries.Any(s => s.AnyUnpriced) ? " **Some runs used a model with no known price; their cost is understated.**" : ""));
        sb.AppendLine("- **Intervals:** 95 %. *Attempts* use the Wilson score interval over all attempts. Every other interval is a percentile bootstrap (" + Stats.DefaultIterations.ToString("N0", Inv) + " resamples, fixed seed) over **tasks**, because the attempts of one task are not independent. With few tasks the intervals are wide; that is the honest width.");
        sb.AppendLine("- **No cherry-picking:** every task and every attempt is in the tables below and in the CSV. Nothing was dropped, re-run or filtered after the fact.").AppendLine();

        // ---- results
        sb.AppendLine("## Results").AppendLine();
        sb.AppendLine("| Configuration | Attempts | Passed | Pass rate (attempts, Wilson) | Pass rate (mean over tasks, bootstrap) | Failed | Timeouts | Errors |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in summaries)
            sb.AppendLine($"| `{s.Config}` | {s.Attempts} | {s.Passed} | {Pct(s.PassRate)} [{Pct(s.PassWilson.Lo)}, {Pct(s.PassWilson.Hi)}] | {Pct(s.PassRateByTask)} | {s.Failed} | {s.Timeouts} | {s.Errors} |");
        sb.AppendLine();
        sb.AppendLine("| Configuration | Cost per attempt (USD) | Tokens per attempt | Agent time per attempt (s) | Total cost (USD) | Cost per passed attempt (USD) |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in summaries)
            sb.AppendLine($"| `{s.Config}` | {Num(s.CostUsd, "0.000")} | {Num(s.Tokens, "N0")} | {Num(s.Seconds, "0.0")} | {s.TotalCostUsd.ToString("0.00", Inv)}{(s.AnyUnpriced ? " (understated)" : "")} | {(s.CostPerPassUsd is { } c ? c.ToString("0.000", Inv) : "n/a")} |");
        sb.AppendLine();
        sb.AppendLine("_Means over tasks (each task weighs the same), with the 95 % interval in brackets._").AppendLine();

        // ---- paired comparison
        if (summaries.Count >= 2)
        {
            var baseline = configNames[0];
            sb.AppendLine("## Paired comparison with `" + baseline + "`").AppendLine();
            sb.AppendLine("Difference per task (configuration minus baseline), then averaged; an interval that contains 0 means the data do not separate the two.").AppendLine();
            sb.AppendLine("| Configuration | Δ pass rate (percentage points) | Δ cost per attempt (USD) | Δ agent time (s) |");
            sb.AppendLine("|---|---|---|---|");
            var baseRows = attempts.Where(a => a.Config == baseline).ToList();
            foreach (var other in configNames.Skip(1))
            {
                var rows = attempts.Where(a => a.Config == other).ToList();
                var pass = Stats.PairedDifference(Summarizer.PerTask(baseRows, a => a.Passed ? 100.0 : 0.0), Summarizer.PerTask(rows, a => a.Passed ? 100.0 : 0.0));
                var cost = Stats.PairedDifference(Summarizer.PerTask(baseRows.Where(a => a.CostUsd != null), a => (double)a.CostUsd!.Value), Summarizer.PerTask(rows.Where(a => a.CostUsd != null), a => (double)a.CostUsd!.Value));
                var time = Stats.PairedDifference(Summarizer.PerTask(baseRows.Where(a => a.Status != AttemptStatus.Error), a => a.AgentSeconds), Summarizer.PerTask(rows.Where(a => a.Status != AttemptStatus.Error), a => a.AgentSeconds));
                sb.AppendLine($"| `{other}` | {Signed(pass, "0.0")} | {Signed(cost, "0.000")} | {Signed(time, "0.0")} |");
            }
            sb.AppendLine();
        }

        // ---- every task
        sb.AppendLine("## Every task").AppendLine();
        sb.AppendLine("Cells read: passed/attempts · mean cost per attempt · mean agent time.").AppendLine();
        sb.AppendLine("| Task | Language | " + string.Join(" | ", configNames.Select(c => $"`{c}`")) + " |");
        sb.AppendLine("|---|---|" + string.Join("|", configNames.Select(_ => "---")) + "|");
        foreach (var id in ctx.AllTaskIds)
        {
            var task = ctx.Suite.Tasks.FirstOrDefault(t => t.Id == id);
            var cells = configNames.Select(c => TaskCell(attempts.Where(a => a.Task == id && a.Config == c).ToList(), ctx.Options.Seeds));
            sb.AppendLine($"| `{id}` | {task?.Language ?? ""} | {string.Join(" | ", cells)} |");
        }
        sb.AppendLine();

        // ---- attempts that did not end normally
        var odd = attempts.Where(a => a.Status is AttemptStatus.Error or AttemptStatus.Timeout).OrderBy(a => a.Task, StringComparer.Ordinal).ThenBy(a => a.Config, StringComparer.Ordinal).ThenBy(a => a.Seed).ToList();
        if (odd.Count > 0)
        {
            sb.AppendLine("## Attempts that timed out or errored").AppendLine();
            sb.AppendLine("These count as failures in every rate above.").AppendLine();
            sb.AppendLine("| Task | Configuration | Seed | Status | Note |").AppendLine("|---|---|---|---|---|");
            foreach (var a in odd)
                sb.AppendLine($"| `{a.Task}` | `{a.Config}` | {a.Seed} | {a.Status} | {Cell(a.Note ?? "")} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Reading these numbers").AppendLine();
        sb.AppendLine("- The result describes these tasks, this model and these dates. It says nothing about other languages, other models or later versions of the agent.");
        sb.AppendLine("- A flow costs more tokens by design (several characters read the same code). The question is whether the extra cost buys a higher pass rate; compare the pass-rate difference and the cost difference together.");
        sb.AppendLine("- The raw rows are in `attempts.csv` next to this report.");
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string TaskCell(IReadOnlyList<Attempt> rows, int seeds)
    {
        if (rows.Count == 0) return "not run";
        var passed = rows.Count(a => a.Passed);
        var cost = rows.Where(a => a.CostUsd != null).Select(a => (double)a.CostUsd!.Value).ToList();
        var seconds = rows.Where(a => a.Status != AttemptStatus.Error).Select(a => a.AgentSeconds).ToList();
        var text = $"{passed}/{rows.Count}";
        if (rows.Count < seeds) text += $" (of {seeds})";
        if (cost.Count > 0) text += " · $" + cost.Average().ToString("0.000", Inv);
        if (seconds.Count > 0) text += " · " + seconds.Average().ToString("0", Inv) + " s";
        return text;
    }

    private static string Pct(double v) => double.IsNaN(v) ? "n/a" : (v * 100).ToString("0.0", Inv) + " %";
    private static string Pct(Estimate e) => !e.HasValue ? "n/a" : $"{Pct(e.Mean)} [{Pct(e.Lo)}, {Pct(e.Hi)}]";

    private static string Num(Estimate e, string format) =>
        !e.HasValue ? "n/a" : $"{e.Mean.ToString(format, Inv)} [{e.Lo.ToString(format, Inv)}, {e.Hi.ToString(format, Inv)}]";

    private static string Signed(Estimate e, string format)
    {
        if (!e.HasValue) return "n/a";
        static string S(double v, string f) => (v > 0 ? "+" : "") + v.ToString(f, Inv);
        return $"{S(e.Mean, format)} [{S(e.Lo, format)}, {S(e.Hi, format)}]";
    }

    private static string Cell(string s) => s.ReplaceLineEndings(" ").Replace("|", "/");
}
