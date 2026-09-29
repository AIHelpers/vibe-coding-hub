using System.Text.Json;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Tools.Tests.FileSystem;

public class UnifiedPatchApplierTests
{
    private const string Original = "line1\nline2\nline3\nline4\nline5\n";

    [Fact]
    public void Apply_ReplacesLine()
    {
        var patch = "@@ -2,3 +2,3 @@\n line2\n-line3\n+LINE3\n line4\n";
        var result = UnifiedPatchApplier.Apply(Original, patch, out var error);
        Assert.Equal(string.Empty, error);
        Assert.Equal("line1\nline2\nLINE3\nline4\nline5\n", result);
    }

    [Fact]
    public void Apply_IgnoresFileHeaders()
    {
        var patch = "--- a/x.txt\n+++ b/x.txt\n@@ -1,2 +1,3 @@\n line1\n+inserted\n line2\n";
        Assert.Equal("line1\ninserted\nline2\nline3\nline4\nline5\n", UnifiedPatchApplier.Apply(Original, patch, out _));
    }

    [Fact]
    public void Apply_FindsHunkWhenLineNumbersAreWrong()
    {
        var patch = "@@ -40,3 +40,3 @@\n line3\n-line4\n+LINE4\n line5\n";
        Assert.Equal("line1\nline2\nline3\nLINE4\nline5\n", UnifiedPatchApplier.Apply(Original, patch, out _));
    }

    [Fact]
    public void Apply_MultipleHunks_TrackLineOffset()
    {
        var patch = "@@ -1,2 +1,3 @@\n line1\n+extra\n line2\n@@ -4,2 +5,2 @@\n line4\n-line5\n+LINE5\n";
        Assert.Equal("line1\nextra\nline2\nline3\nline4\nLINE5\n", UnifiedPatchApplier.Apply(Original, patch, out _));
    }

    [Fact]
    public void Apply_ReturnsNull_WhenContextDoesNotMatch()
    {
        var patch = "@@ -2,2 +2,2 @@\n nope\n-line3\n+x\n";
        Assert.Null(UnifiedPatchApplier.Apply(Original, patch, out var error));
        Assert.Contains("Hunk 1", error);
    }

    [Fact]
    public void Apply_PreservesCrlf()
    {
        var crlf = Original.Replace("\n", "\r\n");
        var patch = "@@ -2,3 +2,3 @@\n line2\n-line3\n+LINE3\n line4\n";
        var result = UnifiedPatchApplier.Apply(crlf, patch, out _);
        Assert.Equal("line1\r\nline2\r\nLINE3\r\nline4\r\nline5\r\n", result);
    }

    [Fact]
    public void Apply_RejectsPatchWithoutHunks()
    {
        Assert.Null(UnifiedPatchApplier.Apply(Original, "just some text", out var error));
        Assert.Contains("no hunks", error);
    }
}

public class MultiEditToolTests : TempDirTestBase
{
    private readonly MultiEditTool _tool = new(NullLogger<MultiEditTool>.Instance);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task AppliesAllEditsInOrder()
    {
        WriteFile("f.txt", "alpha beta gamma");
        var edits = Json("""[{"old_string":"alpha","new_string":"A"},{"old_string":"A beta","new_string":"AB"}]""");

        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "f.txt", ["edits"] = edits }), Context());

        Assert.False(result.IsError, result.Content);
        Assert.Equal("AB gamma", ReadFile("f.txt"));
    }

    [Fact]
    public async Task IsAtomic_WhenOneEditFails()
    {
        WriteFile("f.txt", "alpha beta");
        var edits = Json("""[{"old_string":"alpha","new_string":"A"},{"old_string":"missing","new_string":"x"}]""");

        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "f.txt", ["edits"] = edits }), Context());

        Assert.True(result.IsError);
        Assert.Contains("Edit 2", result.Content);
        Assert.Equal("alpha beta", ReadFile("f.txt"));
    }

    [Fact]
    public async Task RejectsAmbiguousMatchUnlessReplaceAll()
    {
        WriteFile("f.txt", "x x x");
        var ambiguous = Json("""[{"old_string":"x","new_string":"y"}]""");
        var all = Json("""[{"old_string":"x","new_string":"y","replace_all":true}]""");

        Assert.True((await _tool.ExecuteAsync(Call(new() { ["path"] = "f.txt", ["edits"] = ambiguous }), Context())).IsError);
        Assert.Equal("x x x", ReadFile("f.txt"));

        Assert.False((await _tool.ExecuteAsync(Call(new() { ["path"] = "f.txt", ["edits"] = all }), Context())).IsError);
        Assert.Equal("y y y", ReadFile("f.txt"));
    }

    [Fact]
    public async Task RefusesInReadOnlyMode()
    {
        WriteFile("f.txt", "a");
        var edits = Json("""[{"old_string":"a","new_string":"b"}]""");
        var result = await _tool.ExecuteAsync(Call(new() { ["path"] = "f.txt", ["edits"] = edits }), Context(readOnly: true));
        Assert.True(result.IsError);
        Assert.Equal("a", ReadFile("f.txt"));
    }

    [Fact]
    public async Task PatchTool_AppliesHunkToFile()
    {
        WriteFile("p.txt", "one\ntwo\nthree\n");
        var tool = new ApplyPatchTool(NullLogger<ApplyPatchTool>.Instance);
        var result = await tool.ExecuteAsync(
            Call(new() { ["path"] = "p.txt", ["patch"] = "@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n" }), Context());
        Assert.False(result.IsError, result.Content);
        Assert.Equal("one\nTWO\nthree\n", ReadFile("p.txt"));
    }
}
