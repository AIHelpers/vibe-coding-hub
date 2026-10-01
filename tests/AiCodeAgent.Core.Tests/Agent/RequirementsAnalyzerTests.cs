using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class RequirementsAnalyzerTests
{
    private static IAiProvider CreateProvider(string responseContent)
    {
        var provider = Substitute.For<IAiProvider>();
        provider.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompletionResponse { Content = responseContent }));
        return provider;
    }

    private static RequirementsAnalyzer CreateAnalyzer(IAiProvider provider)
        => new(provider, Substitute.For<ILogger<RequirementsAnalyzer>>());

    [Fact]
    public async Task AnalyzeAsync_ReturnsEmpty_WhenPromptIsEmpty()
    {
        var analyzer = CreateAnalyzer(CreateProvider("{}"));
        var gaps = await analyzer.AnalyzeAsync("");
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task AnalyzeAsync_ParsesGaps_FromValidJson()
    {
        var json = """
        {
          "gaps": [
            { "dimension": "platform", "question": "Which platform?" },
            { "dimension": "auth", "question": "What auth strategy?" }
          ]
        }
        """;
        var analyzer = CreateAnalyzer(CreateProvider(json));
        var gaps = await analyzer.AnalyzeAsync("build an app");
        Assert.Equal(2, gaps.Count);
        Assert.Equal("platform", gaps[0].Dimension);
        Assert.Equal("Which platform?", gaps[0].Question);
        Assert.Equal("auth", gaps[1].Dimension);
    }

    [Fact]
    public async Task AnalyzeAsync_ExtractsJson_FromSurroundingProse()
    {
        var content = "Here is the analysis:\n```json\n{\"gaps\":[{\"dimension\":\"styling\",\"question\":\"What styling?\"}]}\n```\nDone.";
        var analyzer = CreateAnalyzer(CreateProvider(content));
        var gaps = await analyzer.AnalyzeAsync("build an app");
        Assert.Single(gaps);
        Assert.Equal("styling", gaps[0].Dimension);
    }

    [Fact]
    public async Task AnalyzeAsync_CapsAtFiveGaps()
    {
        var json = """
        {
          "gaps": [
            { "dimension": "platform", "question": "Q1" },
            { "dimension": "auth", "question": "Q2" },
            { "dimension": "dataModel", "question": "Q3" },
            { "dimension": "styling", "question": "Q4" },
            { "dimension": "integrations", "question": "Q5" },
            { "dimension": "deployment", "question": "Q6" }
          ]
        }
        """;
        var analyzer = CreateAnalyzer(CreateProvider(json));
        var gaps = await analyzer.AnalyzeAsync("build an app");
        Assert.Equal(5, gaps.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsEmpty_WhenNoGapsArray()
    {
        var analyzer = CreateAnalyzer(CreateProvider("{\"result\":\"none\"}"));
        var gaps = await analyzer.AnalyzeAsync("build an app");
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsEmpty_WhenJsonIsInvalid()
    {
        var provider = Substitute.For<IAiProvider>();
        provider.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompletionResponse { Content = "not json at all" }));
        var analyzer = CreateAnalyzer(provider);
        var gaps = await analyzer.AnalyzeAsync("build an app");
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task AnalyzeAsync_PassesCurrentRequirements_ToPrompt()
    {
        var json = "{\"gaps\":[]}";
        CompletionRequest? captured = null;
        var provider = Substitute.For<IAiProvider>();
        provider.CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompletionResponse { Content = json }))
            .AndDoes(ci => captured = ci.Arg<CompletionRequest>());
        var analyzer = CreateAnalyzer(provider);
        var current = new AppRequirements { Platform = "web" };
        await analyzer.AnalyzeAsync("build an app", current);
        Assert.NotNull(captured);
        var userMsg = captured!.Messages.Last(m => m.Role == MessageRole.User);
        Assert.Contains("web", userMsg.Content);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsGapsWithMissingFields()
    {
        var json = """
        {
          "gaps": [
            { "dimension": "platform", "question": "" },
            { "dimension": "", "question": "What?" },
            { "dimension": "auth", "question": "Which auth?" }
          ]
        }
        """;
        var analyzer = CreateAnalyzer(CreateProvider(json));
        var gaps = await analyzer.AnalyzeAsync("build an app");
        Assert.Single(gaps);
        Assert.Equal("auth", gaps[0].Dimension);
    }
}

public class RequirementsClarifierTests
{
    private static IRequirementsAnalyzer CreateAnalyzer(params RequirementGap[][] gapReturns)
    {
        var analyzer = Substitute.For<IRequirementsAnalyzer>();
        var callIndex = 0;
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<AppRequirements?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var idx = System.Math.Min(callIndex, gapReturns.Length - 1);
                callIndex++;
                return Task.FromResult(gapReturns[idx].ToList());
            });
        return analyzer;
    }

    private static RequirementsClarifier CreateClarifier(IRequirementsAnalyzer analyzer)
        => new(analyzer, Substitute.For<ILogger<RequirementsClarifier>>());

    [Fact]
    public async Task RunAsync_ReturnsEmptyRequirements_WhenNoGaps()
    {
        var analyzer = CreateAnalyzer(Array.Empty<RequirementGap>());
        var clarifier = CreateClarifier(analyzer);
        var answers = new List<string>();
        var req = await clarifier.RunAsync("build a web app", (_, __) => { answers.Add("called"); return Task.FromResult<string?>(null); });
        Assert.Empty(answers);
        Assert.False(req.Skipped);
    }

    [Fact]
    public async Task RunAsync_AppliesAnswer_ToCorrectDimension()
    {
        var gapsFirst = new[] { new RequirementGap { Dimension = "platform", Question = "Which platform?" } };
        var analyzer = CreateAnalyzer(gapsFirst, Array.Empty<RequirementGap>());
        var clarifier = CreateClarifier(analyzer);
        var req = await clarifier.RunAsync("build an app", (_, __) => Task.FromResult<string?>("web"));
        Assert.Equal("web", req.Platform);
    }

    [Fact]
    public async Task RunAsync_Skips_WhenUserSaysJustBuildIt()
    {
        var gapsFirst = new[] { new RequirementGap { Dimension = "platform", Question = "Which platform?" } };
        var analyzer = CreateAnalyzer(gapsFirst);
        var clarifier = CreateClarifier(analyzer);
        var req = await clarifier.RunAsync("build an app", (_, __) => Task.FromResult<string?>("just build it"));
        Assert.True(req.Skipped);
    }

    [Fact]
    public async Task RunAsync_StopsAfterFiveQuestions()
    {
        var gap = new[] { new RequirementGap { Dimension = "platform", Question = "Q?" } };
        var analyzer = CreateAnalyzer(gap, gap, gap, gap, gap, gap);
        var clarifier = CreateClarifier(analyzer);
        var count = 0;
        var req = await clarifier.RunAsync("build an app", (_, __) => { count++; return Task.FromResult<string?>("web"); });
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task RunAsync_EmptyAnswer_AdvancesCounter_ButDoesNotSetDimension()
    {
        var gapsFirst = new[] { new RequirementGap { Dimension = "platform", Question = "Which platform?" } };
        var analyzer = CreateAnalyzer(gapsFirst, Array.Empty<RequirementGap>());
        var clarifier = CreateClarifier(analyzer);
        var req = await clarifier.RunAsync("build an app", (_, __) => Task.FromResult<string?>(null));
        Assert.Null(req.Platform);
    }

    [Fact]
    public async Task RunAsync_RespectsCancellation()
    {
        var gap = new[] { new RequirementGap { Dimension = "platform", Question = "Q?" } };
        var analyzer = CreateAnalyzer(gap, gap, gap);
        var clarifier = CreateClarifier(analyzer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var req = await clarifier.RunAsync("build an app", (_, __) => Task.FromResult<string?>("web"), cts.Token);
        Assert.Null(req.Platform);
    }
}