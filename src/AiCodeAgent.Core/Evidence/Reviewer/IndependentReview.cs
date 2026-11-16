using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Evidence;

/// <summary>How serious a reviewer finding is.</summary>
public enum FindingSeverity { Low, Medium, High }

/// <summary>The reviewer's overall judgement of the change.</summary>
public enum ReviewVerdict
{
    /// <summary>Nothing that should stop the change.</summary>
    Approve,
    /// <summary>Worth a look before merging, but nothing that is plainly wrong.</summary>
    Concerns,
    /// <summary>At least one problem that should be fixed first.</summary>
    Reject
}

/// <summary>How the reviewer judged one acceptance criterion from the diff alone.</summary>
public enum CriterionAssessment { Satisfied, NotSatisfied, Unclear }

/// <summary>How independent the reviewer is from the model that wrote the change.</summary>
public enum ReviewIndependence
{
    /// <summary>A different provider: the strongest check.</summary>
    DifferentProvider,
    /// <summary>The same provider but another model.</summary>
    DifferentModel,
    /// <summary>The same model reviewed its own work: better than nothing, but it shares its blind spots.</summary>
    SameModel
}

/// <param name="File">A changed file this finding is about; null when it concerns the change as a whole or the file was not in the diff.</param>
/// <param name="Line">A line in <paramref name="File"/>, when the reviewer named one.</param>
/// <param name="Criterion">The acceptance criterion this finding relates to (AC1, AC2…), when it does.</param>
public sealed record ReviewFinding(FindingSeverity Severity, string Title, string Detail, string? File = null, int? Line = null, string? Criterion = null);

public sealed record CriterionReview(string Id, CriterionAssessment Assessment, string Note);

/// <summary>
/// What a second model said about a change (E10-S4). It is an opinion, kept apart from the facts in the rest of the pack:
/// the pack shows it next to them and never lets it overrule a failing test or an unproven criterion.
/// </summary>
public sealed class IndependentReview
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public ReviewIndependence Independence { get; init; }
    public ReviewVerdict Verdict { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = Array.Empty<ReviewFinding>();
    public IReadOnlyList<CriterionReview> Criteria { get; init; } = Array.Empty<CriterionReview>();
    /// <summary>Files whose changes were left out of the prompt because the diff was too large; the reviewer did not see them.</summary>
    public IReadOnlyList<string> UnreviewedFiles { get; init; } = Array.Empty<string>();
    /// <summary>The diff of at least one file was cut.</summary>
    public bool DiffTruncated { get; init; }
    public DateTimeOffset ReviewedUtc { get; init; }
    public double Seconds { get; init; }
    public long Tokens { get; init; }
    public decimal CostUsd { get; init; }

    [JsonIgnore]
    public int HighCount => Findings.Count(f => f.Severity == FindingSeverity.High);

    [JsonIgnore]
    public bool IsIndependent => Independence != ReviewIndependence.SameModel;

    [JsonIgnore]
    public string ReviewerLabel => string.IsNullOrEmpty(Provider) ? Model : $"{Model} ({Provider})";
}

/// <summary>The result of asking for a review: the review, or the reason there is none.</summary>
public sealed record ReviewOutcome(IndependentReview? Review, string? Error)
{
    public bool Succeeded => Review != null;
    public static ReviewOutcome Ok(IndependentReview review) => new(review, null);
    public static ReviewOutcome Failed(string error) => new(null, error);
}
