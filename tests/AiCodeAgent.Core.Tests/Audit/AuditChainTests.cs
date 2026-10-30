using AiCodeAgent.Core.Audit;

namespace AiCodeAgent.Core.Tests.Audit;

/// <summary>E7-S2: the hash chain of <c>audit.jsonl</c> — any edit is detected and the line is named.</summary>
public sealed class AuditChainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "audit-chain-" + Guid.NewGuid().ToString("N"));

    public AuditChainTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string PathOf(string name = "audit.jsonl") => Path.Combine(_dir, name);

    /// <summary>Five ordinary entries and a closing run.end: six lines.</summary>
    private (string Path, string Head) Write(bool close = true)
    {
        var path = PathOf();
        using var w = new AuditChainWriter(path);
        w.Append("{\"kind\":\"run.start\",\"data\":{\"task\":\"fix the bug\"}}");
        w.Append("{\"kind\":\"tool.call\",\"data\":{\"tool\":\"read_file\",\"n\":1}}");
        w.Append("{\"kind\":\"tool.result\",\"data\":{\"ok\":true,\"n\":2}}");
        w.Append("{\"kind\":\"approval.request\",\"data\":{\"tool\":\"write_file\"}}");
        w.Append("{\"kind\":\"approval.decision\",\"data\":{\"decision\":\"approved\"}}");
        if (close) w.Append("{\"kind\":\"run.end\",\"data\":{\"status\":\"completed\"}}");
        return (path, w.HeadHash);
    }

    [Fact]
    public void AnIntactChain_Verifies()
    {
        var (path, head) = Write();

        var result = AuditChainVerifier.Verify(path);

        Assert.True(result.Ok);
        Assert.Equal(6, result.Lines);
        Assert.Equal(head, result.HeadHash);
        Assert.True(result.HasEndRecord);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void EveryLine_StartsWithTheHashOfTheOneBefore()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path);

        Assert.Contains($"\"prev\":\"{AuditChain.GenesisHash}\"", lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            using var before = System.Text.Json.JsonDocument.Parse(lines[i - 1]);
            Assert.Contains($"\"prev\":\"{before.RootElement.GetProperty("hash").GetString()}\"", lines[i]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EditingAnyLine_FailsAndNamesIt(int line)
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path);
        lines[line - 1] = lines[line - 1].Replace("\"entry\":{", "\"entry\":{\"edited\":true,");
        File.WriteAllLines(path, lines);

        var result = AuditChainVerifier.Verify(path);

        Assert.False(result.Ok);
        Assert.Equal(line, result.Failure!.Line);
        Assert.Contains("modified", result.Failure.Problem);
    }

    [Fact]
    public void ChangingOneCharacterOfAValue_Fails()
    {
        var (path, _) = Write();
        File.WriteAllText(path, File.ReadAllText(path).Replace("fix the bug", "fix the bag"));

        var result = AuditChainVerifier.Verify(path);

        Assert.False(result.Ok);
        Assert.Equal(1, result.Failure!.Line);
    }

    [Fact]
    public void ARewrittenLineWithAFreshHash_IsCaughtByTheLineAfterIt()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path);
        using var doc = System.Text.Json.JsonDocument.Parse(lines[2]);
        var prev = doc.RootElement.GetProperty("prev").GetString()!;
        // The forger edits line 3 and recomputes its hash, so the line itself looks fine…
        lines[2] = AuditChain.BuildLine(3, prev, "{\"kind\":\"tool.result\",\"data\":{\"ok\":false,\"n\":2}}");
        File.WriteAllLines(path, lines);

        var result = AuditChainVerifier.Verify(path);

        // …but line 4 still points at the original line 3.
        Assert.False(result.Ok);
        Assert.Equal(3, result.Failure!.Line);
        Assert.Contains("line 4", result.Failure.Problem);
    }

    [Fact]
    public void ARewrittenLastLine_ChangesTheHeadHash()
    {
        var (path, head) = Write();
        var lines = File.ReadAllLines(path);
        using var doc = System.Text.Json.JsonDocument.Parse(lines[^1]);
        lines[^1] = AuditChain.BuildLine(6, doc.RootElement.GetProperty("prev").GetString()!, "{\"kind\":\"run.end\",\"data\":{\"status\":\"cancelled\"}}");
        File.WriteAllLines(path, lines);

        var result = AuditChainVerifier.Verify(path);

        // Nothing follows the last line, so only the head hash (kept elsewhere, see --head) gives it away.
        Assert.True(result.Ok);
        Assert.NotEqual(head, result.HeadHash);
    }

    [Fact]
    public void ARemovedLine_Fails()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(path, lines);

        var result = AuditChainVerifier.Verify(path);

        Assert.False(result.Ok);
        Assert.Equal(2, result.Failure!.Line);
    }

    [Fact]
    public void SwappedLines_Fail()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path);
        (lines[1], lines[2]) = (lines[2], lines[1]);
        File.WriteAllLines(path, lines);

        var result = AuditChainVerifier.Verify(path);

        Assert.False(result.Ok);
        Assert.Equal(2, result.Failure!.Line);
    }

    [Fact]
    public void ADuplicatedLine_Fails()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path).ToList();
        lines.Insert(2, lines[1]);
        File.WriteAllLines(path, lines);

        Assert.False(AuditChainVerifier.Verify(path).Ok);
    }

    [Fact]
    public void AnEntryAppendedAfterTheEnd_Fails()
    {
        var (path, _) = Write();
        using (var w = new AuditChainWriter(path))
            w.Append("{\"kind\":\"tool.call\",\"data\":{\"tool\":\"run_command\"}}");

        var result = AuditChainVerifier.Verify(path);

        Assert.False(result.Ok);
        Assert.Equal(7, result.Failure!.Line);
    }

    [Fact]
    public void ACutOffTail_StillChains_ButWarns()
    {
        var (path, _) = Write();
        File.WriteAllLines(path, File.ReadAllLines(path).Take(4));

        var result = AuditChainVerifier.Verify(path);

        Assert.True(result.Ok);
        Assert.False(result.HasEndRecord);
        Assert.Contains(result.Warnings, w => w.Contains("run.end"));
    }

    [Fact]
    public void ARunThatWasNeverClosed_Warns()
    {
        var (path, _) = Write(close: false);

        var result = AuditChainVerifier.Verify(path);

        Assert.True(result.Ok);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ABlankLineInTheMiddle_Fails_ButTrailingBlankLinesAreFine()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path).ToList();

        File.WriteAllLines(path, lines.Concat(new[] { "", "" }));
        Assert.True(AuditChainVerifier.Verify(path).Ok);

        lines.Insert(2, "");
        File.WriteAllLines(path, lines);
        Assert.False(AuditChainVerifier.Verify(path).Ok);
    }

    [Fact]
    public void WindowsLineEndings_AreTolerated()
    {
        var (path, _) = Write();
        File.WriteAllText(path, string.Join("\r\n", File.ReadAllLines(path)) + "\r\n");

        Assert.True(AuditChainVerifier.Verify(path).Ok);
    }

    [Fact]
    public void NotJson_IsReportedWithItsLine()
    {
        var (path, _) = Write();
        var lines = File.ReadAllLines(path);
        lines[3] = "this was typed by hand";
        File.WriteAllLines(path, lines);

        var result = AuditChainVerifier.Verify(path);

        Assert.Equal(4, result.Failure!.Line);
    }

    [Fact]
    public void ReopeningAFile_ContinuesTheChain()
    {
        var path = PathOf();
        using (var w = new AuditChainWriter(path)) w.Append("{\"kind\":\"a\"}");
        using (var w = new AuditChainWriter(path))
        {
            Assert.Equal(1, w.Count);
            w.Append("{\"kind\":\"b\"}");
        }

        var result = AuditChainVerifier.Verify(path);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Lines);
    }

    [Fact]
    public void AMultiLineEntry_IsRejected()
    {
        using var w = new AuditChainWriter(PathOf());

        Assert.Throws<ArgumentException>(() => w.Append("{\"kind\":\n\"a\"}"));
    }

    [Fact]
    public void AMissingFile_IsAFailure_NotAnException()
    {
        var result = AuditChainVerifier.Verify(PathOf("nope.jsonl"));

        Assert.False(result.Ok);
    }

    [Fact]
    public void AnEmptyFile_HasNoEntries_AndWarns()
    {
        File.WriteAllText(PathOf(), "");

        var result = AuditChainVerifier.Verify(PathOf());

        Assert.True(result.Ok);
        Assert.Equal(0, result.Lines);
        Assert.NotEmpty(result.Warnings);
    }
}
