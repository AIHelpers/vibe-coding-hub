namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Checks the agent's acceptance marks against what the audit trail recorded (E10-S2). Pure: no I/O, so the rules are
/// unit-testable and the CLI, the app and the PR body always agree.
/// </summary>
public static class AcceptanceChecker
{
    /// <summary>The audit file holds the diff of every edit, so test names the agent added are searched in the added lines.</summary>
    public sealed record ChangedFile(string Path, bool IsTest, string AddedText, int Added, int Removed);

    public static AcceptanceEvidence Check(
        IReadOnlyList<DeclaredCriterion> declared, IReadOnlyDictionary<string, CriterionMark> marks,
        IReadOnlyList<TestRunEvidence> testRuns, TestComparison? comparison, IReadOnlyList<ChangedFile> files,
        IReadOnlyList<CommandEvidence> commands, int lastFileChangeSeq, bool declaredAfterEdits)
    {
        var last = testRuns.Count == 0 ? null : testRuns.OrderBy(r => r.Seq).Last();
        var criteria = new List<CriterionEvidence>(declared.Count);
        foreach (var c in declared)
        {
            if (!marks.TryGetValue(c.Id, out var mark))
            {
                criteria.Add(new CriterionEvidence(c.Id, c.Text, CriterionStatus.Unproven, null, Array.Empty<ProofEvidence>()));
                continue;
            }

            var proofs = new List<ProofEvidence>();
            foreach (var t in mark.Tests) proofs.Add(CheckTest(t, last, comparison, files, lastFileChangeSeq));
            foreach (var f in mark.Files) proofs.Add(CheckFile(f, files));
            foreach (var cmd in mark.Commands) proofs.Add(CheckCommand(cmd, commands, lastFileChangeSeq));

            var status = mark.Status == "not_met" ? CriterionStatus.NotMet
                : proofs.Any(p => p.Contradicted) ? CriterionStatus.Contradicted
                : proofs.Any(p => p.Verified) ? CriterionStatus.Proven
                : CriterionStatus.Claimed;
            criteria.Add(new CriterionEvidence(c.Id, c.Text, status, mark.Note, proofs));
        }
        return new AcceptanceEvidence(criteria, declaredAfterEdits);
    }

    private static ProofEvidence CheckTest(string name, TestRunEvidence? last, TestComparison? comparison, IReadOnlyList<ChangedFile> files, int lastFileChangeSeq)
    {
        if (last == null)
            return new ProofEvidence("test", name, false, "No test run was observed.");

        if (last.FailedTests.Any(f => Matches(f, name)))
            return new ProofEvidence("test", name, false, "Failing in the last test run.", Contradicted: true);

        if (lastFileChangeSeq > last.Seq)
            return new ProofEvidence("test", name, false, "Files were changed after the last test run, so it says nothing about the final code.");

        // A test that failed before and is not failing now is the strongest evidence, and needs the names to be trustworthy.
        if (comparison is { NamesKnown: true } && comparison.Fixed.Any(f => Matches(f, name)))
            return new ProofEvidence("test", name, true, "Failed before, passes in the last test run.");

        if (last.Passed && (last.FailedCount ?? 0) == 0)
        {
            var added = files.FirstOrDefault(f => f.IsTest && f.AddedText.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (added != null)
                return new ProofEvidence("test", name, true, $"Added in `{added.Path}`; the last test run passed.");
            return new ProofEvidence("test", name, false, "The test run passed, but a passing run does not list test names and this test was not added in the diff. Cite the file, the command or a test that was failing before.");
        }

        return new ProofEvidence("test", name, false, "The last test run did not pass, and this test is not named among its failures.");
    }

    private static ProofEvidence CheckFile(string path, IReadOnlyList<ChangedFile> files)
    {
        var wanted = Normalize(path);
        var hit = files.FirstOrDefault(f =>
        {
            var p = Normalize(f.Path);
            return p == wanted || p.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase) || wanted.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase);
        });
        return hit == null
            ? new ProofEvidence("file", path, false, "This file was not changed in the run.")
            : new ProofEvidence("file", path, true, $"Changed in the run (+{hit.Added} −{hit.Removed}).");
    }

    private static ProofEvidence CheckCommand(string fragment, IReadOnlyList<CommandEvidence> commands, int lastFileChangeSeq)
    {
        var ran = commands.Where(c => c.Command.Contains(fragment, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Seq).ToList();
        if (ran.Count == 0) return new ProofEvidence("command", fragment, false, "No such command was run.");
        var latest = ran[^1];
        if (latest.Passed != true) return new ProofEvidence("command", fragment, false, "The last run of this command failed.");
        if (lastFileChangeSeq > latest.Seq) return new ProofEvidence("command", fragment, false, "Files were changed after this command last passed.");
        return new ProofEvidence("command", fragment, true, "Passed after the last file change.");
    }

    private static bool Matches(string testName, string cited) => testName.Contains(cited, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('.', '/');
}
