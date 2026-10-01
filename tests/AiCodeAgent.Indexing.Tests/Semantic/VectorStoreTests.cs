using AiCodeAgent.Core.Models;
using AiCodeAgent.Indexing.Semantic;

namespace AiCodeAgent.Indexing.Tests.Semantic;

public class VectorStoreTests
{
    private static CodeChunk MakeChunk(string id, string file, float[] embedding) => new()
    {
        Id = id,
        FilePath = file,
        StartLine = 1,
        EndLine = 10,
        Language = "csharp",
        Content = "some code",
        Embedding = embedding
    };

    [Fact]
    public void Upsert_AddsEntry()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0, 0 }));

        Assert.Equal(1, store.Count);
        Assert.True(store.Contains("c1"));
    }

    [Fact]
    public void Upsert_NullEmbedding_DoesNotAdd()
    {
        var store = new VectorStore();
        var chunk = MakeChunk("c1", "a.cs", new float[] { 1, 0, 0 });
        chunk.Embedding = null;
        store.Upsert(chunk);

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Upsert_SameId_UpdatesEntry()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0, 0 }));
        store.Upsert(MakeChunk("c1", "b.cs", new float[] { 0, 1, 0 }));

        Assert.Equal(1, store.Count);
        Assert.Equal("b.cs", store.AllChunks.First().FilePath);
    }

    [Fact]
    public void UpsertRange_AddsAll()
    {
        var store = new VectorStore();
        store.UpsertRange(new[]
        {
            MakeChunk("c1", "a.cs", new float[] { 1, 0 }),
            MakeChunk("c2", "b.cs", new float[] { 0, 1 })
        });

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0 }));
        store.Remove("c1");

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void RemoveFile_DeletesAllForFile()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0 }));
        store.Upsert(MakeChunk("c2", "a.cs", new float[] { 0, 1 }));
        store.Upsert(MakeChunk("c3", "b.cs", new float[] { 1, 1 }));
        store.RemoveFile("a.cs");

        Assert.Equal(1, store.Count);
        Assert.True(store.Contains("c3"));
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0 }));
        store.Upsert(MakeChunk("c2", "b.cs", new float[] { 0, 1 }));
        store.Clear();

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Search_ReturnsTopKByCosineSimilarity()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0, 0 }));
        store.Upsert(MakeChunk("c2", "b.cs", new float[] { 0, 1, 0 }));
        store.Upsert(MakeChunk("c3", "c.cs", new float[] { 1, 1, 0 }));

        // Query close to c1
        var results = store.Search(new float[] { 1, 0.1f, 0 }, 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("c1", results[0].Chunk.Id);
        Assert.True(results[0].Score >= results[1].Score);
    }

    [Fact]
    public void Search_EmptyStore_ReturnsEmpty()
    {
        var store = new VectorStore();
        var results = store.Search(new float[] { 1, 0 }, 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Search_TopKGreaterThanCount_ReturnsAll()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0 }));
        store.Upsert(MakeChunk("c2", "b.cs", new float[] { 0, 1 }));

        var results = store.Search(new float[] { 1, 0 }, 10);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_ResultsSortedByScoreDescending()
    {
        var store = new VectorStore();
        store.Upsert(MakeChunk("c1", "a.cs", new float[] { 1, 0, 0 }));
        store.Upsert(MakeChunk("c2", "b.cs", new float[] { 0, 1, 0 }));
        store.Upsert(MakeChunk("c3", "c.cs", new float[] { 0.9f, 0.1f, 0 }));

        var results = store.Search(new float[] { 1, 0, 0 }, 3);

        for (var i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Score >= results[i].Score);
        }
    }
}