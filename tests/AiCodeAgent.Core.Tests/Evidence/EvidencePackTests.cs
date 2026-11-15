using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Evidence;

namespace AiCodeAgent.Core.Tests.Evidence;

/// <summary>E10-S1: the pack a reviewer reads is derived from the audit trail, says what it does not know, and is stored with the run.</summary>
public sealed class EvidencePackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evidence-" + Guid.NewGuid().ToString("N"));

    public EvidencePackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string FailingDotnet = """
        $ dotnet test TodoApp.slnx
        Exit code: 1
        STDOUT:
          Failed TodoApp.Tests.OverdueTests.Item_due_yesterday_is_overdue [12 ms]
          Failed TodoApp.Tests.OverdueTests.Other [3 ms]
        Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12, Duration: 1 s
        """;

    private const string PassingDotnet = """
        $ dotnet test TodoApp.slnx
        Exit code: 0
        STDOUT:
        Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s
        """;

    private AuditTrail Open(string name = "run.audit.jsonl") =>
        AuditTrail.Open(Path.Combine(_dir, name), new AuditRunInfo("chat", "run-1", "chat task", "Fix the overdue bug | in TodoApp", "test-model", null, "Ask", "/work/app"), user: "alice");

    private static void Command(AuditTrail t, string id, string command, string output, bool isError, string? agent = null)
    {
        t.Record("tool.call", agent, new JsonObject { ["id"] = id, ["tool"] = "execute_command", ["arguments"] = new JsonObject { ["command"] = command } });
        t.Record("tool.result", agent, new JsonObject { ["id"] = id, ["tool"] = "execute_command", ["isError"] = isError, ["durationMs"] = 40, ["output"] = output });
    }

    private static void Change(AuditTrail t, string path, string diff, bool created = false, string after = "x", string? agent = null) =>
        t.Record("file.change", agent, new JsonObject
        {
            ["path"] = path, ["created"] = created, ["beforeSha256"] = "b", ["afterSha256"] = AuditChain.Sha256(after), ["diffSha256"] = "d", ["diff"] = diff
        });

    private EvidencePack Finish(AuditTrail t, string status = "completed")
    {
        t.Complete(status, costUsd: 0.25m, tokens: 4200);
        return EvidencePackStore.Build(t.Path);
    }

    [Fact]
    public void ABugFix_ShowsBeforeAndAfter_WhichTestsNowPass_AndTheRiskiestFileFirst()
    {
        var trail = Open();
        Command(trail, "c1", "dotnet test TodoApp.slnx", FailingDotnet, isError: true);
        Change(trail, "src/TodoApp/Overdue.cs", "-    public bool IsOverdue => Due < Now;\n+    public bool IsOverdue => Due.Date < Today;");
        Change(trail, "src/TodoApp/README.md", "+one line");
        Command(trail, "c2", "dotnet build TodoApp.slnx", "Exit code: 0\nBuild succeeded.", isError: false);
        Command(trail, "c3", "dotnet test TodoApp.slnx", PassingDotnet, isError: false);
        var pack = Finish(trail);

        Assert.Equal("completed", pack.Status);
        Assert.Equal(0.25m, pack.CostUsd);
        Assert.Equal(4200, pack.Tokens);
        Assert.Equal("test-model", pack.Model);
        Assert.True(pack.AuditIntact);

        Assert.NotNull(pack.Tests);
        Assert.False(pack.Tests!.Before.Passed);
        Assert.True(pack.Tests.After.Passed);
        Assert.Equal(2, pack.Tests.Before.FailedCount);
        Assert.Equal(12, pack.Tests.After.PassedCount);
        Assert.Equal(new[] { "TodoApp.Tests.OverdueTests.Item_due_yesterday_is_overdue", "TodoApp.Tests.OverdueTests.Other" }, pack.Tests.Fixed);
        Assert.Empty(pack.Tests.NewlyFailing);
        Assert.True(pack.Tests.NamesKnown);

        Assert.True(pack.Build!.Passed);
        Assert.Equal(new[] { "src/TodoApp/Overdue.cs", "src/TodoApp/README.md" }, pack.Files.Select(f => f.Path));
        Assert.Equal(EvidenceRisk.Medium, pack.Files[0].Risk);
        Assert.Contains("public API", pack.Files[0].Reasons);
        Assert.Equal((1, 1), (pack.Files[0].Added, pack.Files[0].Removed));
        Assert.Empty(pack.Gaps);
        Assert.Equal(3, pack.Commands.Count);
        Assert.Equal(new[] { CommandKind.Test, CommandKind.Build, CommandKind.Test }, pack.Commands.Select(c => c.Kind));
    }

    [Fact]
    public void FilesChangedAfterTheLastTest_AndAFailingLastRun_AreNamedAsGaps()
    {
        var trail = Open();
        Command(trail, "c1", "dotnet test", PassingDotnet, isError: false);
        Change(trail, "src/Foo.cs", "+    var x = 1;");
        var pack = Finish(trail);
        Assert.Contains("Files were changed after the last test run.", pack.Gaps);

        var trail2 = Open("second.audit.jsonl");
        Command(trail2, "c1", "dotnet test", FailingDotnet, isError: true);
        var pack2 = Finish(trail2);
        Assert.Contains("The last test run failed.", pack2.Gaps);
    }

    [Fact]
    public void NoTests_NoCommands_IsStatedNotImplied()
    {
        var trail = Open();
        Change(trail, "src/Foo.cs", "+    var x = 1;");
        var pack = Finish(trail);
        Assert.Contains("No test run was observed.", pack.Gaps);
        Assert.Null(pack.Tests);
        Assert.Null(pack.Build);
        var md = EvidencePackRenderer.ToMarkdown(pack);
        Assert.Contains("No test run was observed.", md);
        Assert.Contains("no build command observed", md);
    }

    [Fact]
    public void ARunThatNeverEnded_IsIncomplete_AndAStoppedCommandIsKept()
    {
        var trail = Open();
        trail.Record("tool.call", null, new JsonObject { ["id"] = "c1", ["tool"] = "execute_command", ["arguments"] = new JsonObject { ["command"] = "dotnet test" } });
        trail.Dispose(); // killed mid-command: no tool.result, no run.end
        var pack = EvidencePackStore.Build(trail.Path);

        Assert.Equal("incomplete", pack.Status);
        Assert.Contains(pack.Gaps, g => g.Contains("no end record"));
        var command = Assert.Single(pack.Commands);
        Assert.Null(command.Passed);
        Assert.Contains("no result", EvidencePackRenderer.ToMarkdown(pack));
    }

    [Fact]
    public void ASingleTestRun_CannotBeComparedAndSaysSo()
    {
        var trail = Open();
        Command(trail, "c1", "dotnet test", PassingDotnet, isError: false);
        var pack = Finish(trail);
        Assert.Null(pack.Tests);
        Assert.Single(pack.TestRuns);
        Assert.Contains("no earlier run to compare", EvidencePackRenderer.ToMarkdown(pack));
    }

    [Fact]
    public void ARegression_IsReportedAsNewlyFailing()
    {
        var trail = Open();
        Command(trail, "c1", "go test ./...", "Exit code: 1\n--- FAIL: TestA (0.00s)\nFAIL", isError: true);
        Command(trail, "c2", "go test ./...", "Exit code: 1\n--- FAIL: TestB (0.00s)\nFAIL", isError: true);
        var pack = Finish(trail);
        Assert.Equal(new[] { "TestA" }, pack.Tests!.Fixed);
        Assert.Equal(new[] { "TestB" }, pack.Tests.NewlyFailing);
        Assert.Contains("regressions", EvidencePackRenderer.ToMarkdown(pack));
    }

    [Fact]
    public void UnrecognisedTestOutput_FallsBackToTheExitCode_WithoutInventingCounts()
    {
        var trail = Open();
        Command(trail, "c1", "make test", "Exit code: 2\nsomething broke", isError: true);
        var pack = Finish(trail);
        var run = Assert.Single(pack.TestRuns);
        Assert.False(run.Passed);
        Assert.Null(run.Runner);
        Assert.Null(run.PassedCount);
        Assert.Null(run.FailedCount);
        Assert.Contains("counts not available", EvidencePackRenderer.ToMarkdown(pack));
    }

    [Fact]
    public void VerifyChangesStages_AreRead_AsBuildTestAndLint()
    {
        var trail = Open();
        trail.Record("tool.call", null, new JsonObject { ["id"] = "v1", ["tool"] = "verify_changes", ["arguments"] = new JsonObject() });
        trail.Record("tool.result", null, new JsonObject
        {
            ["id"] = "v1", ["tool"] = "verify_changes", ["isError"] = true, ["durationMs"] = 900,
            ["output"] = "PASS  build: dotnet build\nFAIL  test: dotnet test (exit 1)\nPASS  lint: dotnet format --verify-no-changes"
        });
        var pack = Finish(trail);
        Assert.True(pack.Build!.Passed);
        Assert.True(pack.Lint!.Passed);
        var test = Assert.Single(pack.TestRuns);
        Assert.False(test.Passed);
        Assert.Equal(3, pack.Commands.Count);
    }

    [Fact]
    public void ScreenshotsFromBrowserCheck_AreListedWithTheirUrl()
    {
        var trail = Open();
        trail.Record("tool.call", null, new JsonObject { ["id"] = "b1", ["tool"] = "browser_check", ["arguments"] = new JsonObject { ["url"] = "http://localhost:5000" } });
        trail.Record("tool.result", null, new JsonObject { ["id"] = "b1", ["tool"] = "browser_check", ["isError"] = false, ["output"] = "Title: Todo\nScreenshot saved: /work/app/.aiagent/screenshots/page-1.png" });
        var pack = Finish(trail);
        var shot = Assert.Single(pack.Screenshots);
        Assert.Equal("/work/app/.aiagent/screenshots/page-1.png", shot.Path);
        Assert.Equal("http://localhost:5000", shot.Url);
    }

    [Fact]
    public void ADeletedFile_IsFlagged_AndSeveralChangesToOneFileAreMerged()
    {
        var trail = Open();
        Change(trail, "src/Old.cs", "-a\n-b\n-c", after: "");
        Change(trail, "src/Foo.cs", "+one");
        Change(trail, "src/Foo.cs", "+two\n-three");
        var pack = Finish(trail);
        var old = pack.Files.Single(f => f.Path == "src/Old.cs");
        Assert.True(old.Deleted);
        Assert.Contains("file removed", old.Reasons);
        var foo = pack.Files.Single(f => f.Path == "src/Foo.cs");
        Assert.Equal(2, foo.Changes);
        Assert.Equal((2, 1), (foo.Added, foo.Removed));
    }

    [Fact]
    public void FlowSteps_ApprovalsAndAgentAttribution_AreCarried()
    {
        var trail = Open();
        trail.StepStarted("fix", 1, "dev", "m", "p");
        Command(trail, "c1", "dotnet build", "Exit code: 0", isError: false, agent: "fix");
        trail.StepFinished("fix", 1, "Succeeded", "done", null, 900, 0.12m, TimeSpan.FromSeconds(75), 4, 2);
        trail.Record("approval.request", null, new JsonObject { ["id"] = "a1", ["tool"] = "write_file" });
        trail.Record("approval.decision", null, new JsonObject { ["id"] = "a1", ["decision"] = "denied" });
        trail.Record("approval.request", null, new JsonObject { ["id"] = "a2", ["tool"] = "write_file" });
        trail.Record("approval.decision", null, new JsonObject { ["id"] = "a2", ["decision"] = "approved" });
        var pack = Finish(trail);

        var step = Assert.Single(pack.Steps);
        Assert.Equal(("fix", "Succeeded", 2), (step.Id, step.State, step.FilesChanged));
        Assert.Equal(75, step.Seconds);
        Assert.Equal("fix", pack.Commands[0].Step);
        Assert.Equal(new ApprovalEvidence(2, 1, 1), pack.Approvals);
        Assert.Contains("| fix |", EvidencePackRenderer.ToMarkdown(pack));
    }

    [Fact]
    public void ATamperedTrail_IsReportedAndNotRelied()
    {
        var trail = Open();
        Command(trail, "c1", "dotnet test", PassingDotnet, isError: false);
        trail.Complete("completed");
        var lines = File.ReadAllLines(trail.Path);
        lines[1] = lines[1].Replace("dotnet test", "dotnet tesT");
        File.WriteAllLines(trail.Path, lines);

        var pack = EvidencePackStore.Build(trail.Path);
        Assert.False(pack.AuditIntact);
        Assert.Contains(pack.Gaps, g => g.Contains("failed verification"));
        Assert.Contains("FAILED verification", EvidencePackRenderer.ToMarkdown(pack));
    }

    // ------------------------------------------------------------------ stored with the run

    [Fact]
    public void CompletingARun_StoresThePackBesideTheAuditFile_AndItRoundTrips()
    {
        var trail = Open("20260101-120000-session.audit.jsonl");
        Command(trail, "c1", "dotnet test", FailingDotnet, isError: true);
        Command(trail, "c2", "dotnet test", PassingDotnet, isError: false);
        trail.Complete("completed", costUsd: 1.5m);

        var stored = Path.Combine(_dir, "20260101-120000-session.evidence.json");
        Assert.True(File.Exists(stored));
        Assert.Equal(stored, EvidencePackStore.PathFor(trail.Path));
        var loaded = JsonSerializer.Deserialize<EvidencePack>(File.ReadAllText(stored), EvidencePackStore.Json)!;
        Assert.Equal(1.5m, loaded.CostUsd);
        Assert.Equal(2, loaded.TestRuns.Count);
        Assert.Equal(2, loaded.Tests!.Fixed.Count);
        Assert.Equal(CommandKind.Test, loaded.Commands[0].Kind);
        Assert.Equal(EvidencePack.CurrentVersion, loaded.Version);
    }

    [Fact]
    public void AFlowRunFolder_GetsEvidenceJsonNextToItsAuditFile()
    {
        Assert.Equal(Path.Combine(_dir, "flow-1", "evidence.json"), EvidencePackStore.PathFor(Path.Combine(_dir, "flow-1", AuditChain.FileName)));
    }

    [Fact]
    public void WritingEvidence_NeverThrows_WhenTheFileIsMissing()
    {
        Assert.Null(EvidencePackStore.TryWrite(Path.Combine(_dir, "nope.audit.jsonl")));
    }

    // ------------------------------------------------------------------ Markdown

    [Fact]
    public void Markdown_HasEverySection_AndKeepsPipesAndBackticksOutOfTables()
    {
        var trail = Open();
        Command(trail, "c1", "dotnet test", FailingDotnet, isError: true);
        Change(trail, "src/we|ird`name.cs", "+    public int X;");
        Command(trail, "c2", "dotnet test", PassingDotnet, isError: false);
        var md = EvidencePackRenderer.ToMarkdown(Finish(trail));

        foreach (var heading in new[] { "## Evidence pack", "### Not proven", "### Tests", "### Build and lint", "### Changed files, riskiest first", "### Commands run" })
            Assert.Contains(heading, md);
        Assert.Contains("Now passing:", md);
        Assert.Contains("hash chain intact", md);
        Assert.Contains("Fix the overdue bug / in TodoApp", md);
        Assert.Contains("src/we/ird'name.cs", md);
        Assert.Contains("$0.25", md);
    }

    [Fact]
    public void Markdown_CapsLongLists()
    {
        var trail = Open();
        for (var i = 0; i < 40; i++) Change(trail, $"src/File{i:D2}.cs", "+x");
        for (var i = 0; i < 45; i++) Command(trail, "k" + i, "ls", "Exit code: 0", isError: false);
        var md = EvidencePackRenderer.ToMarkdown(Finish(trail));
        Assert.Contains("…and 15 more file(s)", md);
        Assert.Contains("…and 15 more.", md);
    }

    [Fact]
    public void Summary_ShowsTestsBeforeAndAfter_RiskiestFile_AndWhatIsNotProven()
    {
        using var t = Open();
        Command(t, "1", "dotnet test", FailingDotnet, isError: true);
        Change(t, "src/App/Auth/Login.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Command(t, "2", "dotnet test", PassingDotnet, isError: false);
        var pack = Finish(t);

        var summary = EvidencePackRenderer.ToSummaryMarkdown(pack);

        Assert.Contains("**Evidence** · completed", summary);
        Assert.Contains("2 failed → 12 passed", summary);
        Assert.Contains("Login.cs", summary);
        Assert.Contains("**Build:** not run", summary);
        Assert.True(summary.Length < EvidencePackRenderer.ToMarkdown(pack).Length);
    }

    [Fact]
    public void Summary_OfARunWithoutTests_SaysSo_AndListsTheGap()
    {
        using var t = Open();
        Change(t, "src/App/Thing.cs", "@@ -1 +1 @@\n-a\n+b\n");
        var pack = Finish(t);

        var summary = EvidencePackRenderer.ToSummaryMarkdown(pack);

        Assert.Contains("no test run observed", summary);
        Assert.Contains("**Not proven**", summary);
    }

    [Fact]
    public void WorthShowing_IsFalseForARunThatOnlyAnswered_AndTrueOnceAFileChanged()
    {
        using (var read = Open("read.audit.jsonl"))
        {
            read.Complete("completed", costUsd: 0.01m, tokens: 10);
            Assert.False(read.Evidence!.WorthShowing);
        }
        using var edit = Open("edit.audit.jsonl");
        Change(edit, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        edit.Complete("completed", costUsd: 0.01m, tokens: 10);
        Assert.True(edit.Evidence!.WorthShowing);
    }

    [Fact]
    public void WorthShowing_IsNotWrittenToTheJson()
    {
        using var t = Open();
        Command(t, "1", "dotnet test", PassingDotnet, isError: false);
        var json = EvidencePackStore.ToJson(Finish(t));
        Assert.DoesNotContain("worthShowing", json, StringComparison.OrdinalIgnoreCase);
    }
}
