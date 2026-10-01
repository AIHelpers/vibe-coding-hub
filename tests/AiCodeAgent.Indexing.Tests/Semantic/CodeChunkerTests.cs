using AiCodeAgent.Core.Models;
using AiCodeAgent.Indexing.Semantic;

namespace AiCodeAgent.Indexing.Tests.Semantic;

public class CodeChunkerTests
{
    [Fact]
    public void Chunk_EmptyContent_ReturnsNoChunks()
    {
        var chunker = new CodeChunker();
        var result = chunker.Chunk("test.cs", "", "csharp", 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_NullContent_ReturnsNoChunks()
    {
        var chunker = new CodeChunker();
        var result = chunker.Chunk("test.cs", null!, "csharp", 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_SmallFile_ReturnsSingleChunk()
    {
        var chunker = new CodeChunker(targetLines: 80);
        var content = "line1\nline2\nline3";
        var result = chunker.Chunk("test.cs", content, "csharp", 12345);

        Assert.Single(result);
        var chunk = result[0];
        Assert.Equal("test.cs", chunk.FilePath);
        Assert.Equal(1, chunk.StartLine);
        Assert.Equal(3, chunk.EndLine);
        Assert.Equal("csharp", chunk.Language);
        Assert.Equal(content, chunk.Content);
        Assert.Equal(12345, chunk.FileWriteTimeUtcTicks);
        Assert.False(string.IsNullOrEmpty(chunk.Id));
    }

    [Fact]
    public void Chunk_LargeFile_ProducesMultipleChunksWithOverlap()
    {
        var chunker = new CodeChunker(targetLines: 10, overlapLines: 2);
        var lines = Enumerable.Range(1, 25).Select(n => $"line{n}").ToArray();
        var content = string.Join('\n', lines);

        var result = chunker.Chunk("test.cs", content, "csharp", 0);

        Assert.True(result.Count > 1);
        // First chunk starts at line 1
        Assert.Equal(1, result[0].StartLine);
        // Chunks should have overlap - second chunk start should be before first chunk end
        Assert.True(result[1].StartLine <= result[0].EndLine,
            $"Expected overlap: second chunk start {result[1].StartLine} should be <= first chunk end {result[0].EndLine}");
    }

    [Fact]
    public void Chunk_StableId_ForSameFileAndStartLine()
    {
        var chunker = new CodeChunker();
        var content = "line1\nline2\nline3";

        var r1 = chunker.Chunk("test.cs", content, "csharp", 0);
        var r2 = chunker.Chunk("test.cs", content, "csharp", 0);

        Assert.Equal(r1[0].Id, r2[0].Id);
    }

    [Fact]
    public void Chunk_DifferentFiles_DifferentIds()
    {
        var chunker = new CodeChunker();
        var content = "line1\nline2";

        var r1 = chunker.Chunk("a.cs", content, "csharp", 0);
        var r2 = chunker.Chunk("b.cs", content, "csharp", 0);

        Assert.NotEqual(r1[0].Id, r2[0].Id);
    }

    [Fact]
    public void Chunk_RespectsMaxChars()
    {
        var chunker = new CodeChunker(targetLines: 100, maxChars: 20);
        var content = "0123456789\n0123456789\n0123456789";
        var result = chunker.Chunk("test.cs", content, "csharp", 0);

        // Each chunk should not exceed maxChars significantly
        foreach (var chunk in result)
        {
            Assert.True(chunk.Content.Length <= 25,
                $"Chunk content length {chunk.Content.Length} should be near maxChars");
        }
    }

    [Fact]
    public void Constructor_InvalidTargetLines_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeChunker(targetLines: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeChunker(targetLines: -1));
    }

    [Fact]
    public void Constructor_InvalidOverlap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeChunker(targetLines: 10, overlapLines: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeChunker(targetLines: 10, overlapLines: -1));
    }
}