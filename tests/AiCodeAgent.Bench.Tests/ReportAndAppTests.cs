using System.Text.Json;

namespace AiCodeAgent.Bench.Tests;

public class ReportAndAppTests
{
    private static Attempt Make(string task, string config, int seed, string status, decimal? cost = 0.1m, double seconds = 10, string? note = null) => new()
    {
        Task = task, Config = config, Seed = seed, Status = status, Tokens = 1000, CostUsd = cost, AgentSeconds = seconds,
        Models = new[] { "test-model" }, StartedUtc = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), Note = note
    };

    [Fact]
    public void Csv_HasTheHeader_OneRowPerAttempt_AndEscapesTextFields()
    {
        var csv = ReportBuilder.Csv(new[]
        {
            Make("t1", "single", 1, AttemptStatus.Pass),
            Make("t1", "single", 2, AttemptStatus.Error, note: "bad, \"quoted\"\nline")
        });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ReportBuilder.CsvHeader, lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"bad, \"\"quoted\"\" line\"", lines[2]);
        Assert.StartsWith("t1,single,1,pass,", lines[1]);
    }

    [Fact]
    public void Report_StatesMethodology_Models_Dates_AndEveryTask()
    {
        using var box = new BenchSandbox(("alpha", "exit 1"), ("beta", "exit 1"), ("gamma", "exit 1"));
        var suite = box.Suite();
        var options = box.MakeOptions(o => o with { Seeds = 2 });
        var configs = BenchConfig.ParseList(new[] { "single", "bug-fix-flow" });
        var attempts = new List<Attempt>();
        foreach (var t in suite.Tasks)
            foreach (var c in configs)
                for (var s = 1; s <= 2; s++)
                    attempts.Add(Make(t.Id, c.Name, s, t.Id == "gamma" && c.IsSingle ? AttemptStatus.Timeout : s == 1 ? AttemptStatus.Pass : AttemptStatus.Fail,
                        cost: c.IsSingle ? 0.1m : 0.4m, note: t.Id == "gamma" ? "timed out" : null));

        var ctx = new ReportContext(suite, options, configs, suite.Tasks.Select(t => t.Id).ToList(), attempts.Count,
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 15, 30, 0, TimeSpan.Zero), "aiagent 1.2.3", "tools/Bench test");
        var md = ReportBuilder.Markdown(ctx, attempts);

        Assert.Contains("## Methodology", md);
        Assert.Contains("2026-03-01 12:00 UTC", md);
        Assert.Contains("2026-03-01 15:30 UTC", md);
        Assert.Contains("`test-model`", md);
        Assert.Contains("aiagent 1.2.3", md);
        Assert.Contains("No cherry-picking", md);
        Assert.Contains("Wilson", md);
        Assert.Contains("bootstrap", md);
        foreach (var id in new[] { "alpha", "beta", "gamma" }) Assert.Contains($"| `{id}` |", md);
        Assert.Contains("## Paired comparison with `single`", md);
        Assert.Contains("## Attempts that timed out or errored", md);
        Assert.DoesNotContain("Incomplete run", md);
        Assert.DoesNotContain("Warning", md);
    }

    [Fact]
    public void Report_SaysSoWhenTheRunIsIncomplete_OrTheModelsDiffer()
    {
        using var box = new BenchSandbox(("alpha", "exit 1"));
        var suite = box.Suite();
        var configs = BenchConfig.ParseList(new[] { "single" });
        var attempts = new List<Attempt> { Make("alpha", "single", 1, AttemptStatus.Pass) with { Models = new[] { "other-model" } } };
        var ctx = new ReportContext(suite, box.MakeOptions(), configs, new[] { "alpha" }, 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "aiagent", "h");
        var md = ReportBuilder.Markdown(ctx, attempts);
        Assert.Contains("Incomplete run", md);
        Assert.Contains("1 of 3", md);
        Assert.Contains("differ from the requested one", md);
    }

    [Fact]
    public void Report_WarnsWhenNoTokensWereRecorded()
    {
        using var box = new BenchSandbox(("alpha", "exit 1"));
        var suite = box.Suite();
        var configs = BenchConfig.ParseList(new[] { "single" });
        var attempts = new List<Attempt> { Make("alpha", "single", 1, AttemptStatus.Fail) with { Tokens = null, CostUsd = null } };
        var ctx = new ReportContext(suite, box.MakeOptions(), configs, new[] { "alpha" }, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "aiagent", "h");
        Assert.Contains("no tokens were recorded for `single`", ReportBuilder.Markdown(ctx, attempts));
    }

    [Fact]
    public async Task TheDocumentedCommand_ProducesReportAndCsv_ForEveryTaskConfigAndSeed()
    {
        using var box = new BenchSandbox(("one", "exit 0"), ("two", "exit 1"));
        var agent = new FakeAgent();
        var stdout = new StringWriter();
        var code = await BenchApp.RunAsync(
            new[] { "--tasks", box.TasksFile, "--configs", "single,bug-fix-flow", "--model", "test-model", "--seeds", "3", "--out", box.OutDir, "--work-dir", box.WorkDir },
            agent, stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(2 * 2 * 3, agent.Calls.Count);
        var report = File.ReadAllText(Path.Combine(box.OutDir, "report.md"));
        Assert.Contains("## Methodology", report);
        Assert.Contains("`test-model`", report);
        Assert.Contains("| `one` |", report);
        Assert.Contains("| `two` |", report);
        var rows = File.ReadAllLines(Path.Combine(box.OutDir, "attempts.csv"));
        Assert.Equal(1 + 12, rows.Length);
        // The first attempts of seed 1 come before any attempt of seed 2.
        Assert.Equal(new[] { 1, 1, 1, 1 }, agent.Calls.Take(4).Select(c => c.Seed));
    }

    [Fact]
    public async Task ResultsAreNeverOverwritten_ButAStoppedRunCanBeResumed()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        string[] Args(params string[] more) => new[] { "--tasks", box.TasksFile, "--configs", "single", "--model", "m", "--seeds", "2", "--out", box.OutDir, "--work-dir", box.WorkDir }.Concat(more).ToArray();

        var first = new FakeAgent();
        Assert.Equal(0, await BenchApp.RunAsync(Args(), first, new StringWriter(), new StringWriter(), CancellationToken.None));
        Assert.Equal(2, first.Calls.Count);

        var refused = new StringWriter();
        Assert.Equal(2, await BenchApp.RunAsync(Args(), new FakeAgent(), new StringWriter(), refused, CancellationToken.None));
        Assert.Contains("--resume", refused.ToString());

        // Drop the last attempt, as if the run had been killed, then resume: only that one is repeated.
        var store = AttemptStore.FilePath(box.OutDir);
        File.WriteAllLines(store, File.ReadAllLines(store).Take(1));
        var resumed = new FakeAgent();
        Assert.Equal(0, await BenchApp.RunAsync(Args("--resume"), resumed, new StringWriter(), new StringWriter(), CancellationToken.None));
        Assert.Single(resumed.Calls);
        Assert.Equal(2, resumed.Calls[0].Seed);
        Assert.Equal(1 + 2, File.ReadAllLines(Path.Combine(box.OutDir, "attempts.csv")).Length);
    }

    [Fact]
    public async Task ACancelledRun_StillWritesAPartialReport_AndExitsWithOne()
    {
        using var box = new BenchSandbox(("one", "exit 0"), ("two", "exit 0"));
        using var cts = new CancellationTokenSource();
        var agent = new FakeAgent { Behaviour = inv => { cts.Cancel(); return FakeAgent.Ok(inv); } };
        var code = await BenchApp.RunAsync(
            new[] { "--tasks", box.TasksFile, "--configs", "single", "--model", "m", "--seeds", "1", "--out", box.OutDir, "--work-dir", box.WorkDir },
            agent, new StringWriter(), new StringWriter(), cts.Token);
        Assert.Equal(1, code);
        Assert.Contains("Incomplete run", File.ReadAllText(Path.Combine(box.OutDir, "report.md")));
    }

    [Fact]
    public async Task List_AndValidate_NeedNoAgent()
    {
        using var box = new BenchSandbox(("one", "exit 1"));
        var list = new StringWriter();
        Assert.Equal(0, await BenchApp.RunAsync(new[] { "--list", "--tasks", box.TasksFile }, null, list, new StringWriter(), CancellationToken.None));
        Assert.Contains("one", list.ToString());

        var validate = new StringWriter();
        Assert.Equal(0, await BenchApp.RunAsync(new[] { "--validate", "--tasks", box.TasksFile, "--work-dir", box.WorkDir }, null, validate, new StringWriter(), CancellationToken.None));
        Assert.Contains("All 1 task(s) are valid", validate.ToString());
    }

    [Fact]
    public async Task BadInput_ExitsWithTwo_AndSaysWhy()
    {
        var err = new StringWriter();
        Assert.Equal(2, await BenchApp.RunAsync(new[] { "--tasks", "does-not-exist.json", "--model", "m" }, null, new StringWriter(), err, CancellationToken.None));
        Assert.Contains("not found", err.ToString());
    }

    [Fact]
    public void Attempts_RoundTripThroughTheirJsonLine()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        AttemptStore.Append(box.OutDir, Make("one", "single", 1, AttemptStatus.Pass));
        var back = AttemptStore.Load(box.OutDir);
        Assert.Single(back);
        Assert.Equal(0.1m, back[0].CostUsd);
        Assert.Equal("test-model", back[0].Models[0]);
        Assert.DoesNotContain("\"key\"", File.ReadAllText(AttemptStore.FilePath(box.OutDir)));
        _ = JsonDocument.Parse(File.ReadAllLines(AttemptStore.FilePath(box.OutDir))[0]);
    }
}
