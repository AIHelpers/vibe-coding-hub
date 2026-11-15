using System.Globalization;
using System.Text;

namespace AiCodeAgent.Core.Evidence;

/// <summary>Renders an <see cref="EvidencePack"/> as Markdown for a pull request body or a review panel.</summary>
public static class EvidencePackRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxFiles = 25;
    private const int MaxCommands = 30;
    private const int MaxNames = 15;

    public static string ToMarkdown(EvidencePack p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Evidence pack");
        sb.AppendLine();
        sb.AppendLine($"**Task:** {Cell(FirstLine(p.Task, 300))}");
        sb.AppendLine();
        sb.AppendLine($"**Run:** `{p.RunId}` · {p.Kind} · {p.Status}" + (p.Model != null ? $" · {p.Model}" : "") +
                      (p.DurationSeconds is { } s ? $" · {Duration(s)}" : "") +
                      $" · {p.Tokens.ToString("N0", Inv)} tokens · ${p.CostUsd.ToString("0.00", Inv)}");
        sb.AppendLine();
        sb.AppendLine(p.AuditIntact switch
        {
            true => $"**Audit trail:** hash chain intact (head `{Short(p.AuditHeadHash)}`).",
            false => "**Audit trail:** FAILED verification; do not rely on this pack.",
            _ => "**Audit trail:** not verified."
        });

        sb.AppendLine();
        sb.AppendLine("### Not proven");
        sb.AppendLine();
        if (p.Gaps.Count == 0) sb.AppendLine("Nothing flagged. This is not a review: it only means no gap was detected in the recorded evidence.");
        else foreach (var g in p.Gaps) sb.AppendLine($"- {g}");

        sb.AppendLine();
        sb.AppendLine("### Tests");
        sb.AppendLine();
        RenderTests(sb, p);

        sb.AppendLine();
        sb.AppendLine("### Build and lint");
        sb.AppendLine();
        sb.AppendLine(p.Build == null ? "- Build: no build command observed." : $"- Build: **{(p.Build.Passed ? "passed" : "failed")}** (`{Code(p.Build.Command)}`)");
        sb.AppendLine(p.Lint == null ? "- Lint: no lint command observed." : $"- Lint: **{(p.Lint.Passed ? "passed" : "failed")}** (`{Code(p.Lint.Command)}`)");

        sb.AppendLine();
        sb.AppendLine("### Changed files, riskiest first");
        sb.AppendLine();
        if (p.Files.Count == 0) sb.AppendLine("No file changes were recorded.");
        else
        {
            sb.AppendLine("| Risk | File | Lines | Why |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var f in p.Files.Take(MaxFiles))
            {
                var lines = $"+{f.Added} −{f.Removed}" + (f.ApproxLines ? "+" : "");
                var why = f.Reasons.Count == 0 ? (f.Created ? "new file" : "") : string.Join(", ", f.Reasons);
                sb.AppendLine($"| {f.Risk.ToString().ToLowerInvariant()} | `{Code(f.Path)}` | {lines} | {Cell(why)} |");
            }
            if (p.Files.Count > MaxFiles) sb.AppendLine().AppendLine($"…and {p.Files.Count - MaxFiles} more file(s) of lower risk.");
        }

        sb.AppendLine();
        sb.AppendLine("### Commands run");
        sb.AppendLine();
        if (p.Commands.Count == 0) sb.AppendLine("No shell commands were run.");
        else
        {
            foreach (var c in p.Commands.Take(MaxCommands))
                sb.AppendLine($"- `{Code(c.Command)}` → {(c.Passed switch { true => "ok", false => c.ExitCode is { } x ? $"exit {x}" : "failed", _ => "no result (run stopped)" })}");
            if (p.Commands.Count > MaxCommands) sb.AppendLine($"- …and {p.Commands.Count - MaxCommands} more.");
        }

        if (p.Screenshots.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Screenshots");
            sb.AppendLine();
            foreach (var shot in p.Screenshots)
                sb.AppendLine($"- `{Code(shot.Path)}`" + (shot.Url != null ? $" ({shot.Url})" : "") + " (local file: attach it to the pull request by hand)");
        }

        if (p.Steps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Flow steps");
            sb.AppendLine();
            sb.AppendLine("| Step | State | Files | Tool calls | Cost | Time |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var st in p.Steps)
                sb.AppendLine($"| {Cell(st.Id)}{(st.Round > 1 ? $" (round {st.Round})" : "")} | {Cell(st.State)}{(string.IsNullOrWhiteSpace(st.Outcome) ? "" : " · " + Cell(st.Outcome!))} | {st.FilesChanged} | {st.ToolCalls} | ${st.CostUsd.ToString("0.00", Inv)} | {Duration(st.Seconds)} |");
        }

        sb.AppendLine();
        sb.AppendLine($"Approvals: {p.Approvals.Requested} requested, {p.Approvals.Approved} approved, {p.Approvals.Denied} denied.");
        return sb.ToString();
    }

    private static void RenderTests(StringBuilder sb, EvidencePack p)
    {
        if (p.TestRuns.Count == 0) { sb.AppendLine("No test run was observed."); return; }
        if (p.Tests is { } c)
        {
            sb.AppendLine($"- Before: {Describe(c.Before)}");
            sb.AppendLine($"- After: {Describe(c.After)}");
            if (c.NamesKnown)
            {
                if (c.Fixed.Count > 0) sb.AppendLine($"- Now passing: {Names(c.Fixed)}");
                if (c.StillFailing.Count > 0) sb.AppendLine($"- Still failing: {Names(c.StillFailing)}");
                if (c.NewlyFailing.Count > 0) sb.AppendLine($"- Newly failing (regressions): {Names(c.NewlyFailing)}");
                if (c.Fixed.Count == 0 && c.StillFailing.Count == 0 && c.NewlyFailing.Count == 0) sb.AppendLine("- No difference in failing tests.");
            }
            else
            {
                sb.AppendLine("- Test names could not be compared (output not recognised or cut).");
            }
            sb.AppendLine($"- {p.TestRuns.Count} test run(s) in total.");
        }
        else
        {
            var only = p.TestRuns[^1];
            sb.AppendLine($"- Ran {(p.TestRuns.Count == 1 ? "once" : p.TestRuns.Count + " times, with different runners")}: {Describe(only)}");
            if (p.TestRuns.Count == 1) sb.AppendLine("- There is no earlier run to compare with, so before and after cannot be shown.");
        }
    }

    private static string Describe(TestRunEvidence r)
    {
        var counts = r.PassedCount != null || r.FailedCount != null
            ? $"{r.PassedCount?.ToString(Inv) ?? "?"} passed, {r.FailedCount?.ToString(Inv) ?? "?"} failed" + (r.SkippedCount is > 0 ? $", {r.SkippedCount} skipped" : "")
            : "counts not available";
        return $"**{(r.Passed ? "passed" : "failed")}**, {counts} (`{Code(r.Command)}`)" + (r.Partial ? ", output was cut" : "");
    }

    private static string Names(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(MaxNames).Select(n => $"`{Code(n)}`")) + (names.Count > MaxNames ? $" …and {names.Count - MaxNames} more" : "");

    private static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds.ToString("0", Inv)} s" : $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s";

    private static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "" : hash.Length <= 12 ? hash : hash[..12];

    private static string FirstLine(string text, int max)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    /// <summary>Keeps a value inside a Markdown table cell or code span.</summary>
    private static string Code(string s) => s.ReplaceLineEndings(" ").Replace("`", "'").Replace("|", "/");
    private static string Cell(string s) => s.ReplaceLineEndings(" ").Replace("|", "/");
}
