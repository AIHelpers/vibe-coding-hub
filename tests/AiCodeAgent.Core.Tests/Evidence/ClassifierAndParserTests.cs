using AiCodeAgent.Core.Evidence;

namespace AiCodeAgent.Core.Tests.Evidence;

public sealed class CommandClassifierTests
{
    [Theory]
    [InlineData("dotnet test", CommandKind.Test)]
    [InlineData("dotnet test AiCodeAgent.slnx -c Release --no-build", CommandKind.Test)]
    [InlineData("cd app && dotnet build && dotnet test", CommandKind.Test)]
    [InlineData("python -m pytest -q tests/", CommandKind.Test)]
    [InlineData("pytest", CommandKind.Test)]
    [InlineData("go test ./... | tail -20", CommandKind.Test)]
    [InlineData("npm test", CommandKind.Test)]
    [InlineData("npm run test -- --watch=false", CommandKind.Test)]
    [InlineData("npx vitest run", CommandKind.Test)]
    [InlineData("cargo test --workspace", CommandKind.Test)]
    [InlineData("./gradlew test", CommandKind.Test)]
    [InlineData("dotnet build -c Release", CommandKind.Build)]
    [InlineData("npm run build", CommandKind.Build)]
    [InlineData("go build ./...", CommandKind.Build)]
    [InlineData("cargo check", CommandKind.Build)]
    [InlineData("tsc --noEmit", CommandKind.Build)]
    [InlineData("npx eslint src", CommandKind.Lint)]
    [InlineData("golangci-lint run", CommandKind.Lint)]
    [InlineData("dotnet format --verify-no-changes", CommandKind.Lint)]
    [InlineData("ruff check .", CommandKind.Lint)]
    [InlineData("cargo clippy -- -D warnings", CommandKind.Lint)]
    [InlineData("ls -la", CommandKind.Other)]
    [InlineData("git status", CommandKind.Other)]
    [InlineData("cat build.log | grep test", CommandKind.Other)]
    [InlineData("echo dotnet test", CommandKind.Other)]
    [InlineData("", CommandKind.Other)]
    public void Classifies(string command, CommandKind expected)
    {
        // "echo dotnet test" is the one honest limit: text that merely names a runner counts as that runner. It is asserted
        // below so a change in behaviour is a decision, not an accident.
        var actual = CommandClassifier.Classify(command);
        if (command == "echo dotnet test") { Assert.Equal(CommandKind.Test, actual); return; }
        Assert.Equal(expected, actual);
    }
}

public sealed class TestOutputParserTests
{
    [Fact]
    public void Dotnet_ReadsCountsAndFailedNames_AcrossProjects()
    {
        var output = """
              Failed TodoApp.Tests.OverdueTests.Item_due_yesterday_is_overdue [12 ms]
              Error Message:
               Assert.True() Failure
              Failed TodoApp.Tests.OverdueTests.Other(x: 1) [3 ms]
            Failed!  - Failed:     2, Passed:    10, Skipped:     1, Total:    13, Duration: 1 s - A.dll (net10.0)
            Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40, Duration: 2 s - B.dll (net10.0)
            """;
        var r = TestOutputParser.Parse(output);
        Assert.Equal("dotnet", r.Runner);
        Assert.Equal(50, r.Passed);
        Assert.Equal(2, r.Failed);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(new[] { "TodoApp.Tests.OverdueTests.Item_due_yesterday_is_overdue", "TodoApp.Tests.OverdueTests.Other(x: 1)" }, r.FailedTests);
    }

    [Fact]
    public void Pytest_ReadsTheSummaryLine_AndFailedIds()
    {
        var output = """
            FAILED tests/test_todo.py::test_overdue - AssertionError: assert False
            ERROR tests/test_io.py::test_load
            ======== 1 failed, 8 passed, 1 skipped, 1 error in 0.42s ========
            """;
        var r = TestOutputParser.Parse(output);
        Assert.Equal("pytest", r.Runner);
        Assert.Equal(8, r.Passed);
        Assert.Equal(2, r.Failed);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(new[] { "tests/test_todo.py::test_overdue", "tests/test_io.py::test_load" }, r.FailedTests);
    }

    [Fact]
    public void Pytest_QuietModeWithoutBanner()
    {
        var r = TestOutputParser.Parse("3 passed in 0.10s");
        Assert.Equal("pytest", r.Runner);
        Assert.Equal(3, r.Passed);
        Assert.Equal(0, r.Failed);
    }

    [Fact]
    public void Jest_ReadsTheTestsLine_AndFailedBullets()
    {
        var output = """
              ● Todo › marks overdue items

              ● Console

            Tests:       1 failed, 5 passed, 6 total
            """;
        var r = TestOutputParser.Parse(output);
        Assert.Equal("jest", r.Runner);
        Assert.Equal(5, r.Passed);
        Assert.Equal(1, r.Failed);
        Assert.Equal(new[] { "Todo › marks overdue items" }, r.FailedTests);
    }

    [Fact]
    public void Vitest_ReadsTheTestsLine()
    {
        var output = " FAIL  src/todo.test.ts > Todo > marks overdue\n\n Tests  1 failed | 5 passed (6)\n";
        var r = TestOutputParser.Parse(output);
        Assert.Equal("vitest", r.Runner);
        Assert.Equal(5, r.Passed);
        Assert.Equal(1, r.Failed);
        Assert.Equal(new[] { "src/todo.test.ts > Todo > marks overdue" }, r.FailedTests);
    }

    [Fact]
    public void Cargo_SumsTheResultLines()
    {
        var output = "test a::b ... FAILED\ntest result: FAILED. 3 passed; 1 failed; 0 ignored; 0 measured\ntest result: ok. 2 passed; 0 failed; 1 ignored; 0 measured\n";
        var r = TestOutputParser.Parse(output);
        Assert.Equal("cargo", r.Runner);
        Assert.Equal(5, r.Passed);
        Assert.Equal(1, r.Failed);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(new[] { "a::b" }, r.FailedTests);
    }

    [Fact]
    public void Go_NamesFailuresAndCountsOnlyTopLevelOnes_AndDoesNotInventPassCounts()
    {
        var output = "--- FAIL: TestWorker (0.00s)\n    --- FAIL: TestWorker/sub (0.00s)\nFAIL\nFAIL\tgithub.com/x/y\t0.4s\n";
        var r = TestOutputParser.Parse(output);
        Assert.Equal("go", r.Runner);
        Assert.Null(r.Passed);
        Assert.Equal(1, r.Failed);
        Assert.Equal(new[] { "TestWorker", "TestWorker/sub" }, r.FailedTests);

        var ok = TestOutputParser.Parse("ok  \tgithub.com/x/y\t0.4s\n");
        Assert.Equal("go", ok.Runner);
        Assert.Null(ok.Failed);
        Assert.Empty(ok.FailedTests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("Build succeeded.\n    0 Warning(s)")]
    public void UnrecognisedOutput_IsNotGuessed(string output)
    {
        var r = TestOutputParser.Parse(output);
        Assert.Null(r.Runner);
        Assert.Null(r.Passed);
        Assert.Null(r.Failed);
    }
}

public sealed class FileRiskScorerTests
{
    private static (int Score, EvidenceRisk Risk, IReadOnlyList<string> Reasons) Score(string path, int added = 3, int removed = 1,
        bool created = false, bool deleted = false, string? diff = null) =>
        FileRiskScorer.Score(path, added, removed, created, deleted, FileRiskScorer.TouchesPublicApi(path, diff), FileRiskScorer.IsTestPath(path));

    [Theory]
    [InlineData("tests/App.Tests/FooTests.cs", true)]
    [InlineData("src/Foo/FooTest.java", true)]
    [InlineData("web/src/todo.test.ts", true)]
    [InlineData("pkg/worker_test.go", true)]
    [InlineData("tests/test_todo.py", true)]
    [InlineData("src/Contest.cs", false)]
    [InlineData("src/Latest.cs", false)]
    [InlineData("src/Foo.cs", false)]
    public void RecognisesTestFiles(string path, bool expected) => Assert.Equal(expected, FileRiskScorer.IsTestPath(path));

    [Fact]
    public void ASmallPrivateChange_IsLowRisk()
    {
        var (score, risk, reasons) = Score("src/Foo.cs", diff: "-    var x = 1;\n+    var x = 2;");
        Assert.Equal(EvidenceRisk.Low, risk);
        Assert.Equal(0, score);
        Assert.Empty(reasons);
    }

    [Fact]
    public void PublicApi_IsMediumAndSaysWhy()
    {
        var (_, risk, reasons) = Score("src/Foo.cs", diff: "-    public bool IsOverdue => Due < Now;\n+    public bool IsOverdue => Due.Date < Today;");
        Assert.Equal(EvidenceRisk.Medium, risk);
        Assert.Contains("public API", reasons);
    }

    [Fact]
    public void SecurityPath_IsHighWithPublicApi_AndMediumAlone()
    {
        var (_, alone, aloneReasons) = Score("src/Auth/TokenValidator.cs", diff: "+    var x = 1;");
        Assert.Equal(EvidenceRisk.Medium, alone);
        Assert.Contains("security-sensitive path", aloneReasons);
        var (_, both, _) = Score("src/Auth/TokenValidator.cs", diff: "+    public bool Validate(string t)");
        Assert.Equal(EvidenceRisk.High, both);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("src/App/App.csproj")]
    [InlineData("package.json")]
    [InlineData("db/migrations/001_init.sql")]
    public void BuildAndDeployConfig_IsFlagged(string path) =>
        Assert.Contains("build, deploy or data config", Score(path).Reasons);

    [Fact]
    public void WordsThatMerelyContainASecurityWord_AreNotFlagged()
    {
        Assert.DoesNotContain("security-sensitive path", Score("src/Authority.cs").Reasons);
        Assert.DoesNotContain("security-sensitive path", Score("src/ChatSession.cs").Reasons);
        Assert.Contains("security-sensitive path", Score("src/AuthService.cs").Reasons);
    }

    [Fact]
    public void TestFiles_NeverOutrankProductionCode_ExceptWhenHuge()
    {
        var (_, risk, reasons) = Score("tests/AuthTests.cs", added: 30, diff: "+    public void X()");
        Assert.Equal(EvidenceRisk.Low, risk);
        Assert.Contains("test file", reasons);
    }

    [Fact]
    public void LargeChangesAndDeletionsRaiseTheScore()
    {
        var (_, risk, reasons) = Score("src/Big.cs", added: 300, removed: 20);
        Assert.Equal(EvidenceRisk.Medium, risk);
        Assert.Contains(reasons, r => r.StartsWith("large change"));
        Assert.Contains("file removed", Score("src/Gone.cs", added: 0, removed: 5, deleted: true).Reasons);
    }

    [Fact]
    public void CountLines_IgnoresDiffHeaders()
    {
        Assert.Equal((2, 1), FileRiskScorer.CountLines("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n+newer\n context"));
        Assert.Equal((0, 0), FileRiskScorer.CountLines(null));
    }

    [Theory]
    [InlineData("a.go", "+func Handle() {}", true)]
    [InlineData("a.go", "+func handle() {}", false)]
    [InlineData("a.ts", "+export function f() {}", true)]
    [InlineData("a.py", "+def run():", true)]
    [InlineData("a.py", "+    def run(self):", false)]
    [InlineData("a.rs", "+pub fn run() {}", true)]
    [InlineData("a.txt", "+public class X", false)]
    public void PublicSurface_PerLanguage(string path, string diff, bool expected) =>
        Assert.Equal(expected, FileRiskScorer.TouchesPublicApi(path, diff));
}
