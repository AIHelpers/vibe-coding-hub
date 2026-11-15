using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Audit;

namespace AiCodeAgent.Core.Evidence;

/// <summary>Builds the <see cref="EvidencePack"/> of a run from its audit entries. A pure function: the same trail gives the same pack.</summary>
public static class EvidencePackBuilder
{
    private const int MaxCommandChars = 300;
    private const int MaxAddedText = 60_000;
    private static readonly string EmptySha = AuditChain.Sha256(string.Empty);

    private sealed record PendingCall(int Seq, string Tool, string? Agent, JsonNode? Arguments);

    public static EvidencePack Build(IReadOnlyList<AuditLine> lines, AuditVerification? verification = null)
    {
        string runId = "", kind = "chat", name = "", task = "", workDir = "";
        string? model = null;
        DateTimeOffset? started = null, ended = null;
        string status = "incomplete";
        double? seconds = null;
        long tokens = 0;
        decimal cost = 0;

        var pending = new Dictionary<string, PendingCall>(StringComparer.Ordinal);
        var tests = new List<TestRunEvidence>();
        var checks = new List<CheckEvidence>();
        var commands = new List<CommandEvidence>();
        var shots = new List<ScreenshotEvidence>();
        var steps = new List<StepEvidence>();
        var fileAcc = new Dictionary<string, FileAccumulator>(StringComparer.Ordinal);
        int requested = 0, approved = 0, denied = 0;
        var lastFileChangeSeq = 0;
        var firstFileChangeSeq = 0;
        var acceptanceCalls = new List<AcceptanceReplay.Call>();
        decimal lastUsageCost = 0;

        foreach (var line in lines)
        {
            var d = line.Data;
            switch (line.Kind)
            {
                case "run.start":
                    runId = line.Str("runId") ?? "";
                    kind = line.Str("runKind") ?? "chat";
                    name = line.Str("name") ?? "";
                    task = line.Str("task") ?? "";
                    workDir = line.Str("workingDirectory") ?? "";
                    started = ParseTime(line.At);
                    model ??= Model(line);
                    break;

                case "run.end":
                    status = line.Str("status") ?? "completed";
                    ended = ParseTime(line.At);
                    seconds = Long(d, "durationMs") is { } ms ? ms / 1000.0 : null;
                    tokens = Long(d, "tokens") ?? 0;
                    cost = Dec(d, "costUsd") ?? 0;
                    break;

                case "usage":
                    lastUsageCost = Dec(d, "sessionCostUsd") ?? lastUsageCost;
                    model ??= Model(line);
                    break;

                case "step.end":
                    steps.Add(new StepEvidence(
                        line.Str("step") ?? line.Agent ?? "step", (int)(Long(d, "round") ?? 1), line.Str("state") ?? "", line.Str("outcome"), line.Str("detail"),
                        (int)(Long(d, "tokens") ?? 0), Dec(d, "costUsd") ?? 0, (Long(d, "durationMs") ?? 0) / 1000.0,
                        (int)(Long(d, "toolCalls") ?? 0), (int)(Long(d, "filesChanged") ?? 0)));
                    break;

                case "tool.call":
                    if (line.Str("id") is { } callId)
                        pending[callId] = new PendingCall(line.Seq, line.Str("tool") ?? "", line.Agent, d["arguments"]);
                    break;

                case "tool.result":
                    if (line.Str("id") is { } resultId && pending.Remove(resultId, out var call))
                        OnToolResult(line, call, tests, checks, commands, shots, acceptanceCalls);
                    break;

                case "file.change":
                    lastFileChangeSeq = line.Seq;
                    if (firstFileChangeSeq == 0) firstFileChangeSeq = line.Seq;
                    Accumulate(fileAcc, line);
                    break;

                case "approval.request":
                    requested++;
                    break;

                case "approval.decision":
                    if (line.Str("decision") == "approved") approved++;
                    else if (line.Str("decision") == "denied") denied++;
                    break;
            }
        }

        // A call without a result: the run was stopped while it was running. Say so instead of dropping it.
        foreach (var call in pending.Values.Where(c => c.Tool == "execute_command").OrderBy(c => c.Seq))
        {
            var cmd = Truncate(CommandText(call.Arguments));
            commands.Add(new CommandEvidence(call.Seq, cmd, CommandClassifier.Classify(cmd), null, null, 0, call.Agent));
        }
        commands.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        if (status == "incomplete" && cost == 0) cost = lastUsageCost;

        var files = fileAcc.Values.Select(a => a.ToEvidence()).OrderByDescending(f => f.Score)
            .ThenByDescending(f => f.Added + f.Removed).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();

        var comparison = Compare(tests);
        var build = checks.Where(c => c.Kind == CommandKind.Build).OrderBy(c => c.Seq).LastOrDefault();
        var lint = checks.Where(c => c.Kind == CommandKind.Lint).OrderBy(c => c.Seq).LastOrDefault();

        var acceptance = CheckAcceptance(acceptanceCalls, tests, comparison, fileAcc.Values, commands, lastFileChangeSeq, firstFileChangeSeq);
        var gaps = Gaps(status, tests, build, lint, lastFileChangeSeq, files.Count > 0, verification, acceptance);
        return new EvidencePack
        {
            RunId = runId, Kind = kind, Name = name, Task = task, Model = model, WorkingDirectory = workDir.Length == 0 ? null : workDir,
            Status = status, StartedUtc = started, EndedUtc = ended, DurationSeconds = seconds, Tokens = tokens, CostUsd = cost,
            AuditHeadHash = verification?.HeadHash, AuditIntact = verification?.Ok,
            TestRuns = tests.OrderBy(t => t.Seq).ToList(), Tests = comparison, Build = build, Lint = lint,
            Files = files, Commands = commands, Screenshots = shots, Steps = steps,
            Approvals = new ApprovalEvidence(requested, approved, denied), Acceptance = acceptance, Gaps = gaps
        };
    }

    // ------------------------------------------------------------------ tool results

    private static void OnToolResult(AuditLine line, PendingCall call, List<TestRunEvidence> tests, List<CheckEvidence> checks,
        List<CommandEvidence> commands, List<ScreenshotEvidence> shots, List<AcceptanceReplay.Call> acceptanceCalls)
    {
        var output = line.Str("output") ?? "";
        var isError = line.Data["isError"] is JsonValue v && v.TryGetValue<bool>(out var e) && e;
        var duration = Long(line.Data, "durationMs") ?? 0;
        var partial = line.Data["outputTruncated"] is JsonValue t && t.TryGetValue<bool>(out var tr) && tr;

        switch (call.Tool)
        {
            case "execute_command":
            {
                var cmd = Truncate(CommandText(call.Arguments));
                var exit = ExitCode(output);
                var passed = exit is { } x ? x == 0 : !isError;
                var kind = CommandClassifier.Classify(cmd);
                commands.Add(new CommandEvidence(line.Seq, cmd, kind, exit, passed, duration, call.Agent));
                AddOutcome(kind, line.Seq, cmd, passed, output, partial, call.Agent, tests, checks);
                break;
            }
            case "verify_changes":
                foreach (Match m in Regex.Matches(output, @"^(PASS|FAIL)\s+(\S+):\s+(.*?)(?:\s+\((?:timed out after \d+s|exit (-?\d+))\))?\s*$", RegexOptions.Multiline))
                {
                    var passed = m.Groups[1].Value == "PASS";
                    var stage = m.Groups[2].Value;
                    var cmd = Truncate(m.Groups[3].Value);
                    var kind = StageKind(stage, cmd);
                    int? exit = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : passed ? 0 : null;
                    commands.Add(new CommandEvidence(line.Seq, cmd, kind, exit, passed, 0, call.Agent));
                    AddOutcome(kind, line.Seq, cmd, passed, passed ? "" : output, partial, call.Agent, tests, checks);
                }
                break;
            case "acceptance_criteria":
                if (!isError) acceptanceCalls.Add(new AcceptanceReplay.Call(call.Seq, call.Arguments));
                break;
            case "browser_check":
            {
                var m = Regex.Match(output, @"^Screenshot saved:\s*(.+?)\s*$", RegexOptions.Multiline);
                if (m.Success) shots.Add(new ScreenshotEvidence(m.Groups[1].Value, ArgString(call.Arguments, "url"), call.Agent));
                break;
            }
        }
    }

    private static void AddOutcome(CommandKind kind, int seq, string command, bool passed, string output, bool partial, string? step,
        List<TestRunEvidence> tests, List<CheckEvidence> checks)
    {
        switch (kind)
        {
            case CommandKind.Test:
                var parsed = TestOutputParser.Parse(output);
                tests.Add(new TestRunEvidence(seq, command, parsed.Runner, passed, parsed.Passed, parsed.Failed, parsed.Skipped, parsed.FailedTests, partial, step));
                break;
            case CommandKind.Build:
            case CommandKind.Lint:
                checks.Add(new CheckEvidence(seq, kind, command, passed, step));
                break;
        }
    }

    private static CommandKind StageKind(string stage, string command) => stage.ToLowerInvariant() switch
    {
        "build" or "compile" => CommandKind.Build,
        "test" or "tests" => CommandKind.Test,
        "lint" or "format" or "typecheck" => CommandKind.Lint,
        _ => CommandClassifier.Classify(command)
    };

    // ------------------------------------------------------------------ comparison and gaps

    internal static TestComparison? Compare(IReadOnlyList<TestRunEvidence> runs)
    {
        if (runs.Count < 2) return null;
        var ordered = runs.OrderBy(r => r.Seq).ToList();
        var after = ordered[^1];
        var before = ordered.FirstOrDefault(r => r.Seq < after.Seq && r.Runner == after.Runner);
        if (before == null) return null;

        // Names are trustworthy when a run that reports failures also names them and neither output was cut.
        static bool Named(TestRunEvidence r) => (r.FailedCount ?? 0) == 0 || r.FailedTests.Count > 0;
        var namesKnown = after.Runner != null && Named(before) && Named(after) && !before.Partial && !after.Partial;
        var b = before.FailedTests.ToHashSet(StringComparer.Ordinal);
        var a = after.FailedTests.ToHashSet(StringComparer.Ordinal);
        return new TestComparison(before, after,
            b.Except(a).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            b.Intersect(a).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            a.Except(b).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            namesKnown);
    }

    private static IReadOnlyList<string> Gaps(string status, List<TestRunEvidence> tests, CheckEvidence? build, CheckEvidence? lint,
        int lastFileChangeSeq, bool anyFiles, AuditVerification? verification, AcceptanceEvidence? acceptance)
    {
        var gaps = new List<string>();
        if (verification is { Ok: false })
            gaps.Add($"The audit trail failed verification at line {verification.Failure!.Line}; this pack may not match what happened.");
        if (status != "completed")
            gaps.Add(status == "incomplete" ? "The run did not finish: the audit trail has no end record." : $"The run ended with status '{status}'.");

        if (tests.Count == 0)
        {
            gaps.Add("No test run was observed.");
        }
        else
        {
            var last = tests.OrderBy(t => t.Seq).Last();
            if (!last.Passed) gaps.Add("The last test run failed.");
            if (anyFiles && lastFileChangeSeq > last.Seq) gaps.Add("Files were changed after the last test run.");
        }

        if (build != null)
        {
            if (!build.Passed) gaps.Add("The last build failed.");
            if (anyFiles && lastFileChangeSeq > build.Seq) gaps.Add("Files were changed after the last build.");
        }
        if (lint is { Passed: false }) gaps.Add("The last lint run failed.");

        if (acceptance != null)
        {
            var open = acceptance.Criteria.Where(c => c.Status != CriterionStatus.Proven).ToList();
            if (open.Count > 0)
                gaps.Add($"{open.Count} of {acceptance.Criteria.Count} acceptance criteria are not proven: " +
                         string.Join(", ", open.Select(c => $"{c.Id} ({Label(c.Status)})")) + ".");
            if (acceptance.DeclaredAfterEdits)
                gaps.Add("The acceptance criteria were declared after the first edit, so they may have been written to fit the result.");
        }
        return gaps;
    }

    internal static string Label(CriterionStatus s) => s switch
    {
        CriterionStatus.Proven => "proven",
        CriterionStatus.Claimed => "claimed, not confirmed",
        CriterionStatus.Contradicted => "contradicted by a failing test",
        CriterionStatus.NotMet => "not met",
        _ => "unproven"
    };

    private static AcceptanceEvidence? CheckAcceptance(List<AcceptanceReplay.Call> calls, List<TestRunEvidence> tests, TestComparison? comparison,
        IEnumerable<FileAccumulator> files, List<CommandEvidence> commands, int lastFileChangeSeq, int firstFileChangeSeq)
    {
        if (calls.Count == 0) return null;
        var (declared, marks, firstDeclare) = AcceptanceReplay.Replay(calls);
        if (declared.Count == 0) return null;
        var changed = files.Select(a =>
        {
            var e = a.ToEvidence();
            return new AcceptanceChecker.ChangedFile(a.Path, e.IsTest, a.AddedText.ToString(), e.Added, e.Removed);
        }).ToList();
        var late = firstFileChangeSeq > 0 && firstDeclare is { } seq && seq > firstFileChangeSeq;
        return AcceptanceChecker.Check(declared, marks, tests, comparison, changed, commands, lastFileChangeSeq, late);
    }

    // ------------------------------------------------------------------ files

    private sealed class FileAccumulator
    {
        public string Path = "";
        public bool Created, Deleted, Approx, FirstSeen;
        public int Added, Removed, Changes;
        public bool Api;
        public string? Step;
        /// <summary>The added lines of every diff, cut at <see cref="MaxAddedText"/>: where an acceptance proof looks for a test the agent wrote.</summary>
        public readonly System.Text.StringBuilder AddedText = new();

        public FileEvidence ToEvidence()
        {
            var isTest = FileRiskScorer.IsTestPath(Path);
            var (score, risk, reasons) = FileRiskScorer.Score(Path, Added, Removed, Created, Deleted, Api, isTest);
            return new FileEvidence(Path, Created, Deleted, Added, Removed, Approx, isTest, risk, score, reasons, Changes, Step);
        }
    }

    private static void Accumulate(Dictionary<string, FileAccumulator> acc, AuditLine line)
    {
        var path = line.Str("path") ?? "";
        if (path.Length == 0) return;
        if (!acc.TryGetValue(path, out var a)) acc[path] = a = new FileAccumulator { Path = path, Step = line.Agent };
        var created = line.Data["created"] is JsonValue c && c.TryGetValue<bool>(out var cv) && cv;
        if (!a.FirstSeen) { a.Created = created; a.FirstSeen = true; }
        // The last state decides whether the file is gone: it is empty after the change.
        a.Deleted = line.Str("afterSha256") == EmptySha && !created;
        var diff = line.Str("diff") ?? "";
        var (added, removed) = FileRiskScorer.CountLines(diff);
        a.Added += added;
        a.Removed += removed;
        a.Changes++;
        a.Api |= FileRiskScorer.TouchesPublicApi(path, diff);
        foreach (var l in diff.Split('\n'))
        {
            if (a.AddedText.Length >= MaxAddedText) break;
            if (l.Length > 1 && l[0] == '+' && !l.StartsWith("+++", StringComparison.Ordinal)) a.AddedText.AppendLine(l[1..]);
        }
        if (line.Data["diffTruncated"] is JsonValue t && t.TryGetValue<bool>(out var tv) && tv) a.Approx = true;
    }

    // ------------------------------------------------------------------ helpers

    private static string CommandText(JsonNode? arguments) => ArgString(arguments, "command") ?? "";

    private static string? ArgString(JsonNode? arguments, string key)
    {
        try
        {
            if (arguments is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        }
        catch (InvalidOperationException) { }
        return null;
    }

    private static int? ExitCode(string output)
    {
        var m = Regex.Match(output, @"^Exit code:\s*(-?\d+)\s*$", RegexOptions.Multiline);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string Truncate(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= MaxCommandChars ? s : s[..MaxCommandChars] + "…";
    }

    private static string? Model(AuditLine line) => line.Entry["model"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject d, string key)
    {
        if (d[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    private static decimal? Dec(JsonObject d, string key)
    {
        if (d[key] is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var m)) return m;
        if (v.TryGetValue<double>(out var x)) return (decimal)x;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    private static DateTimeOffset? ParseTime(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
