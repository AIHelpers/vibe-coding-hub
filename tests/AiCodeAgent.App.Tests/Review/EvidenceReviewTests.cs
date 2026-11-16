using System;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Evidence;
using Xunit;

namespace AiCodeAgent.App.Tests.Review;

/// <summary>E10-S4 on the evidence card: the review button, and what the card shows afterwards.</summary>
public class EvidenceReviewTests
{
    private static EvidencePack Pack(bool withAudit = true, bool withFiles = true) => new()
    {
        RunId = "run-1", Task = "Fix it", Status = "completed",
        AuditPath = withAudit ? "/tmp/run.audit.jsonl" : null,
        Files = withFiles
            ? new[] { new FileEvidence("src/a.cs", false, false, 3, 1, false, false, EvidenceRisk.Low, 1, Array.Empty<string>(), 1, null) }
            : Array.Empty<FileEvidence>()
    };

    private static IndependentReview Review() => new()
    {
        Provider = "openai", Model = "gpt-y", Independence = ReviewIndependence.DifferentProvider, Verdict = ReviewVerdict.Concerns,
        Findings = new[] { new ReviewFinding(FindingSeverity.High, "Off by one", "d", "src/a.cs", 4) }
    };

    [Fact]
    public void TheButtonIsOffered_OnlyWhenAReviewerExists_AndThereIsADiffToRead()
    {
        Func<EvidencePack, CancellationToken, Task<ReviewOutcome>> reviewer = (_, _) => Task.FromResult(ReviewOutcome.Failed("x"));
        Assert.True(new EvidenceItem(Pack(), reviewer).OffersReview);
        Assert.False(new EvidenceItem(Pack(), null).OffersReview);
        Assert.False(new EvidenceItem(Pack(withAudit: false), reviewer).OffersReview);
        Assert.False(new EvidenceItem(Pack(withFiles: false), reviewer).OffersReview);
    }

    [Fact]
    public async Task ASuccessfulReview_RedrawsTheCard_AndTellsTheQueue()
    {
        var pack = Pack();
        var item = new EvidenceItem(pack, (p, _) => { p.Review = Review(); return Task.FromResult(ReviewOutcome.Ok(p.Review)); });
        var raised = 0;
        item.Reviewed += () => raised++;
        Assert.DoesNotContain("**Review:**", item.Summary);
        Assert.Equal("Review with a second model", item.ReviewButtonText);

        await item.ReviewCommand.ExecuteAsync(null);

        Assert.Contains("**Review:**", item.Summary);
        Assert.Contains("### Independent review", item.Markdown);
        Assert.Equal("Review again", item.ReviewButtonText);
        Assert.False(item.IsReviewing);
        Assert.False(item.HasReviewError);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AFailedReview_ShowsWhy_AndLeavesThePackAlone()
    {
        var item = new EvidenceItem(Pack(), (_, _) => Task.FromResult(ReviewOutcome.Failed("No model is available to review with.")));
        var raised = 0;
        item.Reviewed += () => raised++;

        await item.ReviewCommand.ExecuteAsync(null);

        Assert.Equal("No model is available to review with.", item.ReviewError);
        Assert.True(item.HasReviewError);
        Assert.Null(item.Pack.Review);
        Assert.Equal(0, raised);
        Assert.True(item.CanReview);   // it can be tried again
    }

    [Fact]
    public async Task ACancelledReview_SaysSo()
    {
        var item = new EvidenceItem(Pack(), async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ReviewOutcome.Failed("unreachable"); });

        var running = item.ReviewCommand.ExecuteAsync(null);
        Assert.True(item.IsReviewing);
        Assert.False(item.CanReview);          // no second request while one is running
        item.CancelReviewCommand.Execute(null);
        await running;

        Assert.Equal("The review was cancelled.", item.ReviewError);
        Assert.False(item.IsReviewing);
    }
}
