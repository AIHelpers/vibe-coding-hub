using System;
using System.IO;
using System.Linq;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.PreviewPane;

public class SourceIdResolverTests
{
    private readonly SourceIdResolver _resolver = new();

    [Fact]
    public void Resolve_ValidSourceId_ReturnsSpan()
    {
        var span = _resolver.Resolve("src:Foo.cs:12-18");
        Assert.NotNull(span);
        Assert.Equal("Foo.cs", span!.Value.FilePath);
        Assert.Equal(12, span.Value.StartLine);
        Assert.Equal(18, span.Value.EndLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("src:Foo.cs:abc-18")]
    [InlineData("src:Foo.cs:12")]
    [InlineData("Foo.cs:12-18")]
    public void Resolve_InvalidSourceId_ReturnsNull(string input)
    {
        Assert.Null(_resolver.Resolve(input));
    }

    [Fact]
    public void GetOriginalText_ReadsCorrectLines()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "line1\nline2\nline3\nline4\nline5\n");
        try
        {
            var span = new SourceSpan(path, 2, 4);
            var text = _resolver.GetOriginalText(span);
            Assert.Equal("line2\nline3\nline4", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetOriginalText_OutOfRange_ReturnsNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "only one\n");
        try
        {
            var span = new SourceSpan(path, 1, 50);
            Assert.Null(_resolver.GetOriginalText(span));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildReplacementHunk_ProducesAddedAndRemovedLines()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, new[] { "old1", "old2", "old3" });
        try
        {
            var span = new SourceSpan(path, 1, 3);
            var hunk = _resolver.BuildReplacementHunk(span, "new1\nnew2");
            Assert.Equal(path, hunk.FilePath);
            Assert.Equal(3, hunk.Lines.Count(l => l.Kind == DiffLineKind.Removed));
            Assert.Equal(2, hunk.Lines.Count(l => l.Kind == DiffLineKind.Added));
            Assert.Equal(HunkStatus.Pending, hunk.Status);
            Assert.Equal("visual-editor", hunk.AgentId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveAndBuildHunk_ValidId_ReturnsHunk()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, new[] { "a", "b" });
        try
        {
            var hunk = _resolver.ResolveAndBuildHunk($"src:{path}:1-2", "x\ny\nz");
            Assert.NotNull(hunk);
            Assert.Equal(path, hunk!.FilePath);
            Assert.Equal(3, hunk.Lines.Count(l => l.Kind == DiffLineKind.Added));
        }
        finally
        {
            File.Delete(path);
        }
    }
}