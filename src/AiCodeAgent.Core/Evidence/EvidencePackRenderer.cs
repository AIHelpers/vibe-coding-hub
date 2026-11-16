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

        if (p.Acceptance != null)
        {
            sb.AppendLine();
            sb.AppendLine("### Acceptance criteria");
            sb.AppendLine();
            RenderAcceptance(sb, p.Acceptance);
        }

        if (p.Review != null)
        {
            sb.AppendLine();
            sb.AppendLine("### Independent review");
            sb.AppendLine();
            RenderReview(sb, p.Review);
        }

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

    /// <summary>A short card: the verdict-free facts a reviewer wants first (tests, build and lint, riskiest file) and what is not proven.</summary>
    public static string ToSummaryMarkdown(EvidencePack p)
    {
        var sb = new StringBuilder();
        sb.Append($"**Evidence** · {p.Status}");
        if (p.DurationSeconds is { } seconds) sb.Append($" · {Duration(seconds)}");
        sb.AppendLine($" · ${p.CostUsd.ToString("0.00", Inv)}");
        sb.AppendLine();
        sb.AppendLine($"- **Tests:** {TestsLine(p)}");
        if (p.Acceptance != null) sb.AppendLine($"- **Acceptance:** {AcceptanceLine(p.Acceptance)}");
        if (p.Review != null) sb.AppendLine($"- **Review:** {ReviewLine(p.Review)}");
        sb.AppendLine($"- **Build:** {CheckLine(p.Build)} · **Lint:** {CheckLine(p.Lint)}");
        sb.AppendLine($"- **Files:** {FilesLine(p)}");
        if (p.Gaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Not proven**");
            sb.AppendLine();
            foreach (var g in p.Gaps) sb.AppendLine($"- {g}");
        }
        return sb.ToString();
    }

    /// <summary>One line for the card: the verdict, how many findings, and who said it.</summary>
    private static string ReviewLine(IndependentReview r)
    {
        var verdict = r.Verdict switch { ReviewVerdict.Approve => "no blocking findings", ReviewVerdict.Concerns => "concerns", _ => "changes requested" };
        var counts = r.Findings.Count == 0 ? "no findings" : $"{r.Findings.Count} finding(s), {r.HighCount} high";
        var who = r.Independence == ReviewIndependence.SameModel ? $"{Plain(r.ReviewerLabel)}, the same model that wrote it" : Plain(r.ReviewerLabel);
        return $"{verdict} · {counts} · by {who}";
    }

    /// <summary>The review as safe Markdown, for a flow step's output.</summary>
    public static string ReviewMarkdown(IndependentReview review)
    {
        var sb = new StringBuilder();
        RenderReview(sb, review);
        return sb.ToString();
    }

    private static void RenderReview(StringBuilder sb, IndependentReview r)
    {
        sb.AppendLine($"**{ReviewLine(r)}**" + $" · ${r.CostUsd.ToString("0.00", Inv)}");
        sb.AppendLine();
        switch (r.Independence)
        {
            case ReviewIndependence.SameModel:
                sb.AppendLine("> Not independent: the model that wrote the change also reviewed it, so it may share its blind spots. Configure a second provider for a real second opinion.");
                sb.AppendLine();
                break;
            case ReviewIndependence.DifferentModel:
                sb.AppendLine("> Another model of the same provider reviewed this change.");
                sb.AppendLine();
                break;
        }
        sb.AppendLine("This is a second model's opinion, not proof. It never overrules a failing test or an unproven criterion above.");
        if (!string.IsNullOrWhiteSpace(r.Summary)) sb.AppendLine().AppendLine(Plain(r.Summary));
        if (r.DiffTruncated || r.UnreviewedFiles.Count > 0)
        {
            sb.AppendLine().Append("**Not fully reviewed:** the diff was too large for one request.");
            if (r.UnreviewedFiles.Count > 0) sb.Append($" Not shown to the reviewer: {string.Join(", ", r.UnreviewedFiles.Take(MaxNames).Select(f => $"`{Code(f)}`"))}" + (r.UnreviewedFiles.Count > MaxNames ? $" and {r.UnreviewedFiles.Count - MaxNames} more" : "") + ".");
            sb.AppendLine();
        }

        if (r.Findings.Count > 0)
        {
            sb.AppendLine();
            foreach (var f in r.Findings)
            {
                var where = f.File == null ? "" : $" (`{Code(f.File)}`{(f.Line is { } l ? $":{l}" : "")}{(f.Criterion != null ? $", {f.Criterion}" : "")})";
                sb.AppendLine($"- **{f.Severity.ToString().ToLowerInvariant()}** · {Plain(f.Title)}{where}" + (string.IsNullOrWhiteSpace(f.Detail) ? "" : $": {Plain(f.Detail)}"));
            }
        }
        if (r.Criteria.Count > 0)
        {
            sb.AppendLine().AppendLine("Criteria as the reviewer read the diff:").AppendLine();
            foreach (var c in r.Criteria)
                sb.AppendLine($"- {c.Id}: {c.Assessment switch { CriterionAssessment.Satisfied => "satisfied", CriterionAssessment.NotSatisfied => "**not satisfied**", _ => "unclear" }}" + (string.IsNullOrWhiteSpace(c.Note) ? "" : $" ({Plain(c.Note)})"));
        }
    }

    /// <summary>
    /// Text written by a model, made safe to show as Markdown: no links, images, HTML or code fences. A rendered image or link in
    /// a review could otherwise phone home with whatever the model was shown.
    /// </summary>
    internal static string Plain(string s)
    {
        var flat = s.ReplaceLineEndings(" ");
        var sb = new StringBuilder(flat.Length);
        foreach (var ch in flat)
        {
            switch (ch)
            {
                case '[' or ']' or '<' or '>' or '`' or '|' or '*' or '_' or '!' or '\\' or '#':
                    sb.Append('\\').Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string AcceptanceLine(AcceptanceEvidence a)
    {
        var line = $"{a.ProvenCount} of {a.Criteria.Count} proven";
        var open = a.Criteria.Where(c => c.Status != CriterionStatus.Proven).ToList();
        return open.Count == 0 ? line : line + " · open: " + string.Join(", ", open.Select(c => $"{c.Id} {EvidencePackBuilder.Label(c.Status)}"));
    }

    private static string TestsLine(EvidencePack p)
    {
        if (p.TestRuns.Count == 0) return "no test run observed";
        if (p.Tests is { } c)
        {
            var line = $"{Brief(c.Before)} → {Brief(c.After)}";
            if (c.NamesKnown && c.Fixed.Count > 0) line += $" · now passing: {Names(c.Fixed.Take(3).ToList())}" + (c.Fixed.Count > 3 ? $" (+{c.Fixed.Count - 3})" : "");
            if (c.NamesKnown && c.NewlyFailing.Count > 0) line += $" · **newly failing: {Names(c.NewlyFailing.Take(3).ToList())}**" + (c.NewlyFailing.Count > 3 ? $" (+{c.NewlyFailing.Count - 3})" : "");
            return line;
        }
        var last = p.TestRuns[^1];
        return p.TestRuns.Count == 1 ? $"ran once, {Brief(last)} (no earlier run to compare)" : $"{p.TestRuns.Count} runs, last {Brief(last)}";
    }

    private static string Brief(TestRunEvidence r) =>
        r.FailedCount is > 0 ? $"{r.FailedCount} failed"
        : r.PassedCount is { } passed ? $"{passed} passed"
        : r.Passed ? "passed" : "failed";

    private static string CheckLine(CheckEvidence? c) => c == null ? "not run" : c.Passed ? "passed" : "**failed**";

    private static string FilesLine(EvidencePack p)
    {
        if (p.Files.Count == 0) return "none changed";
        var high = p.Files.Count(f => f.Risk == EvidenceRisk.High);
        var medium = p.Files.Count(f => f.Risk == EvidenceRisk.Medium);
        var top = p.Files[0];
        var line = $"{p.Files.Count} changed" + (high + medium > 0 ? $" ({high} high, {medium} medium risk)" : "");
        return line + $"; riskiest `{Code(top.Path)}`" + (top.Reasons.Count > 0 ? $" ({string.Join(", ", top.Reasons)})" : "");
    }

    private static void RenderAcceptance(StringBuilder sb, AcceptanceEvidence a)
    {
        sb.AppendLine($"{a.ProvenCount} of {a.Criteria.Count} proven. \"Proven\" means the audit trail confirms a cited test, file or command; the agent's own mark alone is only a claim.");
        sb.AppendLine();
        sb.AppendLine("| | Criterion | Status | Proof |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in a.Criteria)
        {
            var proof = c.Proofs.Count == 0
                ? (c.Status == CriterionStatus.Unproven ? "never marked" : "none cited")
                : string.Join("<br>", c.Proofs.Select(x => $"{(x.Verified ? "confirmed" : "not confirmed")}: {x.Kind} `{Code(x.Ref)}`. {Cell(x.Detail)}"));
            if (!string.IsNullOrWhiteSpace(c.Note)) proof += $"<br>Agent's note: {Cell(FirstLine(c.Note!, 200))}";
            sb.AppendLine($"| {c.Id} | {Cell(FirstLine(c.Text, 200))} | {StatusText(c.Status)} | {proof} |");
        }
        if (a.DeclaredAfterEdits)
            sb.AppendLine().AppendLine("The criteria were declared after the first edit.");
    }

    private static string StatusText(CriterionStatus s) => s switch
    {
        CriterionStatus.Proven => "**proven**",
        CriterionStatus.Claimed => "claimed, not confirmed",
        CriterionStatus.Contradicted => "**contradicted**",
        CriterionStatus.NotMet => "**not met**",
        _ => "**unproven**"
    };

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
