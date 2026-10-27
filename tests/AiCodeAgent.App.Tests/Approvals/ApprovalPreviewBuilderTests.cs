using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Shell;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.App.Tests.Approvals;

/// <summary>
/// E2-S5: the approval dialog shows what will happen. The real tools compute a dry-run preview (nothing is written),
/// and <see cref="ApprovalPreviewBuilder"/> turns it into a compact unified diff with the right line numbers.
/// </summary>
public sealed class ApprovalPreviewBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "approval-preview-" + Guid.NewGuid().ToString("N")[..8]);

    public ApprovalPreviewBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private AgentExecutionContext Context => new() { WorkingDirectory = _dir, SessionId = "s" };

    private static ILogger<T> Log<T>() => Substitute.For<ILogger<T>>();

    private string WriteLines(string name, int count, Func<int, string>? line = null)
    {
        var text = string.Join("\n", Enumerable.Range(1, count).Select(i => line?.Invoke(i) ?? $"line {i}")) + "\n";
        File.WriteAllText(Path.Combine(_dir, name), text);
        return text;
    }

    private static ToolCall Call(string name, Dictionary<string, object?> args) => new() { Name = name, Arguments = args };

    private static async Task<ApprovalPreviewViewModel> PreviewAsync(IPreviewableTool tool, ToolCall call, AgentExecutionContext context)
    {
        var preview = await tool.PreviewAsync(call, context);
        return ApprovalPreviewBuilder.Build(call, preview);
    }

    private static List<UnifiedDiffLine> Changes(ApprovalPreviewViewModel p) =>
        p.Lines.Where(l => l.Kind is UnifiedDiffLineKind.Removed or UnifiedDiffLineKind.Added).ToList();

    [Fact]
    public async Task EditFile_ShowsRedGreenDiff_AtTheRightLine_AndWritesNothing()
    {
        var original = WriteLines("Todo.cs", 12);
        var call = Call("edit_file", new() { ["path"] = "Todo.cs", ["old_string"] = "line 7", ["new_string"] = "line seven" });

        var p = await PreviewAsync(new EditFileTool(Log<EditFileTool>()), call, Context);

        Assert.Equal(ApprovalPreviewKind.FileDiff, p.Kind);
        Assert.False(p.HasError);
        Assert.Equal("Todo.cs", p.FilePath);
        Assert.Equal("Edit file", p.Title);
        Assert.Equal((1, 1), (p.AddedCount, p.RemovedCount));
        Assert.Equal("+1 −1", p.ChangeSummary);

        var changes = Changes(p);
        Assert.Equal(2, changes.Count);
        Assert.Equal((UnifiedDiffLineKind.Removed, (int?)7, (int?)null, "line 7"), (changes[0].Kind, changes[0].OldNumber, changes[0].NewNumber, changes[0].Text));
        Assert.Equal((UnifiedDiffLineKind.Added, (int?)null, (int?)7, "line seven"), (changes[1].Kind, changes[1].OldNumber, changes[1].NewNumber, changes[1].Text));
        Assert.Equal("−", changes[0].Marker);
        Assert.Equal("+", changes[1].Marker);
        Assert.NotNull(changes[1].Segments); // word-level highlight ("seven") from the diff viewer's WordDiff
        Assert.Contains(changes[1].Segments!, s => s.Changed && s.Text.Contains("seven"));

        // Three lines of context on each side, numbered on both sides.
        var context = p.Lines.Where(l => l.Kind == UnifiedDiffLineKind.Context).ToList();
        Assert.Equal(new[] { 4, 5, 6, 8, 9, 10 }, context.Select(l => l.OldNumber!.Value));
        Assert.All(context, l => Assert.Equal(l.OldNumber, l.NewNumber));

        Assert.Equal(original, File.ReadAllText(Path.Combine(_dir, "Todo.cs"))); // dry run
    }

    [Fact]
    public async Task EditFile_WhenTheTextIsMissing_ShowsWhyItWouldFail()
    {
        WriteLines("a.cs", 3);
        var call = Call("edit_file", new() { ["path"] = "a.cs", ["old_string"] = "nope", ["new_string"] = "x" });

        var p = await PreviewAsync(new EditFileTool(Log<EditFileTool>()), call, Context);

        Assert.True(p.HasError);
        Assert.Contains("Text not found", p.Error);
        Assert.Equal(ApprovalPreviewKind.Arguments, p.Kind); // nothing to diff: raw arguments
        Assert.Contains("old_string: nope", p.RawArguments);
    }

    [Fact]
    public async Task EditFile_OnACrlfFile_MatchesLikeTheRealEdit()
    {
        File.WriteAllText(Path.Combine(_dir, "win.cs"), "a\r\nb\r\nc\r\n");
        var call = Call("edit_file", new() { ["path"] = "win.cs", ["old_string"] = "a\nb", ["new_string"] = "a\nB" });

        var p = await PreviewAsync(new EditFileTool(Log<EditFileTool>()), call, Context);

        Assert.False(p.HasError);
        var changes = Changes(p);
        Assert.Equal(new[] { "b", "B" }, changes.Select(c => c.Text));
        Assert.Equal(new int?[] { 2, null }, changes.Select(c => c.OldNumber));
    }

    [Fact]
    public async Task MultiEdit_ShowsEachHunk_WithItsLineNumbers()
    {
        WriteLines("svc.cs", 29); // 29, not 30: "line 3" must not also match "line 30"
        var edits = JsonDocument.Parse("""
            [ { "old_string": "line 3",  "new_string": "line three" },
              { "old_string": "line 25", "new_string": "line twenty-five" } ]
            """).RootElement;
        var call = Call("multi_edit", new() { ["path"] = "svc.cs", ["edits"] = edits });

        var p = await PreviewAsync(new MultiEditTool(Log<MultiEditTool>()), call, Context);

        Assert.False(p.HasError, p.Error);
        Assert.Equal((2, 2), (p.AddedCount, p.RemovedCount));
        Assert.Equal(2, p.Lines.Count(l => l.IsHunkHeader));
        Assert.Contains(p.Lines, l => l.IsGap); // the unchanged middle is collapsed
        var changes = Changes(p);
        Assert.Equal(new int?[] { 3, null, 25, null }, changes.Select(c => c.OldNumber));
        Assert.Equal(new int?[] { null, 3, null, 25 }, changes.Select(c => c.NewNumber));
        Assert.Equal("line 3\n", File.ReadAllLines(Path.Combine(_dir, "svc.cs"))[2] + "\n");
    }

    [Fact]
    public async Task WriteFile_NewFile_ShowsEveryLineAsAdded_AndCreatesNothing()
    {
        var call = Call("write_file", new() { ["path"] = "src/New.cs", ["content"] = "class New\n{\n}\n" });

        var p = await PreviewAsync(new WriteFileTool(Log<WriteFileTool>()), call, Context);

        Assert.True(p.IsNewFile);
        Assert.Equal("New file · 3 lines", p.ChangeSummary);
        var changes = Changes(p);
        Assert.All(changes, c => Assert.Equal(UnifiedDiffLineKind.Added, c.Kind));
        Assert.Equal(new int?[] { 1, 2, 3 }, changes.Select(c => c.NewNumber));
        Assert.False(File.Exists(Path.Combine(_dir, "src", "New.cs")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "src")));
    }

    [Fact]
    public async Task WriteFile_Overwrite_DiffsAgainstTheCurrentContent()
    {
        WriteLines("cfg.json", 3);
        var call = Call("write_file", new() { ["path"] = "cfg.json", ["content"] = "line 1\nline 2 changed\nline 3\n" });

        var p = await PreviewAsync(new WriteFileTool(Log<WriteFileTool>()), call, Context);

        Assert.False(p.IsNewFile);
        Assert.Equal(new[] { "line 2", "line 2 changed" }, Changes(p).Select(c => c.Text));
    }

    [Fact]
    public async Task ApplyPatch_ShowsTheResultOfThePatch()
    {
        WriteLines("p.txt", 6);
        var patch = "@@ -2,3 +2,3 @@\n line 2\n-line 3\n+line 3 patched\n line 4\n";
        var call = Call("apply_patch", new() { ["path"] = "p.txt", ["patch"] = patch });

        var p = await PreviewAsync(new ApplyPatchTool(Log<ApplyPatchTool>()), call, Context);

        Assert.False(p.HasError, p.Error);
        var changes = Changes(p);
        Assert.Equal((UnifiedDiffLineKind.Removed, (int?)3), (changes[0].Kind, changes[0].OldNumber));
        Assert.Equal((UnifiedDiffLineKind.Added, (int?)3, "line 3 patched"), (changes[1].Kind, changes[1].NewNumber, changes[1].Text));
        Assert.Equal("line 3", File.ReadAllLines(Path.Combine(_dir, "p.txt"))[2]);
    }

    [Fact]
    public async Task ApplyPatch_ThatDoesNotApply_ShowsTheError()
    {
        WriteLines("p.txt", 3);
        var call = Call("apply_patch", new() { ["path"] = "p.txt", ["patch"] = "@@ -1,1 +1,1 @@\n-nothing like this\n+x\n" });

        var p = await PreviewAsync(new ApplyPatchTool(Log<ApplyPatchTool>()), call, Context);

        Assert.True(p.HasError);
        Assert.Contains("No changes were written", p.Error);
    }

    [Fact]
    public async Task LongDiff_ShowsTheFirst30Lines_UntilShowAll()
    {
        var call = Call("write_file", new() { ["path"] = "big.txt", ["content"] = string.Join("\n", Enumerable.Range(1, 100)) });

        var p = await PreviewAsync(new WriteFileTool(Log<WriteFileTool>()), call, Context);

        Assert.Equal(101, p.Lines.Count); // "@@ -0,0 +1,100 @@" + 100 added lines
        Assert.True(p.HasMoreLines);
        Assert.Equal(ApprovalPreviewBuilder.MaxCollapsedLines, p.VisibleLines.Count);
        Assert.Equal("Show all 101 lines", p.ShowAllText);

        p.ToggleShowAllCommand.Execute(null);
        Assert.Equal(101, p.VisibleLines.Count);
        Assert.Equal("Show less", p.ShowAllText);
    }

    [Fact]
    public async Task ExecuteCommand_ShowsCommand_WorkingDirectory_AndSandboxOff()
    {
        var call = Call("execute_command", new() { ["command"] = "dotnet test" });

        var p = await PreviewAsync(new ExecuteCommandTool(Log<ExecuteCommandTool>()), call, Context);

        Assert.Equal(ApprovalPreviewKind.Command, p.Kind);
        Assert.Equal("dotnet test", p.CommandText);
        Assert.Equal(Path.GetFullPath(_dir).TrimEnd(Path.DirectorySeparatorChar), p.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar));
        Assert.False(p.IsSandboxed);
        Assert.Contains("off", p.SandboxText);
    }

    [Fact]
    public async Task ExecuteCommand_WithTheDockerSandboxOn_SaysSo()
    {
        var sandbox = Substitute.For<ICommandSandbox>();
        sandbox.Enabled.Returns(true);
        sandbox.Description.Returns("docker (image ubuntu:24.04, network off)");
        var call = Call("execute_command", new() { ["command"] = "npm test" });

        var p = await PreviewAsync(new ExecuteCommandTool(Log<ExecuteCommandTool>(), sandbox), call, Context);

        Assert.True(p.IsSandboxed);
        Assert.Equal("Docker sandbox: on (docker (image ubuntu:24.04, network off))", p.SandboxText);
    }

    [Fact]
    public void NoPreview_FallsBackToReadableArguments()
    {
        var call = Call("web_fetch", new() { ["url"] = "https://example.com", ["max"] = 5 });

        var p = ApprovalPreviewBuilder.Build(call, null);

        Assert.Equal(ApprovalPreviewKind.Arguments, p.Kind);
        Assert.Equal("url: https://example.com" + Environment.NewLine + "max: 5", p.RawArguments);
        Assert.False(p.HasError);
    }
}
