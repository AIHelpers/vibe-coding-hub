namespace AiCodeAgent.Bench.Tests;

public class MatrixTests
{
    private const string Secret = "sk-test-SECRET-123";

    private static string WriteMatrix(BenchSandbox box, string setupsJson)
    {
        var path = Path.Combine(box.Root, "matrix.json");
        File.WriteAllText(path, "{ \"name\": \"unit\", \"setups\": [" + setupsJson + "] }");
        return path;
    }

    private const string Cloud = """{ "id": "cloud", "provider": "anthropic", "model": "model-a", "apiKeyEnv": "TEST_KEY" }""";
    private const string NoKey = """{ "id": "nokey", "provider": "openai", "model": "model-b", "apiKeyEnv": "MISSING_KEY" }""";
    private const string Local = """{ "id": "local", "provider": "ollama", "model": "model-c", "baseUrl": "http://localhost:11434", "local": true }""";

    private static MatrixAvailability Availability(bool localUp = false) =>
        new(name => name == "TEST_KEY" ? Secret : null, (_, _) => Task.FromResult(localUp));

    private static Options MatrixOptions(BenchSandbox box, string matrixFile, Func<Options, Options>? change = null) =>
        box.MakeOptions(o => (change == null ? o : change(o)) with { Matrix = matrixFile, Configs = MatrixApp.DefaultConfigs, Model = null });

    private static IReadOnlyList<BenchConfig> DefaultConfigs() => BenchConfig.ParseList(MatrixApp.DefaultConfigs);

    // ------------------------------------------------------------ the matrix file

    [Fact]
    public void Loader_ReadsTheShippedMatrix()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "matrix.json");
        // The shipped file is linked into the test output so a typo in it fails here, not on the tester's machine.
        var definition = MatrixLoader.Load(path);
        Assert.Equal(new[] { "anthropic", "openai", "openai-compatible", "ollama", "lmstudio" }, definition.Setups.Select(s => s.Id));
    }

    [Theory]
    [InlineData("""{ "id": "x", "provider": "scripted", "model": "m" }""", "test double")]
    [InlineData("""{ "id": "x", "provider": "anthropic", "model": "m", "apiKeyEnv": "sk-ant-api03-abc-def" }""", "NAME of an environment variable")]
    [InlineData("""{ "id": "x", "provider": "anthropic", "model": "m" }""", "needs apiKeyEnv")]
    [InlineData("""{ "id": "x", "provider": "lmstudio", "model": "m" }""", "baseUrl is required")]
    [InlineData("""{ "id": "x", "provider": "ollama", "model": "m", "local": true }""", "needs baseUrl")]
    [InlineData("""{ "id": "x", "provider": "ollama", "model": "m", "baseUrl": "localhost:11434" }""", "absolute http")]
    [InlineData("""{ "id": "X Y", "provider": "ollama", "model": "m" }""", "lower-case")]
    [InlineData("""{ "id": "x", "provider": "ollama", "model": " " }""", "model is required")]
    [InlineData("""{ "id": "x", "provider": "ollama", "model": "m", "apikey": "oops" }""", "")]
    public void Loader_RejectsBadSetups(string setup, string expected)
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var ex = Assert.Throws<BenchException>(() => MatrixLoader.Load(WriteMatrix(box, setup)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Loader_RejectsDuplicateIds_AndAnEmptyFile()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        Assert.Contains("twice", Assert.Throws<BenchException>(() => MatrixLoader.Load(WriteMatrix(box, Cloud + "," + Cloud))).Message);
        Assert.Contains("no setups", Assert.Throws<BenchException>(() => MatrixLoader.Load(WriteMatrix(box, ""))).Message);
    }

    // ------------------------------------------------------------ running it

    [Fact]
    public async Task OnlyTestableSetupsRun_TheOthersAreSkippedWithTheReason_AndDefaultsApply()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, string.Join(",", Cloud, NoKey, Local));
        var agent = new FakeAgent();
        var seen = new List<Options>();
        var stdout = new StringWriter();

        var code = await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), o => { seen.Add(o); return agent; },
            Availability(localUp: false), stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code);
        // One task x three configurations x one seed, for the one setup that could run.
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(new[] { "single", "bug-fix-flow", "feature-team-flow" }, agent.Calls.Select(c => c.Config.Name));
        var run = seen.Last();
        Assert.Equal("anthropic", run.Provider);
        Assert.Equal("model-a", run.Model);
        Assert.Equal(1, run.Seeds);
        Assert.EndsWith(Path.Combine("cloud", "home"), run.AgentHome);
        Assert.Equal(Secret, run.AgentEnvironment!["AI_CODE_AGENT_ANTHROPIC_API_KEY"]);

        var report = File.ReadAllText(Path.Combine(box.OutDir, "matrix.md"));
        Assert.Contains("| cloud | pass | pass | pass | clean |", report);
        Assert.Contains("| nokey | skipped | skipped | skipped | not tested |", report);
        Assert.Contains("environment variable MISSING_KEY is not set", report);
        Assert.Contains("nothing answers at http://localhost:11434", report);
        Assert.DoesNotContain("| nokey | pass", report);
    }

    [Fact]
    public async Task AnErrorOrNonZeroExit_NeedsAttention_AndIsListedUnderFindings()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, Cloud);
        var agent = new FakeAgent
        {
            Behaviour = inv => inv.Config.IsSingle
                ? FakeAgent.Ok(inv, exit: 1) with { Note = "exit code 1: stream ended unexpectedly" }
                : FakeAgent.Ok(inv) with { ExitCode = 4, Note = "no key" }
        };

        var code = await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => agent,
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code); // problems are findings, not a harness failure
        var report = File.ReadAllText(Path.Combine(box.OutDir, "matrix.md"));
        Assert.Contains("needs attention", report);
        Assert.Contains("stream ended unexpectedly", report);
        Assert.Contains("| cloud | pass (non-zero exit) | error | error | needs attention |", report);
        var csv = File.ReadAllLines(Path.Combine(box.OutDir, "matrix.csv"));
        Assert.Equal(1 + 3, csv.Length);
        Assert.All(csv.Skip(1), l => Assert.Contains(",false,", l));
    }

    [Fact]
    public async Task AFailedFix_IsAModelResult_NotAFinding()
    {
        using var box = new BenchSandbox(("one", "exit 1")); // the hidden test always fails
        var file = WriteMatrix(box, Cloud);
        await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => new FakeAgent(),
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None);

        var report = File.ReadAllText(Path.Combine(box.OutDir, "matrix.md"));
        Assert.Contains("| cloud | fail | fail | fail | clean |", report);
        Assert.Contains("None: every tested setup ran clean.", report);
    }

    [Fact]
    public async Task TheApiKey_NeverReachesTheResults_EvenWhenTheAgentEchoesIt()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, Cloud);
        var agent = new FakeAgent { Behaviour = inv => FakeAgent.Ok(inv, exit: 1) with { Note = $"401: bad key {Secret}" } };
        await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => agent,
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None);

        foreach (var path in Directory.EnumerateFiles(box.OutDir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Secret, File.ReadAllText(path));
        Assert.Contains("bad key ***", File.ReadAllText(Path.Combine(box.OutDir, "matrix.md")));
    }

    [Fact]
    public async Task ASetupThatCannotBePrepared_IsReportedAsSuchAndNothingRuns()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, Cloud);
        var agent = new PreparingAgent("add-provider failed with " + Secret);
        var code = await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => agent,
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(1, code); // nothing could be tested
        Assert.Empty(agent.Calls);
        var report = File.ReadAllText(Path.Combine(box.OutDir, "matrix.md"));
        Assert.Contains("| cloud | setup failed | setup failed | setup failed | setup failed |", report);
        Assert.DoesNotContain(Secret, report);
    }

    [Fact]
    public async Task WhenNothingCanBeTested_ExitCodeIsOne_ButTheReportStillExplainsWhy()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, NoKey + "," + Local);
        var stderr = new StringWriter();
        var code = await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => new FakeAgent(),
            Availability(), new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains("No setup could be tested", stderr.ToString());
        Assert.True(File.Exists(Path.Combine(box.OutDir, "matrix.md")));
    }

    [Fact]
    public async Task SetupsAreFiltered_AndAnUnknownIdIsRefused()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, Cloud + "," + Local);
        var agent = new FakeAgent();
        await MatrixApp.RunAsync(MatrixOptions(box, file, o => o with { Setups = new[] { "cloud" } }), box.Suite(), DefaultConfigs(), _ => agent,
            Availability(localUp: true), new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.Equal(3, agent.Calls.Count);
        Assert.DoesNotContain("| local |", File.ReadAllText(Path.Combine(box.OutDir, "matrix.md")));

        var ex = await Assert.ThrowsAsync<BenchException>(() => MatrixApp.RunAsync(
            MatrixOptions(box, file, o => o with { Setups = new[] { "nope" }, OutDir = box.OutDir + "2" }), box.Suite(), DefaultConfigs(), _ => agent,
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None));
        Assert.Contains("'nope'", ex.Message);
    }

    [Fact]
    public async Task ResultsAreNeverOverwritten_ButResumeKeepsFinishedAttempts()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, Cloud);
        var first = new FakeAgent();
        await MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => first,
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None);

        await Assert.ThrowsAsync<BenchException>(() => MatrixApp.RunAsync(MatrixOptions(box, file), box.Suite(), DefaultConfigs(), _ => new FakeAgent(),
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None));

        var again = new FakeAgent();
        var code = await MatrixApp.RunAsync(MatrixOptions(box, file, o => o with { Resume = true }), box.Suite(), DefaultConfigs(), _ => again,
            Availability(), new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Empty(again.Calls); // everything was already done
        Assert.Contains("| cloud | pass | pass | pass | clean |", File.ReadAllText(Path.Combine(box.OutDir, "matrix.md")));
    }

    [Fact]
    public async Task TheCommandLine_RunsTheMatrix_WithoutAModelArgument()
    {
        using var box = new BenchSandbox(("one", "exit 0"));
        var file = WriteMatrix(box, Cloud);
        var agent = new FakeAgent();
        var code = await BenchApp.RunAsync(
            new[] { "--tasks", box.TasksFile, "--matrix", file, "--out", box.OutDir, "--work-dir", box.WorkDir },
            agent, new StringWriter(), new StringWriter(), CancellationToken.None, Availability());

        Assert.Equal(0, code);
        Assert.Equal(3, agent.Calls.Count);
        Assert.All(agent.Calls, c => Assert.Equal(1, c.Seed));
    }

    [Fact]
    public void Options_MatrixFlags_AreParsed()
    {
        var r = OptionsParser.Parse(new[] { "--matrix", "m.json", "--setups", "a, b", "--seeds", "2" });
        Assert.Null(r.Error);
        Assert.Equal("m.json", r.Options!.Matrix);
        Assert.Equal(new[] { "a", "b" }, r.Options.Setups);
        Assert.True(r.Options.SeedsGiven);
        Assert.False(OptionsParser.Parse(new[] { "--matrix", "m.json" }).Options!.SeedsGiven);
    }

    [Fact]
    public async Task Availability_ChecksKeyAndServer()
    {
        var cloud = new MatrixSetup { Id = "c", Provider = "anthropic", Model = "m", ApiKeyEnv = "K" };
        var local = new MatrixSetup { Id = "l", Provider = "ollama", Model = "m", BaseUrl = "http://localhost:1", Local = true };
        Assert.Null(await new MatrixAvailability(_ => "v").CheckAsync(cloud, CancellationToken.None));
        Assert.Contains("K is not set", await new MatrixAvailability(_ => "").CheckAsync(cloud, CancellationToken.None));
        Assert.Null(await new MatrixAvailability(null, (_, _) => Task.FromResult(true)).CheckAsync(local, CancellationToken.None));
        Assert.Contains("nothing answers", await new MatrixAvailability(null, (_, _) => Task.FromResult(false)).CheckAsync(local, CancellationToken.None));
    }

    private sealed class PreparingAgent : IAgentRunner, ISetupPreparer
    {
        private readonly string _problem;
        public List<AgentInvocation> Calls { get; } = new();
        public PreparingAgent(string problem) => _problem = problem;
        public Task<string?> PrepareAsync(MatrixSetup setup, IReadOnlyList<BenchConfig> configs, CancellationToken cancellation) => Task.FromResult<string?>(_problem);
        public Task<AgentRun> RunAsync(AgentInvocation invocation, CancellationToken cancellation)
        {
            Calls.Add(invocation);
            return Task.FromResult(FakeAgent.Ok(invocation));
        }
    }
}

public class ErrorSummaryTests
{
    [Fact]
    public void LeadsWithTheFirstLine_WhenAStackTraceFollows()
    {
        var stderr = "Unhandled exception: stream ended unexpectedly\n" + string.Join("\n", Enumerable.Range(0, 40).Select(i => $"   at Frame{i}()"));
        var text = CliAgentRunner.ErrorSummary(stderr);
        Assert.StartsWith("Unhandled exception: stream ended unexpectedly", text);
        Assert.Contains("Frame39", text);
    }

    [Fact]
    public void ShortErrors_AreKeptAsTheyAre()
    {
        Assert.Equal("boom", CliAgentRunner.ErrorSummary("boom\n"));
        Assert.Equal("", CliAgentRunner.ErrorSummary(""));
    }
}
