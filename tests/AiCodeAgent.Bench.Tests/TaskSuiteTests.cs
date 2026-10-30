namespace AiCodeAgent.Bench.Tests;

public class TaskSuiteTests
{
    private const string Good = """
        {
          // comments are allowed
          "name": "demo",
          "tasks": [
            { "id": "t1", "language": "csharp", "prompt": "Fix it", "testCommand": "dotnet test", "repo": { "path": "repo1" } },
            { "id": "t2", "prompt": "Fix that", "testCommand": "pytest", "repo": { "git": "https://example.invalid/x.git", "commit": "abc123" } }
          ]
        }
        """;

    [Fact]
    public void AGoodFile_Loads()
    {
        var suite = TaskSuiteLoader.Parse(Good, "/base");
        Assert.Equal("demo", suite.Name);
        Assert.Equal(2, suite.Tasks.Count);
        Assert.Equal("repo1", suite.Tasks[0].Repo.Path);
        Assert.Equal("abc123", suite.Tasks[1].Repo.Commit);
    }

    [Theory]
    [InlineData("""{ "tasks": [] }""", "no tasks")]
    [InlineData("""{ "tasks": [ { "prompt": "p", "testCommand": "t", "repo": { "path": "r" } } ] }""", "'id' is required")]
    [InlineData("""{ "tasks": [ { "id": "a b", "prompt": "p", "testCommand": "t", "repo": { "path": "r" } } ] }""", "letters, digits")]
    [InlineData("""{ "tasks": [ { "id": "a", "testCommand": "t", "repo": { "path": "r" } } ] }""", "'prompt' is required")]
    [InlineData("""{ "tasks": [ { "id": "a", "prompt": "p", "repo": { "path": "r" } } ] }""", "'testCommand' is required")]
    [InlineData("""{ "tasks": [ { "id": "a", "prompt": "p", "testCommand": "t" } ] }""", "'path' or 'git'")]
    [InlineData("""{ "tasks": [ { "id": "a", "prompt": "p", "testCommand": "t", "repo": { "git": "u" } } ] }""", "'repo.commit' is required")]
    [InlineData("""{ "tasks": [ { "id": "a", "prompt": "p", "testCommand": "t", "repo": { "path": "r" }, "surprise": 1 } ] }""", "not valid")]
    public void BadFiles_AreRejectedWithAClearMessage(string json, string expected)
    {
        var ex = Assert.Throws<BenchException>(() => TaskSuiteLoader.Parse(json, "/base"));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        const string json = """
            { "tasks": [
              { "id": "a", "prompt": "p", "testCommand": "t", "repo": { "path": "r" } },
              { "id": "A", "prompt": "p", "testCommand": "t", "repo": { "path": "r" } } ] }
            """;
        Assert.Contains("used twice", Assert.Throws<BenchException>(() => TaskSuiteLoader.Parse(json, "/base")).Message);
    }

    [Fact]
    public void Filter_KeepsTheListed_AndRejectsUnknownIds()
    {
        var suite = TaskSuiteLoader.Parse(Good, "/base");
        Assert.Single(TaskSuiteLoader.Filter(suite, new[] { "t2" }).Tasks);
        Assert.Equal(2, TaskSuiteLoader.Filter(suite, Array.Empty<string>()).Tasks.Count);
        Assert.Throws<BenchException>(() => TaskSuiteLoader.Filter(suite, new[] { "nope" }));
    }
}
