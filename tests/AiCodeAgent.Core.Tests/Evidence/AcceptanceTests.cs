using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Evidence;

namespace AiCodeAgent.Core.Tests.Evidence;

/// <summary>E10-S2: the agent's marks are claims; the pack confirms each proof against the audit trail.</summary>
public sealed class AcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acceptance-" + Guid.NewGuid().ToString("N"));
    private int _id;

    public AcceptanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string FailingDotnet = """
        $ dotnet test TodoApp.slnx
        Exit code: 1
        STDOUT:
          Failed TodoApp.Tests.OverdueTests.Overdue_ExcludesCompletedItems [12 ms]
        Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 1 s
        """;

    private const string PassingDotnet = """
        $ dotnet test TodoApp.slnx
        Exit code: 0
        STDOUT:
        Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s
        """;

    private AuditTrail Open() =>
        AuditTrail.Open(Path.Combine(_dir, "run.audit.jsonl"), new AuditRunInfo("chat", "run-1", "chat task", "Fix overdue", "test-model", null, "Ask", "/work/app"), user: "alice");

    private string NextId() => "c" + (++_id);

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private void Tool(AuditTrail t, string tool, JsonObject arguments, string output = "ok", bool isError = false)
    {
        var id = NextId();
        t.Record("tool.call", null, new JsonObject { ["id"] = id, ["tool"] = tool, ["arguments"] = arguments });
        t.Record("tool.result", null, new JsonObject { ["id"] = id, ["tool"] = tool, ["isError"] = isError, ["durationMs"] = 5, ["output"] = output });
    }

    private void Declare(AuditTrail t, params string[] criteria) =>
        Tool(t, "acceptance_criteria", new JsonObject { ["action"] = "declare", ["criteria"] = new JsonArray(criteria.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()) });

    private void Mark(AuditTrail t, string marksJson, bool isError = false) =>
        Tool(t, "acceptance_criteria", new JsonObject { ["action"] = "mark", ["marks"] = Node(marksJson) }, isError: isError);

    private void Test(AuditTrail t, string output, bool isError) =>
        Tool(t, "execute_command", new JsonObject { ["command"] = "dotnet test" }, output, isError);

    private static void Change(AuditTrail t, string path, string diff, bool created = false) =>
        t.Record("file.change", null, new JsonObject { ["path"] = path, ["created"] = created, ["beforeSha256"] = "b", ["afterSha256"] = AuditChain.Sha256("x"), ["diffSha256"] = "d", ["diff"] = diff });

    private EvidencePack Finish(AuditTrail t)
    {
        t.Complete("completed", costUsd: 0.1m, tokens: 100);
        return EvidencePackStore.Build(t.Path);
    }

    private static CriterionEvidence Criterion(EvidencePack p, string id) => p.Acceptance!.Criteria.Single(c => c.Id == id);

    [Fact]
    public void ATestThatFailedBeforeAndPassesNow_Proves_AndAnUnmarkedCriterionStaysUnproven()
    {
        using var t = Open();
        Declare(t, "Overdue items exclude completed ones", "Items due today are not overdue");
        Test(t, FailingDotnet, isError: true);
        Change(t, "src/Todo.Core/TodoService.cs", "@@ -1 +1 @@\n-x.Due <= today\n+!x.Done && x.Due < today\n");
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Overdue_ExcludesCompletedItems"]}]""");

        var pack = Finish(t);

        Assert.Equal(CriterionStatus.Proven, Criterion(pack, "AC1").Status);
        Assert.Equal("Failed before, passes in the last test run.", Criterion(pack, "AC1").Proofs.Single().Detail);
        Assert.Equal(CriterionStatus.Unproven, Criterion(pack, "AC2").Status);
        Assert.Equal(1, pack.Acceptance!.ProvenCount);
        Assert.Contains(pack.Gaps, g => g.StartsWith("1 of 2 acceptance criteria are not proven: AC2 (unproven)", StringComparison.Ordinal));
    }

    [Fact]
    public void AllCriteriaProven_AddsNoGap()
    {
        using var t = Open();
        Declare(t, "It works");
        Test(t, FailingDotnet, isError: true);
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Overdue_ExcludesCompletedItems"],"files":["src/A.cs"]}]""");

        var pack = Finish(t);

        Assert.True(pack.Acceptance!.AllProven);
        Assert.DoesNotContain(pack.Gaps, g => g.Contains("acceptance"));
    }

    [Fact]
    public void ACitedTestThatIsNotConfirmedAnywhere_IsOnlyClaimed()
    {
        using var t = Open();
        Declare(t, "It works");
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Imaginary_Test"]}]""");

        var c = Criterion(Finish(t), "AC1");

        Assert.Equal(CriterionStatus.Claimed, c.Status);
        Assert.False(c.Proofs.Single().Verified);
    }

    [Fact]
    public void ATestAddedInTheDiff_IsConfirmed_WhenTheLastRunPassed()
    {
        using var t = Open();
        Declare(t, "Leap years are handled");
        Change(t, "tests/DateTests.cs", "@@ -1 +1,4 @@\n+[Fact]\n+public void Feb29_InLeapYear_IsValid() { }\n", created: true);
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Feb29_InLeapYear_IsValid"]}]""");

        var c = Criterion(Finish(t), "AC1");

        Assert.Equal(CriterionStatus.Proven, c.Status);
        Assert.Contains("tests/DateTests.cs", c.Proofs.Single().Detail);
    }

    [Fact]
    public void ATestNameInANonTestFile_IsNotConfirmedByTheDiff()
    {
        using var t = Open();
        Declare(t, "Leap years are handled");
        Change(t, "src/Dates.cs", "@@ -1 +1 @@\n+// Feb29_InLeapYear_IsValid\n");
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Feb29_InLeapYear_IsValid"]}]""");

        Assert.Equal(CriterionStatus.Claimed, Criterion(Finish(t), "AC1").Status);
    }

    [Fact]
    public void ACitedTestThatIsStillFailing_ContradictsTheMark()
    {
        using var t = Open();
        Declare(t, "Overdue items exclude completed ones");
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Test(t, FailingDotnet, isError: true);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Overdue_ExcludesCompletedItems"],"files":["src/A.cs"]}]""");

        var c = Criterion(Finish(t), "AC1");

        Assert.Equal(CriterionStatus.Contradicted, c.Status);
    }

    [Fact]
    public void FilesChangedAfterTheLastTestRun_MakeTestProofStale()
    {
        using var t = Open();
        Declare(t, "Overdue items exclude completed ones");
        Test(t, FailingDotnet, isError: true);
        Test(t, PassingDotnet, isError: false);
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Mark(t, """[{"id":"AC1","status":"met","tests":["Overdue_ExcludesCompletedItems"]}]""");

        var c = Criterion(Finish(t), "AC1");

        Assert.Equal(CriterionStatus.Claimed, c.Status);
        Assert.Contains("changed after the last test run", c.Proofs.Single().Detail);
    }

    [Fact]
    public void AFileProof_IsConfirmedByTheDiff_AndAnUntouchedFileIsNot()
    {
        using var t = Open();
        Declare(t, "The service is changed", "The docs are updated");
        Change(t, "src/Todo.Core/TodoService.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Mark(t, """[{"id":"AC1","status":"met","files":["Todo.Core/TodoService.cs"]},{"id":"AC2","status":"met","files":["docs/readme.md"]}]""");

        var pack = Finish(t);

        Assert.Equal(CriterionStatus.Proven, Criterion(pack, "AC1").Status);
        Assert.Equal(CriterionStatus.Claimed, Criterion(pack, "AC2").Status);
        Assert.Contains("not changed", Criterion(pack, "AC2").Proofs.Single().Detail);
    }

    [Fact]
    public void ACommandProof_NeedsItToHavePassedAfterTheLastChange()
    {
        using var t = Open();
        Declare(t, "Everything builds and passes");
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","commands":["dotnet test"]}]""");

        Assert.Equal(CriterionStatus.Proven, Criterion(Finish(t), "AC1").Status);
    }

    [Fact]
    public void ACommandThatFailedOrNeverRan_IsNotConfirmed()
    {
        using var t = Open();
        Declare(t, "Everything passes", "Lint is clean");
        Test(t, FailingDotnet, isError: true);
        Mark(t, """[{"id":"AC1","status":"met","commands":["dotnet test"]},{"id":"AC2","status":"met","commands":["dotnet format"]}]""");

        var pack = Finish(t);

        Assert.Equal(CriterionStatus.Claimed, Criterion(pack, "AC1").Status);
        Assert.Equal(CriterionStatus.Claimed, Criterion(pack, "AC2").Status);
    }

    [Fact]
    public void ANoteIsNeverProof_AndNotMetIsReportedAsSuch()
    {
        using var t = Open();
        Declare(t, "It works", "It is fast");
        Mark(t, """[{"id":"AC1","status":"met","note":"I checked, believe me"},{"id":"AC2","status":"not_met","note":"needs profiling"}]""");

        var pack = Finish(t);

        Assert.Equal(CriterionStatus.Claimed, Criterion(pack, "AC1").Status);
        Assert.Equal(CriterionStatus.NotMet, Criterion(pack, "AC2").Status);
        Assert.Contains(pack.Gaps, g => g.Contains("AC1 (claimed, not confirmed)") && g.Contains("AC2 (not met)"));
    }

    [Fact]
    public void CriteriaDeclaredAfterTheFirstEdit_AreFlagged()
    {
        using var t = Open();
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Declare(t, "It works");

        var pack = Finish(t);

        Assert.True(pack.Acceptance!.DeclaredAfterEdits);
        Assert.Contains(pack.Gaps, g => g.Contains("declared after the first edit"));
    }

    [Fact]
    public void CriteriaDeclaredFirst_AreNotFlagged()
    {
        using var t = Open();
        Declare(t, "It works");
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");

        Assert.False(Finish(t).Acceptance!.DeclaredAfterEdits);
    }

    [Fact]
    public void FailedToolCalls_AreNotReplayed()
    {
        using var t = Open();
        Declare(t, "It works");
        Mark(t, """[{"id":"AC9","status":"met"}]""", isError: true);
        Tool(t, "acceptance_criteria", new JsonObject { ["action"] = "declare", ["criteria"] = new JsonArray("replaced") }, "error", isError: true);

        var pack = Finish(t);

        var c = Assert.Single(pack.Acceptance!.Criteria);
        Assert.Equal("It works", c.Text);
        Assert.Equal(CriterionStatus.Unproven, c.Status);
    }

    [Fact]
    public void DeclaringAgain_ReplacesTheListAndDropsEarlierMarks()
    {
        using var t = Open();
        Declare(t, "first", "second");
        Mark(t, """[{"id":"AC1","status":"met","files":["a.cs"]}]""");
        Declare(t, "only one now");

        var pack = Finish(t);

        var c = Assert.Single(pack.Acceptance!.Criteria);
        Assert.Equal("only one now", c.Text);
        Assert.Equal(CriterionStatus.Unproven, c.Status);
    }

    [Fact]
    public void ALaterMark_OverridesAnEarlierOne()
    {
        using var t = Open();
        Declare(t, "It works");
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Mark(t, """[{"id":"AC1","status":"not_met"}]""");
        Mark(t, """[{"id":"AC1","status":"met","files":["src/A.cs"]}]""");

        Assert.Equal(CriterionStatus.Proven, Criterion(Finish(t), "AC1").Status);
    }

    [Fact]
    public void ARunWithoutCriteria_HasNoAcceptanceSection_AndNoAcceptanceGap()
    {
        using var t = Open();
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Test(t, PassingDotnet, isError: false);

        var pack = Finish(t);

        Assert.Null(pack.Acceptance);
        Assert.DoesNotContain(pack.Gaps, g => g.Contains("acceptance"));
        Assert.DoesNotContain("Acceptance criteria", EvidencePackRenderer.ToMarkdown(pack));
        Assert.DoesNotContain("Acceptance:", EvidencePackRenderer.ToSummaryMarkdown(pack));
    }

    [Fact]
    public void ARunThatOnlyDeclaredCriteria_IsWorthShowing()
    {
        using var t = Open();
        Declare(t, "It works");

        Assert.True(Finish(t).WorthShowing);
    }

    [Fact]
    public void TheFullPackAndTheSummary_ShowTheChecklistHonestly()
    {
        using var t = Open();
        Declare(t, "Overdue items exclude completed ones", "Docs updated | with a pipe");
        Test(t, FailingDotnet, isError: true);
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Test(t, PassingDotnet, isError: false);
        Mark(t, """[{"id":"AC1","status":"met","tests":["Overdue_ExcludesCompletedItems"]},{"id":"AC2","status":"met","note":"done"}]""");

        var pack = Finish(t);
        var full = EvidencePackRenderer.ToMarkdown(pack);
        var summary = EvidencePackRenderer.ToSummaryMarkdown(pack);

        Assert.Contains("### Acceptance criteria", full);
        Assert.Contains("1 of 2 proven", full);
        Assert.Contains("**proven**", full);
        Assert.Contains("claimed, not confirmed", full);
        Assert.Contains("Docs updated / with a pipe", full);
        Assert.Contains("- **Acceptance:** 1 of 2 proven · open: AC2 claimed, not confirmed", summary);
    }

    [Fact]
    public void TheStoredJson_RoundTripsTheChecklist()
    {
        using var t = Open();
        Declare(t, "It works");
        Change(t, "src/A.cs", "@@ -1 +1 @@\n-a\n+b\n");
        Mark(t, """[{"id":"AC1","status":"met","files":["src/A.cs"]}]""");
        var pack = Finish(t);

        var json = EvidencePackStore.ToJson(pack);

        Assert.Contains("\"acceptance\"", json);
        Assert.DoesNotContain("provenCount", json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("proven", doc.RootElement.GetProperty("acceptance").GetProperty("criteria")[0].GetProperty("status").GetString()!.ToLowerInvariant());
    }
}
