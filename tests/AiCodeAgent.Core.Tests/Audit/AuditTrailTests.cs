using System.IO.Compression;
using AiCodeAgent.Core.Audit;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Tests.Audit;

/// <summary>E7-S2: what the audit file says about a run, how it is exported, and that no key ever reaches it.</summary>
public sealed class AuditTrailTests : IDisposable
{
    private const string EnvKey = "zk-live-9f8e7d6c5b4a39281706f5e4d3c2b1a0";
    private const string PromptKey = "sk-ant-api03-PromptTypedKey0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "audit-trail-" + Guid.NewGuid().ToString("N"));

    public AuditTrailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SecretRedactor Redactor() =>
        new(new[] { new KeyValuePair<string, string>("OPENAI_API_KEY", EnvKey) });

    private AuditTrail Open(string task = "fix the failing test", string name = "audit.jsonl") =>
        AuditTrail.Open(Path.Combine(_dir, name),
            new AuditRunInfo("chat", "run-1", "chat task", task, "claude-sonnet-test", "reviewer", "Ask", "/work/app"),
            Redactor(), user: "alice");

    private static ToolCall Call(string name, string id, params (string K, object? V)[] args) =>
        new() { Id = id, Name = name, Arguments = args.ToDictionary(a => a.K, a => a.V) };

    // ------------------------------------------------------------------ who / what / when / how much

    [Fact]
    public void ARun_RecordsWhoWhatWhenAndHowMuch()
    {
        string path;
        using (var trail = Open())
        {
            path = trail.Path;
            var read = Call("read_file", "c1", ("path", "src/App.cs"));
            trail.Observe(new ToolCallStartEvent(read));
            trail.Observe(new ToolCallEndEvent(read, new ToolResult { Content = "class App {}" }, TimeSpan.FromMilliseconds(12)));

            var write = Call("write_file", "c2", ("path", "src/App.cs"));
            var gate = new TaskCompletionSource<bool>();
            var approval = new ApprovalRequestEvent(write, gate, RiskLevel.Write);
            trail.Observe(approval);
            approval.Feedback.Text = "looks fine";
            gate.SetResult(true);

            trail.Observe(new DiffProducedEvent(new DiffEntry { FilePath = "src/App.cs", OriginalContent = "class App {}", ModifiedContent = "class App { }", DiffText = "-class App {}\n+class App { }" }));
            trail.Observe(new TokenUsageEvent(new TokenUsage { PromptTokens = 1200, CompletionTokens = 300 }, 0.0123m));
            trail.Complete("completed");
        }

        var result = AuditChainVerifier.Verify(path);
        Assert.True(result.Ok);
        Assert.True(result.HasEndRecord);
        var entries = AuditExporter.Read(path);
        Assert.Equal(new[] { "run.start", "tool.call", "tool.result", "approval.request", "approval.decision", "file.change", "usage", "run.end" },
            entries.Select(e => e.Kind));

        // who
        Assert.All(entries, e => Assert.Equal("alice", e.Entry["user"]!.GetValue<string>()));
        Assert.All(entries, e => Assert.Equal("claude-sonnet-test", e.Entry["model"]!.GetValue<string>()));
        Assert.All(entries, e => Assert.Equal("reviewer", e.Entry["character"]!.GetValue<string>()));
        // when
        Assert.All(entries, e => Assert.True(DateTimeOffset.TryParse(e.At, out _)));
        // what: arguments and results
        Assert.Equal("src/App.cs", entries[1].Data["arguments"]!["path"]!.GetValue<string>());
        Assert.Equal("class App {}", entries[2].Str("output"));
        Assert.Equal(AuditChain.Sha256("class App {}"), entries[2].Str("outputSha256"));
        // approvals and who decided
        Assert.Equal("approved", entries[4].Str("decision"));
        Assert.Equal("alice", entries[4].Str("decidedBy"));
        Assert.Equal("looks fine", entries[4].Str("note"));
        Assert.Equal("Write", entries[3].Str("risk"));
        // files changed, with hashes
        Assert.Equal(AuditChain.Sha256("class App {}"), entries[5].Str("beforeSha256"));
        Assert.Equal(AuditChain.Sha256("class App { }"), entries[5].Str("afterSha256"));
        Assert.Contains("+class App { }", entries[5].Str("diff"));
        // how much
        Assert.Equal("1200", entries[6].Str("promptTokens"));
        Assert.Equal("0.0123", entries[7].Str("costUsd"));
    }

    [Fact]
    public void ADeniedApproval_IsRecordedWithTheNote()
    {
        string path;
        using (var trail = Open())
        {
            path = trail.Path;
            var gate = new TaskCompletionSource<bool>();
            var approval = new ApprovalRequestEvent(Call("run_command", "c1", ("command", "rm -rf build")), gate, RiskLevel.Execute);
            trail.Observe(approval);
            approval.Feedback.Text = "never delete build";
            gate.SetResult(false);
            trail.Complete("completed");
        }

        var decision = AuditExporter.Read(path).Single(e => e.Kind == "approval.decision");
        Assert.Equal("denied", decision.Str("decision"));
        Assert.Equal("never delete build", decision.Str("note"));
    }

    [Fact]
    public void ADecisionByTheSystem_NamesWhoDecided()
    {
        string path;
        using (var trail = Open())
        {
            path = trail.Path;
            var gate = new TaskCompletionSource<bool>();
            var approval = new ApprovalRequestEvent(Call("write_file", "c1"), gate);
            trail.Observe(approval);
            approval.Feedback.DecidedBy = "headless run (no person to ask; denied)";
            gate.SetResult(false);
            trail.Complete("completed");
        }

        Assert.Equal("headless run (no person to ask; denied)", AuditExporter.Read(path).Single(e => e.Kind == "approval.decision").Str("decidedBy"));
    }

    [Fact]
    public void AnApprovalNobodyAnswered_HasNoDecision_AndACancelledOneSaysSo()
    {
        string path;
        using (var trail = Open())
        {
            path = trail.Path;
            trail.Observe(new ApprovalRequestEvent(Call("a", "c1"), new TaskCompletionSource<bool>()));
            var cancelled = new TaskCompletionSource<bool>();
            trail.Observe(new ApprovalRequestEvent(Call("b", "c2"), cancelled));
            cancelled.SetCanceled();
            trail.Complete("cancelled");
        }

        var decisions = AuditExporter.Read(path).Where(e => e.Kind == "approval.decision").ToList();
        Assert.Single(decisions);
        Assert.Equal("cancelled", decisions[0].Str("decision"));
    }

    [Fact]
    public void LongOutput_IsCut_ButItsFullHashAndLengthAreKept()
    {
        var big = new string('x', AuditTrail.MaxOutputChars + 500);
        string path;
        using (var trail = Open())
        {
            path = trail.Path;
            var call = Call("read_file", "c1");
            trail.Observe(new ToolCallStartEvent(call));
            trail.Observe(new ToolCallEndEvent(call, new ToolResult { Content = big }, TimeSpan.Zero));
            trail.Complete("completed");
        }

        var result = AuditExporter.Read(path).Single(e => e.Kind == "tool.result");
        Assert.Equal(AuditTrail.MaxOutputChars, result.Str("output")!.Length);
        Assert.Equal("True", result.Data["outputTruncated"]!.ToString(), ignoreCase: true);
        Assert.Equal(big.Length.ToString(), result.Str("outputChars"));
        Assert.Equal(AuditChain.Sha256(big), result.Str("outputSha256"));
    }

    [Fact]
    public void FlowSteps_CarryTheirOwnCharacterAndModel()
    {
        string path;
        using (var trail = Open())
        {
            path = trail.Path;
            trail.StepStarted("review", 1, "claude-code-reviewer", "claude-code-acp", "acp");
            var call = Call("read_file", "c1");
            trail.Observe(new AgentTaggedEvent(new ToolCallStartEvent(call), "review", "reviewer"));
            trail.StepFinished("review", 1, "done", "approved", null, 1500, 0.02m, TimeSpan.FromSeconds(3), 1, 0);
            trail.Complete("succeeded");
        }

        var tool = AuditExporter.Read(path).Single(e => e.Kind == "tool.call");
        Assert.Equal("review", tool.Agent);
        Assert.Equal("claude-code-reviewer", tool.Entry["character"]!.GetValue<string>());
        Assert.Equal("claude-code-acp", tool.Entry["model"]!.GetValue<string>());
        Assert.Equal("acp", tool.Entry["provider"]!.GetValue<string>());
    }

    [Fact]
    public void AfterTheRunEnded_NothingIsWritten()
    {
        var trail = Open();
        trail.Complete("completed");
        var lines = File.ReadAllLines(trail.Path).Length;

        trail.Observe(new StatusUpdateEvent("late"));
        trail.Observe(new ToolCallStartEvent(Call("x", "c9")));
        trail.Complete("again");
        trail.Dispose();

        Assert.Equal(lines, File.ReadAllLines(trail.Path).Length);
        Assert.True(AuditChainVerifier.Verify(trail.Path).Ok);
    }

    [Fact]
    public void ATrailThatCannotBeCreated_IsNull_NotAnException()
    {
        var blocker = Path.Combine(_dir, "file-not-dir");
        File.WriteAllText(blocker, "x");

        var trail = AuditTrail.TryOpen(Path.Combine(blocker, "audit.jsonl"), new AuditRunInfo("chat", "r", "n", "t"));

        Assert.Null(trail);
    }

    [Fact]
    public void ChatTrails_GetOneFilePerTask_EvenInTheSameSecond()
    {
        var options = new AgentOptions { Model = "m", WorkingDirectory = _dir };

        using var first = AuditTrail.OpenChat("session 1", "one", options, runsRoot: _dir);
        using var second = AuditTrail.OpenChat("session 1", "two", options, runsRoot: _dir);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Path, second!.Path);
        Assert.EndsWith(AuditLocator.ChatSuffix, first.Path);
        first.Complete("completed");
        second.Complete("completed");
        Assert.Equal(2, new AuditLocator(_dir).ForSession("session 1").Count);
        Assert.Empty(new AuditLocator(_dir).ForSession("session 2"));
    }

    // ------------------------------------------------------------------ keys never reach the file

    [Fact]
    public void NoKeyAppearsInAnyAuditFile_NeitherFromTheEnvironmentNorFromAPrompt()
    {
        string path;
        using (var trail = Open(task: $"deploy with {PromptKey} and also {EnvKey}"))
        {
            path = trail.Path;
            var run = Call("run_command", "c1", ("command", $"curl -H 'x-api-key: {PromptKey}' https://api.example.test"), ("env", $"OPENAI_API_KEY={EnvKey}"));
            trail.Observe(new ToolCallStartEvent(run));
            trail.Observe(new ToolCallEndEvent(run, new ToolResult { Content = $"OPENAI_API_KEY={EnvKey}\nBearer abcDEF123456.ghiJKL789_0-xyz\n{PromptKey}" }, TimeSpan.Zero));
            var gate = new TaskCompletionSource<bool>();
            var approval = new ApprovalRequestEvent(run, gate, RiskLevel.Execute);
            trail.Observe(approval);
            approval.Feedback.Text = $"use {EnvKey} instead";
            gate.SetResult(false);
            trail.Observe(new DiffProducedEvent(new DiffEntry { FilePath = ".env", OriginalContent = "", ModifiedContent = $"KEY={PromptKey}", DiffText = $"+KEY={PromptKey}\n+OTHER={EnvKey}" }));
            trail.Observe(new AgentErrorEvent(new InvalidOperationException($"401 for key {PromptKey}")));
            trail.Complete("failed", $"stopped: {EnvKey}");
        }

        AssertNoKey(File.ReadAllText(path));
        // The file still verifies: redaction happens before hashing, not after.
        Assert.True(AuditChainVerifier.Verify(path).Ok);
    }

    [Fact]
    public void NoKeyAppearsInAnyExport()
    {
        string path;
        using (var trail = Open(task: $"use {PromptKey}"))
        {
            path = trail.Path;
            trail.Observe(new DiffProducedEvent(new DiffEntry { FilePath = "a.txt", OriginalContent = "a", ModifiedContent = "b", DiffText = $"+{EnvKey}" }));
            trail.Complete("completed");
        }

        foreach (var format in new[] { AuditExportFormat.Markdown, AuditExportFormat.Json, AuditExportFormat.Zip })
        {
            var output = Path.Combine(_dir, "export" + AuditExporter.DefaultExtension(format));
            AuditExporter.Export(path, output, format);
            if (format == AuditExportFormat.Zip)
            {
                using var zip = ZipFile.OpenRead(output);
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    AssertNoKey(reader.ReadToEnd(), requireMask: false);
                }
            }
            else
            {
                AssertNoKey(File.ReadAllText(output));
            }
        }
    }

    private static void AssertNoKey(string text, bool requireMask = true)
    {
        Assert.DoesNotContain(EnvKey, text);
        Assert.DoesNotContain(PromptKey, text);
        Assert.DoesNotContain("PromptTypedKey", text);
        Assert.DoesNotContain("abcDEF123456.ghiJKL789", text);
        if (requireMask) Assert.Contains(SecretRedactor.Mask, text);
    }

    // ------------------------------------------------------------------ export

    private string RecordedRun()
    {
        using var trail = Open();
        var call = Call("write_file", "c1", ("path", "src/App.cs"));
        trail.Observe(new ToolCallStartEvent(call));
        trail.Observe(new ToolCallEndEvent(call, new ToolResult { Content = "ok" }, TimeSpan.FromMilliseconds(5)));
        trail.Observe(new DiffProducedEvent(new DiffEntry { FilePath = "src/App.cs", OriginalContent = "a", ModifiedContent = "b", DiffText = "-a\n+b" }));
        trail.Observe(new TokenUsageEvent(new TokenUsage { PromptTokens = 10, CompletionTokens = 5 }, 0.5m));
        trail.Complete("completed");
        return trail.Path;
    }

    [Fact]
    public void MarkdownExport_ShowsTheRunAndTheVerification()
    {
        var path = RecordedRun();
        var output = Path.Combine(_dir, "report.md");

        var verification = AuditExporter.Export(path, output, AuditExportFormat.Markdown);

        var md = File.ReadAllText(output);
        Assert.True(verification.Ok);
        Assert.Contains("# Audit report — chat task", md);
        Assert.Contains("alice", md);
        Assert.Contains("`claude-sonnet-test`", md);
        Assert.Contains("Hash chain: **verified**", md);
        Assert.Contains(verification.HeadHash, md);
        Assert.Contains("src/App.cs", md);
        Assert.Contains("write_file", md);
    }

    [Fact]
    public void JsonExport_HoldsEveryEntryAndTheVerification()
    {
        var path = RecordedRun();
        var output = Path.Combine(_dir, "report.json");

        AuditExporter.Export(path, output, AuditExportFormat.Json);

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(output))!;
        Assert.True(root["verification"]!["ok"]!.GetValue<bool>());
        Assert.Equal(File.ReadAllLines(path).Length, root["entries"]!.AsArray().Count);
    }

    [Fact]
    public void ZipExport_HoldsTheVerbatimLog_TheReport_AndOneDiffPerFileChange()
    {
        var path = RecordedRun();
        var output = Path.Combine(_dir, "run.zip");

        AuditExporter.Export(path, output, AuditExportFormat.Zip);

        using var zip = ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("audit.jsonl", names);
        Assert.Contains("audit.md", names);
        Assert.Contains("verification.json", names);
        var diff = Assert.Single(names, n => n.StartsWith("diffs/"));
        Assert.EndsWith(".diff", diff);
        using (var reader = new StreamReader(zip.GetEntry(diff)!.Open()))
            Assert.Equal("-a\n+b", reader.ReadToEnd());
        using (var reader = new StreamReader(zip.GetEntry("audit.jsonl")!.Open()))
            Assert.Equal(File.ReadAllText(path), reader.ReadToEnd());
        // And the export verifies like the original.
        Assert.True(AuditChainVerifier.Verify(output).Ok);
    }

    [Fact]
    public void EditingAnExportedAuditFile_MakesVerifyFail_AndNameTheLine()
    {
        var path = RecordedRun();
        var output = Path.Combine(_dir, "run.zip");
        AuditExporter.Export(path, output, AuditExportFormat.Zip);

        // Take audit.jsonl out of the export, change one value on line 3 and check it again.
        var exported = Path.Combine(_dir, "exported-audit.jsonl");
        using (var zip = ZipFile.OpenRead(output))
        using (var reader = new StreamReader(zip.GetEntry("audit.jsonl")!.Open()))
            File.WriteAllText(exported, reader.ReadToEnd());
        Assert.True(AuditChainVerifier.Verify(exported).Ok);
        var lines = File.ReadAllLines(exported);
        lines[2] = lines[2].Replace("\"isError\":false", "\"isError\":true");
        Assert.NotEqual(File.ReadAllLines(exported)[2], lines[2]);
        File.WriteAllLines(exported, lines);

        var result = AuditChainVerifier.Verify(exported);

        Assert.False(result.Ok);
        Assert.Equal(3, result.Failure!.Line);
        // An export of the damaged file says so too.
        var again = AuditExporter.Export(exported, Path.Combine(_dir, "again.md"), AuditExportFormat.Markdown);
        Assert.False(again.Ok);
        Assert.Contains("BROKEN at line 3", File.ReadAllText(Path.Combine(_dir, "again.md")));
    }

    [Fact]
    public void ExportingSeveralRuns_PutsEachInItsOwnFolder_AndEachStillVerifies()
    {
        var first = RecordedRun();
        var second = Path.Combine(_dir, "20991231-235959-s2.audit.jsonl");
        using (var trail = AuditTrail.Open(second, new AuditRunInfo("chat", "run-2", "chat task", "second"), Redactor(), "alice"))
            trail.Complete("completed");
        var output = Path.Combine(_dir, "session.zip");

        var results = AuditExporter.ExportMany(new[] { first, second }, output, AuditExportFormat.Zip);

        Assert.All(results, r => Assert.True(r.Ok));
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(2, zip.Entries.Count(e => e.FullName.EndsWith("/audit.jsonl")));
    }

    [Fact]
    public void TheLocator_FindsARunByIdByPartOfItsNameAndByPath()
    {
        var chat = Path.Combine(_dir, "20260101-101010-session_a.audit.jsonl");
        File.WriteAllText(chat, "");
        var flowDir = Path.Combine(_dir, "flows", "feature-team", "20260101-111111-ab12");
        Directory.CreateDirectory(flowDir);
        File.WriteAllText(Path.Combine(flowDir, AuditChain.FileName), "");
        var locator = new AuditLocator(_dir);

        Assert.Equal(chat, locator.Resolve("session_a").Path);
        Assert.Equal(Path.Combine(flowDir, AuditChain.FileName), locator.Resolve("20260101-111111-ab12").Path);
        Assert.Equal(chat, locator.Resolve(chat).Path);
        Assert.Equal(Path.Combine(flowDir, AuditChain.FileName), locator.Resolve(flowDir).Path);
        Assert.Null(locator.Resolve("20260101").Path);
        Assert.Equal(2, locator.Resolve("20260101").Candidates.Count);
        Assert.Null(locator.Resolve("nothing-like-this").Path);
        Assert.Equal(2, locator.List().Count);
    }
}
