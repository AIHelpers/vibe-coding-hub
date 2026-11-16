using System.Text;
using AiCodeAgent.Core.Audit;

namespace AiCodeAgent.Core.Evidence;

/// <summary>What the reviewer is shown: the task, the checklist, the diff and a few facts the pack already proved.</summary>
/// <param name="Diff">Unified diffs, one block per file, riskiest first, within the size budget.</param>
/// <param name="ChangedFiles">Every file the run changed, whether or not its diff fits.</param>
/// <param name="UnreviewedFiles">Changed files left out because the budget ran out.</param>
/// <param name="Truncated">At least one diff was cut.</param>
public sealed record ReviewInput(
    string Task, IReadOnlyList<DeclaredCriterion> Criteria, string Diff, IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> UnreviewedFiles, bool Truncated, IReadOnlyList<string> Facts)
{
    /// <summary>What earlier steps of a flow said (their plans and reports). Data for the reviewer, never instructions.</summary>
    public string? Context { get; init; }

    /// <summary>What the flow author asked this review to look at, from the step's prompt.</summary>
    public string? Focus { get; init; }

    public bool HasChanges => ChangedFiles.Count > 0;
}

/// <summary>Builds <see cref="ReviewInput"/> from a pack and the audit trail it came from. A pure function.</summary>
public static class ReviewInputBuilder
{
    public const int DefaultMaxChars = 60_000;
    public const int MaxCharsPerFile = 14_000;

    public static ReviewInput Build(EvidencePack pack, IReadOnlyList<AuditLine> lines, int maxChars = DefaultMaxChars)
    {
        // One diff per file, in the order the edits happened. The audit file keeps the diff of every edit.
        var perFile = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var cutInAudit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Kind != "file.change") continue;
            var path = line.Str("path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!perFile.TryGetValue(path, out var sb)) perFile[path] = sb = new StringBuilder();
            sb.AppendLine(line.Str("diff") ?? string.Empty);
            if (line.Data["diffTruncated"] is System.Text.Json.Nodes.JsonValue t && t.TryGetValue<bool>(out var cut) && cut) cutInAudit.Add(path);
        }

        // Riskiest files first, so a diff that does not fit loses the files that matter least. Files the pack does not list come last.
        var rank = pack.Files.Select((f, i) => (f.Path, i)).ToDictionary(x => x.Path, x => x.i, StringComparer.Ordinal);
        var ordered = perFile.Keys.OrderBy(p => rank.TryGetValue(p, out var r) ? r : int.MaxValue).ThenBy(p => p, StringComparer.Ordinal).ToList();

        var diff = new StringBuilder();
        var left = new List<string>();
        var truncated = false;
        foreach (var path in ordered)
        {
            var text = perFile[path].ToString().TrimEnd();
            if (text.Length == 0) continue;
            var cut = cutInAudit.Contains(path);
            if (text.Length > MaxCharsPerFile) { text = text[..MaxCharsPerFile]; cut = true; }
            var block = $"=== {path} ===\n{text}\n{(cut ? "[diff cut here]\n" : string.Empty)}\n";
            if (diff.Length + block.Length > maxChars)
            {
                left.Add(path);
                continue;
            }
            diff.Append(block);
            truncated |= cut;
        }

        var criteria = pack.Acceptance?.Criteria.Select(c => new DeclaredCriterion(c.Id, c.Text)).ToList() ?? new List<DeclaredCriterion>();
        return new ReviewInput(pack.Task, criteria, diff.ToString(), ordered, left, truncated || left.Count > 0, Facts(pack));
    }

    /// <summary>
    /// Builds the input for a flow's review step from the diffs the earlier steps produced, in the order they were made. Files are
    /// ranked by the same risk score as the pack, so a diff that does not fit loses the files that matter least.
    /// </summary>
    public static ReviewInput FromChanges(string task, IEnumerable<(string Path, string Diff)> changes, int maxChars = DefaultMaxChars)
    {
        var perFile = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        foreach (var (path, text) in changes)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!perFile.TryGetValue(path, out var sb)) perFile[path] = sb = new StringBuilder();
            sb.AppendLine(text ?? string.Empty);
        }

        var scored = perFile.ToDictionary(kv => kv.Key, kv =>
        {
            var diff = kv.Value.ToString();
            var (added, removed) = FileRiskScorer.CountLines(diff);
            return FileRiskScorer.Score(kv.Key, added, removed, created: false, deleted: false, FileRiskScorer.TouchesPublicApi(kv.Key, diff), FileRiskScorer.IsTestPath(kv.Key)).Score;
        }, StringComparer.Ordinal);
        var ordered = perFile.Keys.OrderByDescending(p => scored[p]).ThenBy(p => p, StringComparer.Ordinal).ToList();

        var diffText = new StringBuilder();
        var left = new List<string>();
        var truncated = false;
        foreach (var path in ordered)
        {
            var text = perFile[path].ToString().TrimEnd();
            if (text.Length == 0) continue;
            var cut = false;
            if (text.Length > MaxCharsPerFile) { text = text[..MaxCharsPerFile]; cut = true; }
            var block = $"=== {path} ===\n{text}\n{(cut ? "[diff cut here]\n" : string.Empty)}\n";
            if (diffText.Length + block.Length > maxChars) { left.Add(path); continue; }
            diffText.Append(block);
            truncated |= cut;
        }
        return new ReviewInput(task, new List<DeclaredCriterion>(), diffText.ToString(), ordered, left, truncated || left.Count > 0, new List<string>());
    }

    /// <summary>Plain facts the pack proved, so the reviewer does not repeat them or contradict them.</summary>
    private static IReadOnlyList<string> Facts(EvidencePack pack)
    {
        var facts = new List<string>();
        if (pack.Tests is { } t)
            facts.Add($"Tests: {t.After.PassedCount?.ToString() ?? "?"} passed, {t.After.FailedCount?.ToString() ?? "?"} failed in the last run.");
        else if (pack.TestRuns.Count > 0)
            facts.Add($"Tests: the last run {(pack.TestRuns[^1].Passed ? "passed" : "failed")}.");
        else
            facts.Add("Tests: none were run.");
        if (pack.Build != null) facts.Add($"Build {(pack.Build.Passed ? "passed" : "failed")}.");
        if (pack.Lint != null) facts.Add($"Lint {(pack.Lint.Passed ? "passed" : "failed")}.");
        foreach (var gap in pack.Gaps.Take(6)) facts.Add($"Not proven: {gap}");
        return facts;
    }
}
