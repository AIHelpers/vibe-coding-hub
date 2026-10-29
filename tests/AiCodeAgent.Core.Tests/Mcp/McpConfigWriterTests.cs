using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Mcp;
using Xunit;

namespace AiCodeAgent.Core.Tests.Mcp;

/// <summary>E6-S3: the writer behind Settings → MCP servers keeps the files intact and never puts a secret in the project file.</summary>
public class McpConfigWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-writer-" + Guid.NewGuid().ToString("N"));
    private readonly McpConfigStore _store;

    public McpConfigWriterTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new McpConfigStore(Path.Combine(_dir, "user", "mcp.json"), Path.Combine(_dir, "proj", ".aiagent", "mcp.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static McpServerConfig GitHub() => McpPresets.Find("github")!.ToConfig();

    [Fact]
    public void RoundTrip_KeepsCommandArgsAndEnvReferences()
    {
        _store.Upsert(McpScope.Global, GitHub());

        var back = Assert.Single(_store.Read(McpScope.Global));
        Assert.Equal("github", back.Name);
        Assert.Equal("npx", back.Command);
        Assert.Equal(GitHub().Args, back.Args);
        Assert.Equal("${env:GITHUB_TOKEN}", back.Env["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Equal(McpScope.Global, back.Scope);
        Assert.Contains("${env:GITHUB_TOKEN}", File.ReadAllText(_store.FileOf(McpScope.Global)!));
    }

    [Fact]
    public void Upsert_PreservesOtherServersAndUnknownKeys()
    {
        var file = _store.FileOf(McpScope.Global)!;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "servers": { "other": { "command": "foo", "custom": 7 } }, "extra": true }""");

        _store.Upsert(McpScope.Global, GitHub());

        var text = File.ReadAllText(file);
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("extra").GetBoolean());
        Assert.Equal(7, doc.RootElement.GetProperty("servers").GetProperty("other").GetProperty("custom").GetInt32());
        Assert.Equal(new[] { "other", "github" }, _store.Read(McpScope.Global).Select(s => s.Name).ToArray());
    }

    [Fact]
    public void ProjectFile_RefusesSecrets()
    {
        var token = new McpServerConfig { Name = "api", Url = "https://example.com/mcp", Token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" };
        Assert.Throws<McpSecretException>(() => _store.Upsert(McpScope.Project, token));

        var env = GitHub() with { Env = new() { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } };
        Assert.Throws<McpSecretException>(() => _store.Upsert(McpScope.Project, env));

        var pg = new McpServerConfig { Name = "pg", Command = "npx", Args = new() { "-y", "@modelcontextprotocol/server-postgres", "postgresql://user:hunter2@localhost/db" } };
        Assert.Throws<McpSecretException>(() => _store.Upsert(McpScope.Project, pg));

        Assert.False(File.Exists(_store.FileOf(McpScope.Project)!));
    }

    [Fact]
    public void ProjectFile_AcceptsReferences_AndContainsNoSecret()
    {
        _store.Upsert(McpScope.Project, GitHub());
        _store.Upsert(McpScope.Project, new McpServerConfig { Name = "remote", Url = "https://example.com/mcp", Token = "${env:REMOTE_TOKEN}" });

        var text = File.ReadAllText(_store.FileOf(McpScope.Project)!);
        Assert.Contains("${env:GITHUB_TOKEN}", text);
        Assert.Contains("${env:REMOTE_TOKEN}", text);
        Assert.DoesNotContain("ghp_", text);
        Assert.All(_store.Read(McpScope.Project), s => Assert.Empty(McpSecrets.Find(s)));
    }

    [Fact]
    public void EnableDisableAndRemove()
    {
        _store.Upsert(McpScope.Global, GitHub());
        _store.Upsert(McpScope.Global, McpPresets.Find("fetch")!.ToConfig());

        Assert.True(_store.SetEnabled(McpScope.Global, "github", false));
        Assert.False(_store.Read(McpScope.Global).Single(s => s.Name == "github").Enabled);
        Assert.True(_store.SetEnabled(McpScope.Global, "github", true));
        Assert.True(_store.Read(McpScope.Global).Single(s => s.Name == "github").Enabled);
        Assert.DoesNotContain("enabled", File.ReadAllText(_store.FileOf(McpScope.Global)!));

        Assert.True(_store.Remove(McpScope.Global, "github"));
        Assert.False(_store.Remove(McpScope.Global, "github"));
        Assert.Equal("fetch", Assert.Single(_store.Read(McpScope.Global)).Name);
    }

    [Fact]
    public void Rename_ReplacesTheOldEntry()
    {
        _store.Upsert(McpScope.Global, GitHub());
        _store.Upsert(McpScope.Global, GitHub() with { Name = "gh" }, replacingName: "github");

        Assert.Equal("gh", Assert.Single(_store.Read(McpScope.Global)).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a__b")]
    [InlineData("bad/name")]
    public void InvalidNames_AreRejected(string name) => Assert.NotNull(McpConfigStore.ValidateName(name));

    [Theory]
    [InlineData("github")]
    [InlineData("my-server_2")]
    public void ValidNames_AreAccepted(string name) => Assert.Null(McpConfigStore.ValidateName(name));

    [Fact]
    public void Env_ExpandsReferences_AndReportsMissingOnes()
    {
        var config = GitHub();
        var set = McpEnv.Expand(config, n => n == "GITHUB_TOKEN" ? "tok" : null);
        Assert.Equal("tok", set.Env["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Empty(set.MissingEnv);

        var unset = McpEnv.Expand(config, _ => null);
        Assert.Equal(new[] { "GITHUB_TOKEN" }, unset.MissingEnv.ToArray());
        // The original (what is written to disk) keeps the reference.
        Assert.Equal("${env:GITHUB_TOKEN}", config.Env["GITHUB_PERSONAL_ACCESS_TOKEN"]);
    }

    [Fact]
    public void Secrets_DoNotFlagOrdinaryValues()
    {
        var plain = new McpServerConfig { Name = "t", Command = "npx", Args = new() { "-y", "task-runner", "--port", "8080" } };
        Assert.Empty(McpSecrets.Find(plain));
    }

    [Fact]
    public void Presets_ProduceNoSecrets()
    {
        Assert.NotEmpty(McpPresets.All);
        Assert.All(McpPresets.All, p => Assert.Empty(McpSecrets.Find(p.ToConfig("/work/app"))));
        Assert.Contains(McpPresets.All, p => p.Id == "github");
        Assert.Contains("/work/app", McpPresets.Find("filesystem")!.ToConfig("/work/app").Args);
    }

    [Fact]
    public void ToolPatterns_MatchMcpServers()
    {
        Assert.True(ToolPatterns.IsMatch("mcp:github/*", "github__create_issue"));
        Assert.True(ToolPatterns.IsMatch("mcp:github/create_issue", "github__create_issue"));
        Assert.False(ToolPatterns.IsMatch("mcp:github/create_issue", "github__list_issues"));
        Assert.False(ToolPatterns.IsMatch("mcp:github/*", "fetch__fetch"));
        Assert.True(ToolPatterns.IsMatch("read_file", "read_file"));
        Assert.True(ToolPatterns.MatchesAny(new[] { "x", "mcp:fetch/*" }, "fetch__fetch"));
        Assert.Equal("mcp:github/*", ToolPatterns.McpPattern("github"));
    }

    [Fact]
    public void CharacterToolEdit_AddsAndRemovesThePattern()
    {
        const string character = "---\nid: dev\nname: Dev\n---\nBody\n";
        var added = CharacterToolEdit.SetAllowed(character, "mcp:github/*", true);

        Assert.True(CharacterToolEdit.IsAllowed(added, "mcp:github/*"));
        Assert.Contains("Body", added);
        Assert.Contains("id: dev", added);

        var removed = CharacterToolEdit.SetAllowed(added, "mcp:github/*", false);
        Assert.False(CharacterToolEdit.IsAllowed(removed, "mcp:github/*"));
        Assert.Contains("Body", removed);
    }
}
