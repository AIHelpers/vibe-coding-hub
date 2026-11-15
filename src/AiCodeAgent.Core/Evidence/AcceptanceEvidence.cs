using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// What the pack can say about one acceptance criterion (E10-S2). The agent's own mark is a claim, never proof:
/// only <see cref="Proven"/> means the recorded evidence (a test run, a diff, a command) backs it up.
/// </summary>
public enum CriterionStatus
{
    /// <summary>Marked met, and at least one cited proof is confirmed by the audit trail and none is contradicted.</summary>
    Proven,
    /// <summary>Marked met, but nothing the audit trail holds confirms it (no proof cited, proof not found, or proof is stale).</summary>
    Claimed,
    /// <summary>Marked met, but a cited test is failing in the last test run.</summary>
    Contradicted,
    /// <summary>The agent said the criterion is not met.</summary>
    NotMet,
    /// <summary>Declared but never marked.</summary>
    Unproven
}

/// <param name="Kind">test, file or command.</param>
/// <param name="Ref">What the agent cited: a test name (or part of it), a file path, or part of a command line.</param>
/// <param name="Verified">True when the audit trail confirms it.</param>
/// <param name="Detail">Why it was or was not confirmed, in a sentence a reviewer can read.</param>
/// <param name="Contradicted">The evidence says the opposite: the cited test is failing.</param>
public sealed record ProofEvidence(string Kind, string Ref, bool Verified, string Detail, bool Contradicted = false);

/// <param name="Id">AC1, AC2… in declaration order.</param>
/// <param name="Note">The agent's free-text remark with its mark; never counted as proof.</param>
public sealed record CriterionEvidence(string Id, string Text, CriterionStatus Status, string? Note, IReadOnlyList<ProofEvidence> Proofs);

/// <summary>The acceptance checklist of a run, checked against the rest of the pack.</summary>
/// <param name="DeclaredAfterEdits">The first file was edited before the criteria were declared, so they may have been written to fit the result.</param>
public sealed record AcceptanceEvidence(IReadOnlyList<CriterionEvidence> Criteria, bool DeclaredAfterEdits)
{
    [JsonIgnore]
    public int ProvenCount => Criteria.Count(c => c.Status == CriterionStatus.Proven);

    [JsonIgnore]
    public bool AllProven => Criteria.Count > 0 && ProvenCount == Criteria.Count;
}

/// <summary>One criterion as the agent declared it, and the last mark it made on it.</summary>
public sealed record DeclaredCriterion(string Id, string Text);

/// <param name="Status">met or not_met.</param>
public sealed record CriterionMark(string Id, string Status, IReadOnlyList<string> Tests, IReadOnlyList<string> Files, IReadOnlyList<string> Commands, string? Note);
