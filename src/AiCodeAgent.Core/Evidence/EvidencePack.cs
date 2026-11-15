using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Evidence;

/// <summary>What a command was for.</summary>
public enum CommandKind { Other, Build, Test, Lint }

/// <summary>How much a human should look at a changed file.</summary>
public enum EvidenceRisk { Low, Medium, High }

/// <summary>One test run seen in the run (a test command, or the test stage of <c>verify_changes</c>).</summary>
/// <param name="Seq">Position in the audit trail; orders runs against file changes.</param>
/// <param name="Runner">dotnet, pytest, jest, vitest, cargo or go; null when the output was not recognised (only pass or fail is then known).</param>
/// <param name="Passed">True when the command exited with 0.</param>
/// <param name="PassedCount">Counts are null when the runner does not print them or the output was cut.</param>
/// <param name="Partial">The tool output was cut in the audit file, so names and counts may be incomplete.</param>
public sealed record TestRunEvidence(
    int Seq, string Command, string? Runner, bool Passed, int? PassedCount, int? FailedCount, int? SkippedCount,
    IReadOnlyList<string> FailedTests, bool Partial, string? Step);

/// <summary>The first and the last test run of the same runner: what was failing before, what is failing now.</summary>
public sealed record TestComparison(
    TestRunEvidence Before, TestRunEvidence After,
    IReadOnlyList<string> Fixed, IReadOnlyList<string> StillFailing, IReadOnlyList<string> NewlyFailing,
    bool NamesKnown);

/// <summary>A build or lint command and whether it passed.</summary>
public sealed record CheckEvidence(int Seq, CommandKind Kind, string Command, bool Passed, string? Step);

/// <param name="Passed">Null when the audit has the call but no result (the run was stopped).</param>
public sealed record CommandEvidence(int Seq, string Command, CommandKind Kind, int? ExitCode, bool? Passed, long DurationMs, string? Step);

/// <param name="Score">Higher is riskier; the reasons say why.</param>
/// <param name="ApproxLines">The diff was cut in the audit file, so the line counts are a lower bound.</param>
public sealed record FileEvidence(
    string Path, bool Created, bool Deleted, int Added, int Removed, bool ApproxLines, bool IsTest,
    EvidenceRisk Risk, int Score, IReadOnlyList<string> Reasons, int Changes, string? Step);

public sealed record ScreenshotEvidence(string Path, string? Url, string? Step);

public sealed record StepEvidence(
    string Id, int Round, string State, string? Outcome, string? Detail, int Tokens, decimal CostUsd, double Seconds, int ToolCalls, int FilesChanged);

public sealed record ApprovalEvidence(int Requested, int Approved, int Denied);

/// <summary>
/// Everything a reviewer needs to judge a finished task or flow without reading the transcript (E10-S1). It is derived from the
/// audit trail, so it cannot disagree with it, and it says plainly what was NOT proven (<see cref="Gaps"/>).
/// </summary>
public sealed class EvidencePack
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string RunId { get; init; } = "";
    /// <summary>chat or flow.</summary>
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Task { get; init; } = "";
    public string? Model { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>The run's end status (completed, failed, cancelled, ...) or "incomplete" when the trail has no end record.</summary>
    public string Status { get; init; } = "incomplete";
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public double? DurationSeconds { get; init; }
    public long Tokens { get; init; }
    public decimal CostUsd { get; init; }
    public string? AuditHeadHash { get; init; }
    /// <summary>Null when the chain was not checked.</summary>
    public bool? AuditIntact { get; init; }
    public IReadOnlyList<TestRunEvidence> TestRuns { get; init; } = Array.Empty<TestRunEvidence>();
    /// <summary>Set when the same runner ran at least twice.</summary>
    public TestComparison? Tests { get; init; }
    public CheckEvidence? Build { get; init; }
    public CheckEvidence? Lint { get; init; }
    public IReadOnlyList<FileEvidence> Files { get; init; } = Array.Empty<FileEvidence>();
    public IReadOnlyList<CommandEvidence> Commands { get; init; } = Array.Empty<CommandEvidence>();
    public IReadOnlyList<ScreenshotEvidence> Screenshots { get; init; } = Array.Empty<ScreenshotEvidence>();
    public IReadOnlyList<StepEvidence> Steps { get; init; } = Array.Empty<StepEvidence>();
    public ApprovalEvidence Approvals { get; init; } = new(0, 0, 0);
    /// <summary>What is not proven or needs a look: no test run, files changed after the last test, a failed last run, a broken trail.</summary>
    /// <summary>The acceptance checklist (E10-S2); null when the agent declared none.</summary>
    public AcceptanceEvidence? Acceptance { get; init; }

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    /// <summary>
    /// False for a run that only read and answered: no file changed, no test, build or lint ran. The app shows a pack
    /// only when there is something to look at, so a plain question does not end with an evidence card.
    /// </summary>
    [JsonIgnore]
    public bool WorthShowing => Files.Count > 0 || TestRuns.Count > 0 || Build != null || Lint != null || Acceptance != null;
}
