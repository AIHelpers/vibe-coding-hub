using System.Runtime.CompilerServices;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E5-S8: the Markdown report, the Mermaid diagram with run status, and the loop labels.</summary>
public sealed class FlowReportBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    /// <summary>build → review; review → ship on "approved"; review → build on "rejected" (2 loops); ship → announce.</summary>
    private static SdlcPipelineDefinition Definition() => new()
    {
        Name = "code-review",
        Stages = new() { Stage("build"), Stage("review", "reviewer", null, "approved", "rejected"), Stage("ship"), Stage("announce") },
        Edges = new()
        {
            Edge("build", "review"),
            Edge("review", "ship", "approved"),
            Edge("review", "build", "rejected", 2),
            Edge("ship", "announce")
        }
    };

    private static FlowStepRecord Step(string node, int round, string? character, int prompt, int completion, decimal cost, int seconds,
        string? outcome = null, string? outputFile = null, params FlowDiffRecord[] diffs) => new()
    {
        NodeId = node,
        Round = round,
        CharacterId = character,
        Model = "gpt-4o",
        Provider = "openai",
        State = FlowNodeState.Done,
        Outcome = outcome,
        OutputFile = outputFile,
        Duration = TimeSpan.FromSeconds(seconds),
        Usage = new FlowStepRunUsage(node, round, "gpt-4o", "openai", prompt, completion, 0, 0, cost, false, TimeSpan.FromSeconds(seconds)),
        Diffs = diffs.ToList()
    };

    /// <summary>build, review (rejected), build again, review (approved), then ship fails; announce never runs.</summary>
    internal static FlowRunRecord FailedRun() => new()
    {
        Id = "20261029-100000-abc123",
        FlowName = "code-review",
        Task = "Add pagination to the orders API.\nKeep the page size configurable.",
        StartedAt = T0,
        FinishedAt = T0.AddSeconds(95),
        Status = FlowRunStatus.Failed,
        Message = "Step 'ship' failed: step limit reached.",
        BudgetUsd = 1.00m,
        CostUsd = 0.0423m,
        Tokens = 8700,
        Definition = Definition(),
        Steps = new()
        {
            Step("build", 1, "bob", 1000, 500, 0.0100m, 10, null, "build-1.md", new FlowDiffRecord("src/Orders.cs", "+a")),
            Step("review", 1, "rae", 2000, 300, 0.0123m, 12, "rejected", "review-1.md"),
            Step("build", 2, "bob", 1500, 700, 0.0150m, 20, null, "build-2.md",
                new FlowDiffRecord("src/Orders.cs", "+b"), new FlowDiffRecord("src/OrdersTests.cs", "+c")),
            Step("review", 2, "rae", 2500, 200, 0.0050m, 14, "approved", "review-2.md"),
            new FlowStepRecord { NodeId = "ship", Round = 1, State = FlowNodeState.Failed, Detail = "step limit reached", Duration = TimeSpan.FromSeconds(3) }
        },
        Events = new()
        {
            new FlowLogEntry(T0.AddSeconds(30), "loop", "review", "review → build when rejected (loop 1)")
        }
    };

    private static readonly Dictionary<string, string> Outputs = new()
    {
        ["build-1.md"] = "first attempt",
        ["review-1.md"] = "needs paging",
        ["build-2.md"] = "Pagination added.",
        ["review-2.md"] = "Looks good now."
    };

    private static string? OutputOf(FlowStepRecord s) => s.OutputFile != null && Outputs.TryGetValue(s.OutputFile, out var text) ? text : null;

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void Report_MatchesTheGoldenFile()
    {
        var actual = FlowReportBuilder.Markdown(FailedRun(), OutputOf);
        AssertGolden("code-review-run.md", actual);
    }

    /// <summary>Set UPDATE_GOLDEN=1 to rewrite the golden file from the current output, then review the diff in git.</summary>
    private static void AssertGolden(string name, string actual, [CallerFilePath] string testFile = "")
    {
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(Path.GetDirectoryName(testFile)!, "Golden", name);
            File.WriteAllText(source, actual, new System.Text.UTF8Encoding(false));
            return;
        }
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Flows", "Golden", name));
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public void Report_UsesUnixLineEndings()
    {
        Assert.DoesNotContain('\r', FlowReportBuilder.Markdown(FailedRun(), OutputOf));
    }

    [Fact]
    public void Mermaid_ColoursNodesByStatus_AndShowsRoundsAndOutcomes()
    {
        var mermaid = FlowReportBuilder.Mermaid(FailedRun());

        Assert.Contains("class n_build,n_review done", mermaid);
        Assert.Contains("class n_ship failed", mermaid);
        Assert.Contains("class n_announce notrun", mermaid);
        Assert.Contains("<small>→ approved ×2</small>", mermaid);   // review: last outcome and two rounds
        Assert.Contains("<small>×2</small>", mermaid);               // build ran twice
    }

    [Fact]
    public void Mermaid_LabelsALoopWithTheRoundsActuallyTaken()
    {
        Assert.Contains("n_review -.->|rejected ↺ 1/2| n_build", FlowReportBuilder.Mermaid(FailedRun()));

        var noLoops = FailedRun() with { Events = new() };
        Assert.Contains("n_review -.->|rejected ↺ 0/2| n_build", FlowReportBuilder.Mermaid(noLoops));
    }

    [Fact]
    public void Mermaid_MarksASkippedStep()
    {
        var run = FailedRun() with
        {
            Steps = new() { new FlowStepRecord { NodeId = "ship", State = FlowNodeState.Skipped, Detail = "skipped by reviewer" } }
        };
        Assert.Contains("class n_ship skipped", FlowReportBuilder.Mermaid(run));
    }

    [Fact]
    public void Mermaid_WithoutARun_IsTheUnchangedPlainDiagram()
    {
        var plain = FlowGraph.Build(Definition(), null).ToMermaid();

        Assert.DoesNotContain("classDef", plain);
        Assert.Contains("n_review -.->|rejected ↺2| n_build", plain);
    }

    [Fact]
    public void Report_OfASucceededRun_HasNoFailureQuote()
    {
        var run = FailedRun() with { Status = FlowRunStatus.Succeeded, Message = "All steps finished." };
        var md = FlowReportBuilder.Markdown(run, OutputOf);

        Assert.Contains("✅ **succeeded**", md);
        Assert.DoesNotContain("> All steps finished.", md);
    }

    [Fact]
    public void Report_EscapesPipesInTableCells_AndKeepsTheTaskVerbatim()
    {
        var run = FailedRun() with
        {
            Task = "Fix | the <b>table</b>",
            Steps = new() { Step("build", 1, "a|b", 100, 10, 0.001m, 1, "ok|fine") }
        };
        var md = FlowReportBuilder.Markdown(run, OutputOf);

        Assert.Contains("| a\\|b |", md);
        Assert.Contains("| ok\\|fine |", md);
        Assert.Contains("Fix | the <b>table</b>", md);
    }

    [Fact]
    public void Report_ShortensAVeryLongAnswer()
    {
        var md = FlowReportBuilder.Markdown(FailedRun(), s => s.NodeId == "build" ? new string('x', FlowReportBuilder.MaxStepOutput * 2) : null);

        Assert.Contains("_…answer shortened._", md);
        Assert.True(md.Length < FlowReportBuilder.MaxStepOutput * 2);
    }

    [Fact]
    public void Report_NamesAReplayAndCountsReusedSteps()
    {
        var run = FailedRun() with
        {
            ReplayOfRunId = "old-run",
            ReplayFromNodeId = "review",
            Steps = new()
            {
                new FlowStepRecord { NodeId = "build", State = FlowNodeState.Done, Reused = true },
                Step("review", 1, "rae", 100, 10, 0.001m, 1, "approved", "review-2.md")
            }
        };
        var md = FlowReportBuilder.Markdown(run, OutputOf);

        Assert.Contains("Re-run of `old-run` from `review`", md);
        Assert.Contains("- Steps run: 1 (+1 reused)", md);
        Assert.Contains("| reused |", md);
    }

    [Fact]
    public void Report_WithNoSteps_StillRenders()
    {
        var md = FlowReportBuilder.Markdown(FailedRun() with { Steps = new(), Events = new() }, _ => null);

        Assert.Contains("_No step ran._", md);
        Assert.Contains("_No files were changed._", md);
    }
}
