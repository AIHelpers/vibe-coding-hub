using System.Text;
using AiCodeAgent.Core.Evidence;

namespace AiCodeAgent.Core.Flows;

/// <summary>The findings of a review step as plain text: what the step saves and what a loop edge hands back to the author.</summary>
public static class FlowReviewText
{
    public static string ForAgents(IndependentReview review)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Independent review by {review.ReviewerLabel}: {Verdict(review.Verdict)}.");
        if (!review.IsIndependent) sb.AppendLine("(The same model that wrote the change reviewed it, so this is not an independent check.)");
        if (!string.IsNullOrWhiteSpace(review.Summary)) sb.AppendLine().AppendLine(review.Summary.Trim());
        if (review.DiffTruncated || review.UnreviewedFiles.Count > 0)
            sb.AppendLine().AppendLine("Not fully reviewed: the diff was too large" + (review.UnreviewedFiles.Count > 0 ? $"; not shown to the reviewer: {string.Join(", ", review.UnreviewedFiles.Take(15))}." : "."));
        if (review.Findings.Count > 0)
        {
            sb.AppendLine().AppendLine("Findings (the reviewer's opinion, worst first):");
            foreach (var f in review.Findings)
            {
                var where = f.File == null ? "" : $" ({f.File}{(f.Line is { } l ? $":{l}" : "")})";
                sb.AppendLine($"- [{f.Severity.ToString().ToLowerInvariant()}] {f.Title}{where}" + (string.IsNullOrWhiteSpace(f.Detail) ? "" : $": {f.Detail}"));
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Verdict(ReviewVerdict v) => v switch
    {
        ReviewVerdict.Approve => "no blocking findings",
        ReviewVerdict.Concerns => "concerns, not blocking",
        _ => "changes requested"
    };
}
