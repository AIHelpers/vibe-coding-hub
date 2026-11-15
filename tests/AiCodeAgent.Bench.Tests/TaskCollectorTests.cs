using System.Text.Json.Nodes;

namespace AiCodeAgent.Bench.Tests;

public class TestFileClassifierTests
{
    [Theory]
    [InlineData("tests/test_parser.py")]
    [InlineData("test/Foo.cs")]
    [InlineData("src/App.Tests/FooTests.cs")]
    [InlineData("src/App.UnitTests/Helpers/Bar.cs")]
    [InlineData("src/App/FooTests.cs")]
    [InlineData("pkg/parser_test.go")]
    [InlineData("web/src/__tests__/util.ts")]
    [InlineData("web/src/util.test.ts")]
    [InlineData("web/src/util.spec.js")]
    [InlineData("conftest.py")]
    [InlineData("src/test/java/com/acme/FooTest.java")]
    [InlineData(@"tests\data\input.json")]
    [InlineData("tests/__snapshots__/a.snap")]
    public void TestFiles_AreRecognised(string path) => Assert.True(TestFileClassifier.IsTestFile(path));

    [Theory]
    [InlineData("src/Calc.cs")]
    [InlineData("src/Contest.cs")]
    [InlineData("src/Latest.java")]
    [InlineData("pkg/parser.go")]
    [InlineData("src/App/Attestation.cs")]
    [InlineData("README.md")]
    [InlineData("docs/testing-guide.md")]
    public void SourceFiles_AreNotTests(string path) => Assert.False(TestFileClassifier.IsTestFile(path));
}

public class TaskCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vch-collect-tests-" + Guid.NewGuid().ToString("N")[..10]);
    private string Repo => Path.Combine(_root, "repo");
    private string Tasks => Path.Combine(_root, "bench", "tasks.json");

    public TaskCollectorTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        try { FileSystemUtil.ForceDelete(_root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<string> Git(string dir, params string[] args)
    {
        var r = await ProcessRunner.GitAsync(dir, args, CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)} failed: {r.Stderr}");
        return r.Stdout.Trim();
    }

    private async Task Commit(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(Repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }
        await Git(Repo, "add", "-A");
        await Git(Repo, "commit", "--quiet", "-m", message);
    }

    /// <summary>Two commits: a repository with a bug, and the fix that also adds a test.</summary>
    private async Task<(string Base, string Fix)> BugAndFix()
    {
        await Git(Repo, "init", "--quiet", "-b", "main");
        await Commit("initial", ("src/Calc.cs", "int Add(int a, int b) => a - b;\n"), ("README.md", "calc\n"));
        var baseSha = await Git(Repo, "rev-parse", "HEAD");
        await Commit("fix: Add subtracted",
            ("src/Calc.cs", "int Add(int a, int b) => a + b;\n"),
            ("tests/CalcTests.cs", "// Add(2,3) == 5\n"));
        return (baseSha, await Git(Repo, "rev-parse", "HEAD"));
    }

    private string Prompt(string text, string name = "issue.txt")
    {
        var file = Path.Combine(_root, name);
        File.WriteAllText(file, text);
        return file;
    }

    private Options CollectOptions(string fix, Func<Options, Options>? change = null)
    {
        var o = new Options
        {
            Collect = Repo, Fix = fix, Id = "calc-1", PromptFile = Prompt("Add returns the difference instead of the sum."),
            Language = "csharp", TestCommand = "dotnet test --filter CalcTests", GitUrl = "https://example.invalid/acme/calc.git",
            TasksPath = Tasks, WorkDir = Path.Combine(_root, "work")
        };
        return change == null ? o : change(o);
    }

    private static async Task<(int Exit, string Output)> Run(Options o)
    {
        var writer = new StringWriter();
        var exit = await TaskCollector.RunAsync(o, writer, CancellationToken.None);
        return (exit, writer.ToString());
    }

    [Fact]
    public async Task TheSnapshotIsTheCommitBeforeTheFix_AndTheEntryHasEverythingTheHarnessNeeds()
    {
        var (baseSha, fix) = await BugAndFix();

        var (exit, output) = await Run(CollectOptions(fix));

        Assert.Equal(0, exit);
        Assert.Contains(baseSha[..12], output);
        var entry = JsonNode.Parse(output[output.IndexOf('{')..(output.LastIndexOf('}') + 1)])!;
        Assert.Equal("calc-1", entry["id"]!.GetValue<string>());
        Assert.Equal(baseSha, entry["repo"]!["commit"]!.GetValue<string>());
        Assert.Equal("https://example.invalid/acme/calc.git", entry["repo"]!["git"]!.GetValue<string>());
        Assert.Null(entry["repo"]!["path"]);
        Assert.Equal("patches/calc-1.tests.patch", entry["testPatch"]!.GetValue<string>());
        Assert.Equal("dotnet restore", entry["setupCommand"]!.GetValue<string>());
        Assert.Equal("dotnet test --filter CalcTests", entry["testCommand"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheHiddenTestPatchHasOnlyTheTests_AndAppliesToTheSnapshot()
    {
        var (baseSha, fix) = await BugAndFix();
        await Run(CollectOptions(fix));

        var patch = File.ReadAllText(Path.Combine(_root, "bench", "patches", "calc-1.tests.patch"));
        Assert.Contains("tests/CalcTests.cs", patch);
        Assert.DoesNotContain("src/Calc.cs", patch);
        Assert.DoesNotContain("a + b", patch);

        // The proof that matters: the patch applies to a checkout of the snapshot.
        var work = Path.Combine(_root, "checkout");
        await Git(_root, "clone", "--quiet", Repo, work);
        await Git(work, "checkout", "--quiet", "--detach", baseSha);
        var patchPath = Path.Combine(_root, "bench", "patches", "calc-1.tests.patch");
        await Git(work, "apply", "--check", patchPath);
    }

    [Fact]
    public async Task APatchWithManyFiles_StillAppliesAndKeepsLineEndings()
    {
        await Git(Repo, "init", "--quiet", "-b", "main");
        await Commit("initial", ("src/A.cs", "a\r\n"));
        var baseSha = await Git(Repo, "rev-parse", "HEAD");
        await Commit("fix",
            ("src/A.cs", "b\r\n"),
            ("tests/T1.cs", "one\r\ntwo\r\n"), ("tests/sub dir/T2.cs", "x\r\n"), ("tests/T3.json", "{}\r\n"));
        var fix = await Git(Repo, "rev-parse", "HEAD");

        await Run(CollectOptions(fix));

        var patchPath = Path.Combine(_root, "bench", "patches", "calc-1.tests.patch");
        Assert.Contains("\r\n", File.ReadAllText(patchPath));
        var work = Path.Combine(_root, "checkout");
        await Git(_root, "clone", "--quiet", Repo, work);
        await Git(work, "checkout", "--quiet", "--detach", baseSha);
        await Git(work, "apply", "--check", patchPath);
    }

    [Fact]
    public async Task ACommitWithoutTests_IsRefused()
    {
        await Git(Repo, "init", "--quiet", "-b", "main");
        await Commit("initial", ("src/A.cs", "a\n"));
        await Commit("change", ("src/A.cs", "b\n"));
        var fix = await Git(Repo, "rev-parse", "HEAD");

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix)));

        Assert.Contains("no test files", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "bench", "patches")));
    }

    [Fact]
    public async Task ACommitThatOnlyChangesTests_IsRefused()
    {
        await Git(Repo, "init", "--quiet", "-b", "main");
        await Commit("initial", ("src/A.cs", "a\n"));
        await Commit("tests only", ("tests/ATests.cs", "t\n"));
        var fix = await Git(Repo, "rev-parse", "HEAD");

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix)));

        Assert.Contains("only test files", ex.Message);
    }

    [Fact]
    public async Task AMergeCommit_NeedsAParent()
    {
        await Git(Repo, "init", "--quiet", "-b", "main");
        await Commit("initial", ("src/A.cs", "a\n"));
        await Git(Repo, "checkout", "--quiet", "-b", "side");
        await Commit("side", ("src/B.cs", "b\n"), ("tests/BTests.cs", "t\n"));
        await Git(Repo, "checkout", "--quiet", "main");
        await Commit("main work", ("src/C.cs", "c\n"));
        await Git(Repo, "merge", "--quiet", "--no-ff", "-m", "merge", "side");
        var merge = await Git(Repo, "rev-parse", "HEAD");
        var firstParent = await Git(Repo, "rev-parse", "HEAD^1");

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(merge)));
        Assert.Contains("merge commit", ex.Message);

        var (exit, _) = await Run(CollectOptions(merge, o => o with { Parent = firstParent }));
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AParentThatIsNotOne_IsRefused()
    {
        var (_, fix) = await BugAndFix();
        var unrelated = await Git(Repo, "rev-parse", "HEAD");

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix, o => o with { Parent = unrelated })));

        Assert.Contains("not a parent", ex.Message);
    }

    [Fact]
    public async Task AnUnknownCommit_IsRefused()
    {
        await BugAndFix();

        await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef")));
    }

    [Fact]
    public async Task APromptThatNamesAChangedFile_GetsAWarning_AndADiffInThePromptToo()
    {
        var (_, fix) = await BugAndFix();
        var file = Prompt("Calc.cs is wrong.\n```diff\n- a - b\n+ a + b\n```", "leaky.txt");

        var (_, output) = await Run(CollectOptions(fix, o => o with { PromptFile = file }));

        Assert.Contains("names 'Calc.cs'", output);
        Assert.Contains("contains a diff", output);
    }

    [Fact]
    public async Task ADefaultTestCommand_IsFlaggedAsTooBroad()
    {
        var (_, fix) = await BugAndFix();

        var (_, output) = await Run(CollectOptions(fix, o => o with { TestCommand = null }));

        Assert.Contains("runs the whole suite", output);
        Assert.Contains("dotnet test --nologo", output);
    }

    [Fact]
    public async Task WithoutALanguageOrATestCommand_TheCollectorAsks()
    {
        var (_, fix) = await BugAndFix();

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix, o => o with { TestCommand = null, Language = null })));

        Assert.Contains("--test-command", ex.Message);
    }

    [Fact]
    public async Task ARepositoryWithoutAnOriginAndWithoutGitUrl_IsRefused()
    {
        var (_, fix) = await BugAndFix();

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix, o => o with { GitUrl = null })));

        Assert.Contains("--git-url", ex.Message);
    }

    [Fact]
    public async Task Append_CreatesAFileThatTheHarnessLoads_AndKeepsExistingTasks()
    {
        var (_, fix) = await BugAndFix();
        Directory.CreateDirectory(Path.GetDirectoryName(Tasks)!);
        File.WriteAllText(Tasks, """
            { // a comment
              "name": "mine",
              "tasks": [ { "id": "old", "prompt": "p", "testCommand": "true", "repo": { "path": "x" } }, ]
            }
            """);

        var (exit, _) = await Run(CollectOptions(fix, o => o with { Append = true }));

        Assert.Equal(0, exit);
        var suite = TaskSuiteLoader.Load(Tasks);
        Assert.Equal("mine", suite.Name);
        Assert.Equal(new[] { "old", "calc-1" }, suite.Tasks.Select(t => t.Id).ToArray());
        var added = suite.Tasks[1];
        Assert.Equal("https://example.invalid/acme/calc.git", added.Repo.Git);
        Assert.Null(added.Repo.Path);
    }

    [Fact]
    public async Task Append_ToANewFile_UsesTheSuiteName()
    {
        var (_, fix) = await BugAndFix();

        await Run(CollectOptions(fix, o => o with { Append = true, SuiteName = "bugfix-v1" }));

        Assert.Equal("bugfix-v1", TaskSuiteLoader.Load(Tasks).Name);
    }

    [Fact]
    public async Task ATaskIsNeverCollectedTwice()
    {
        var (_, fix) = await BugAndFix();
        await Run(CollectOptions(fix, o => o with { Append = true }));

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix, o => o with { Append = true })));

        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task ADuplicateIdInTheTaskFile_IsRefused_WithoutLeavingAPatchBehind()
    {
        var (_, fix) = await BugAndFix();
        Directory.CreateDirectory(Path.GetDirectoryName(Tasks)!);
        File.WriteAllText(Tasks, """{ "tasks": [ { "id": "calc-1", "prompt": "p", "testCommand": "true", "repo": { "path": "x" } } ] }""");

        var ex = await Assert.ThrowsAsync<BenchException>(() => Run(CollectOptions(fix, o => o with { Append = true })));

        Assert.Contains("already has a task 'calc-1'", ex.Message);
    }

    [Fact]
    public void CollectNeedsNoModel_AndTheOptionsAreParsed()
    {
        var r = OptionsParser.Parse(new[]
        {
            "--collect", "https://github.com/a/b.git", "--fix", new string('a', 40), "--id", "b-1", "--prompt-file", "issue.txt",
            "--language", "python", "--test-command", "pytest tests/test_x.py", "--append"
        });

        Assert.Null(r.Error);
        Assert.Equal("https://github.com/a/b.git", r.Options!.Collect);
        Assert.Equal("b-1", r.Options.Id);
        Assert.True(r.Options.Append);
        Assert.Equal("python", r.Options.Language);
    }

    [Theory]
    [InlineData("--collect", "x")]
    public async Task MissingPieces_AreNamed(string flag, string value)
    {
        var o = OptionsParser.Parse(new[] { flag, value }).Options!;

        var ex = await Assert.ThrowsAsync<BenchException>(() => TaskCollector.RunAsync(o, new StringWriter(), CancellationToken.None));

        Assert.Contains("--id", ex.Message);
    }
}
