using AiCodeAgent.Core.Context;
using Xunit;

namespace AiCodeAgent.Core.Tests.Context;

public class FrontmatterTests
{
    [Fact]
    public void Parse_ReadsScalarsListsAndBody()
    {
        var doc = Frontmatter.Parse("""
            ---
            name: release-notes   # trailing comment
            description: "Write notes: from PRs"
            tags: [docs, git]
            version: 2
            disable-model-invocation: yes
            ---
            # Body
            Steps.
            """);

        Assert.True(doc.HasFrontmatter);
        Assert.Equal("release-notes", doc.GetString("name"));
        Assert.Equal("Write notes: from PRs", doc.GetString("description"));
        Assert.Equal(new[] { "docs", "git" }, doc.GetList("tags"));
        Assert.Equal(2, doc.GetInt("version"));
        Assert.True(doc.GetBool("disable-model-invocation"));
        Assert.StartsWith("# Body", doc.Body);
    }

    [Fact]
    public void Parse_ReadsBlockListsAndNestedMaps()
    {
        var doc = Frontmatter.Parse("""
            ---
            skills:
              - architecture-review
              - adr-writer  # comment
            tools: { add: [web_fetch], remove: [] }
            limits:
              add: [git]
              remove:
                - execute_command
            ---
            Body
            """);

        Assert.Equal(new[] { "architecture-review", "adr-writer" }, doc.GetList("skills"));
        var tools = doc.GetMap("tools");
        Assert.Equal(new List<string> { "web_fetch" }, tools["add"]);
        Assert.Empty((List<string>)tools["remove"]!);
        var limits = doc.GetMap("limits");
        Assert.Equal(new List<string> { "git" }, limits["add"]);
        Assert.Equal(new List<string> { "execute_command" }, limits["remove"]);
    }

    [Fact]
    public void Parse_NoFrontmatter_ReturnsWholeBody()
    {
        var doc = Frontmatter.Parse("# Just markdown\n---\nnot frontmatter");
        Assert.False(doc.HasFrontmatter);
        Assert.Equal("# Just markdown\n---\nnot frontmatter", doc.Body);
        Assert.Empty(doc.Fields);
    }

    [Fact]
    public void Parse_UnclosedFrontmatter_IsTreatedAsBody()
    {
        var doc = Frontmatter.Parse("---\nname: x\nno closing");
        Assert.False(doc.HasFrontmatter);
    }

    [Fact]
    public void Parse_HandlesCrLf()
    {
        var doc = Frontmatter.Parse("---\r\nname: a\r\ntags: [x]\r\n---\r\nBody\r\n");
        Assert.Equal("a", doc.GetString("name"));
        Assert.Equal(new[] { "x" }, doc.GetList("tags"));
        Assert.StartsWith("Body", doc.Body);
    }

    [Fact]
    public void GetList_ScalarIsSplitOnCommas()
    {
        var doc = Frontmatter.Parse("---\ntags: a, b ,c\n---\n");
        Assert.Equal(new[] { "a", "b", "c" }, doc.GetList("tags"));
    }

    [Fact]
    public void SetField_ReplacesBlockListAndPreservesEverythingElse()
    {
        var content = """
            ---
            id: alex
            # keep this comment
            skills:
              - one
              - two
            model: sonnet
            ---
            Persona body
            line 2
            """;

        var updated = Frontmatter.SetField(content, "skills", Frontmatter.FormatList(new[] { "one", "three" }));

        var doc = Frontmatter.Parse(updated);
        Assert.Equal(new[] { "one", "three" }, doc.GetList("skills"));
        Assert.Equal("sonnet", doc.GetString("model"));
        Assert.Contains("# keep this comment", updated);
        // The body keeps the file's own line endings; the raw literal above has CRLF when git
        // checks this file out on Windows, so compare line by line.
        Assert.Equal("Persona body\nline 2", doc.Body.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void SetField_AddsMissingKey()
    {
        var updated = Frontmatter.SetField("---\nname: a\n---\nBody\n", "tags", "[x]");
        var doc = Frontmatter.Parse(updated);
        Assert.Equal("a", doc.GetString("name"));
        Assert.Equal(new[] { "x" }, doc.GetList("tags"));
        Assert.Equal("Body\n", doc.Body);
    }

    [Fact]
    public void SetField_CreatesFrontmatterWhenAbsent()
    {
        var updated = Frontmatter.SetField("Body only", "name", "x");
        var doc = Frontmatter.Parse(updated);
        Assert.Equal("x", doc.GetString("name"));
        Assert.Equal("Body only", doc.Body);
    }

    [Fact]
    public void ComposeAndQuote_RoundTrip()
    {
        var text = Frontmatter.Compose(new[]
        {
            new KeyValuePair<string, string>("name", "a"),
            new KeyValuePair<string, string>("description", Frontmatter.QuoteIfNeeded("Uses: colons, # and \"quotes\"")),
            new KeyValuePair<string, string>("tags", Frontmatter.FormatList(new[] { "x", "y z" }))
        }, "Body");

        var doc = Frontmatter.Parse(text);
        Assert.Equal("Uses: colons, # and \"quotes\"", doc.GetString("description"));
        Assert.Equal(new[] { "x", "y z" }, doc.GetList("tags"));
        Assert.Equal("Body\n", doc.Body);
    }

    [Fact]
    public void SetBody_KeepsFrontmatter()
    {
        var updated = Frontmatter.SetBody("---\nname: a\n---\nold", "new body\n");
        var doc = Frontmatter.Parse(updated);
        Assert.Equal("a", doc.GetString("name"));
        Assert.Equal("new body\n", doc.Body);
    }

    [Theory]
    [InlineData("release-notes", true)]
    [InlineData("a1", true)]
    [InlineData("Release", false)]
    [InlineData("two--dashes", false)]
    [InlineData("-lead", false)]
    [InlineData("under_score", false)]
    [InlineData("", false)]
    public void LocalFileStore_IsValidName(string name, bool valid)
        => Assert.Equal(valid, LocalFileStore.IsValidName(name));

    [Fact]
    public void LocalFileStore_ToKebab()
        => Assert.Equal("alex-the-architect", LocalFileStore.ToKebab("  Alex — the Architect! "));
}
