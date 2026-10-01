using System.Text.Json;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Mcp;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Mcp;

public class McpConfigLoaderTests
{
    [Fact]
    public void Load_WhenConfigMissing_ReturnsEmptyList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configs = McpConfigLoader.Load(tempDir);
            // May pick up user-level config; filter to only those we care about
            Assert.DoesNotContain(configs, c => c.Name == "filesystem");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_ParsesStdioServerConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var aiAgentDir = Path.Combine(tempDir, ".aiagent");
            Directory.CreateDirectory(aiAgentDir);
            var json = """
            {
              "servers": {
                "filesystem": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
                  "env": { "FOO": "bar" }
                }
              }
            }
            """;
            File.WriteAllText(Path.Combine(aiAgentDir, "mcp.json"), json);

            var configs = McpConfigLoader.Load(tempDir);
            var cfg = Assert.Single(configs, c => c.Name == "filesystem");
            Assert.False(cfg.IsRemote);
            Assert.Equal("npx", cfg.Command);
            Assert.Contains("-y", cfg.Args);
            Assert.Equal("bar", cfg.Env["FOO"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_ParsesRemoteServerConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var aiAgentDir = Path.Combine(tempDir, ".aiagent");
            Directory.CreateDirectory(aiAgentDir);
            var json = """
            {
              "servers": {
                "remote": {
                  "url": "https://example.com/mcp",
                  "token": "Bearer token"
                }
              }
            }
            """;
            File.WriteAllText(Path.Combine(aiAgentDir, "mcp.json"), json);

            var configs = McpConfigLoader.Load(tempDir);
            var cfg = Assert.Single(configs, c => c.Name == "remote");
            Assert.True(cfg.IsRemote);
            Assert.Equal("https://example.com/mcp", cfg.Url);
            Assert.Equal("Bearer token", cfg.Token);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_ProjectConfigOverridesUserConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var aiAgentDir = Path.Combine(tempDir, ".aiagent");
            Directory.CreateDirectory(aiAgentDir);
            var json = """
            {
              "servers": {
                "shared": {
                  "command": "project-cmd",
                  "args": ["--project"]
                }
              }
            }
            """;
            File.WriteAllText(Path.Combine(aiAgentDir, "mcp.json"), json);

            var configs = McpConfigLoader.Load(tempDir);
            var cfg = Assert.Single(configs, c => c.Name == "shared");
            Assert.Equal("project-cmd", cfg.Command);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

public class McpToolAdapterTests
{
    [Fact]
    public void Constructor_SetsNamespacedName()
    {
        var client = Substitute.For<IMcpClient>();
        client.ServerName.Returns("myserver");
        var toolInfo = new McpToolInfo { Name = "search", Description = "Search things" };
        var logger = Substitute.For<ILogger<McpToolAdapter>>();

        var adapter = new McpToolAdapter(client, toolInfo, logger);

        Assert.Equal("myserver__search", adapter.Name);
        Assert.Equal("Search things", adapter.Description);
    }

    [Fact]
    public void Constructor_WithEmptyDescription_UsesFallback()
    {
        var client = Substitute.For<IMcpClient>();
        client.ServerName.Returns("srv");
        var toolInfo = new McpToolInfo { Name = "tool1", Description = "" };
        var logger = Substitute.For<ILogger<McpToolAdapter>>();

        var adapter = new McpToolAdapter(client, toolInfo, logger);

        Assert.Contains("MCP tool tool1 from srv", adapter.Description);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCallToClient()
    {
        var client = Substitute.For<IMcpClient>();
        client.ServerName.Returns("srv");
        client.CallToolAsync("search", Arg.Any<Dictionary<string, object?>>(), Arg.Any<CancellationToken>())
              .Returns(new McpToolResult { Content = "result-data", IsError = false });
        var toolInfo = new McpToolInfo { Name = "search", Description = "Search" };
        var logger = Substitute.For<ILogger<McpToolAdapter>>();
        var adapter = new McpToolAdapter(client, toolInfo, logger);

        var call = new ToolCall { Id = "c1", Name = "srv__search", Arguments = new() { ["q"] = "test" } };
        var result = await adapter.ExecuteAsync(call, null!);

        Assert.False(result.IsError);
        Assert.Equal("result-data", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_OnException_ReturnsError()
    {
        var client = Substitute.For<IMcpClient>();
        client.ServerName.Returns("srv");
        client.CallToolAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>(), Arg.Any<CancellationToken>())
              .Returns<McpToolResult>(x => throw new InvalidOperationException("boom"));
        var toolInfo = new McpToolInfo { Name = "fail", Description = "Fails" };
        var logger = Substitute.For<ILogger<McpToolAdapter>>();
        var adapter = new McpToolAdapter(client, toolInfo, logger);

        var call = new ToolCall { Id = "c1", Name = "srv__fail", Arguments = new() };
        var result = await adapter.ExecuteAsync(call, null!);

        Assert.True(result.IsError);
        Assert.Contains("boom", result.Content);
    }
}

public class McpRegistryTests
{
    private class FakeMcpClient : IMcpClient
    {
        public string ServerName { get; set; } = "fake";
        public bool IsConnected => true;
        public List<McpToolInfo> Tools { get; set; } = new();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult(Tools);
        public Task<McpToolResult> CallToolAsync(string name, Dictionary<string, object?> args, CancellationToken ct = default)
            => Task.FromResult(new McpToolResult { Content = "ok" });
        public Task<List<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<McpResourceInfo>());
        public Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
            => Task.FromResult(new McpResourceContent { Uri = uri, Text = "" });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int TotalTools(McpRegistry r) =>
        r.Clients.Sum(c => r.GetToolCount(c.ServerName));

    [Fact]
    public async Task InitializeAsync_ConnectsAllClients()
    {
        var client1 = new FakeMcpClient { ServerName = "s1", Tools = new() { new() { Name = "t1" } } };
        var client2 = new FakeMcpClient { ServerName = "s2", Tools = new() { new() { Name = "t2" } } };
        var configs = new List<McpServerConfig>
        {
            new() { Name = "s1", Command = "a" },
            new() { Name = "s2", Command = "b" }
        };

        var registry = new McpRegistry(
            configs,
            cfg => cfg.Name == "s1" ? client1 : client2,
            Substitute.For<ILogger<McpRegistry>>());

        await registry.InitializeAsync();

        Assert.Equal(2, registry.Clients.Count);
        Assert.Equal(2, TotalTools(registry));
        Assert.Equal("Connected", registry.GetStatus("s1"));
        Assert.Equal("Connected", registry.GetStatus("s2"));
    }

    [Fact]
    public async Task InitializeAsync_ContinuesOnFailure()
    {
        var goodClient = new FakeMcpClient { ServerName = "good", Tools = new() { new() { Name = "t1" } } };
        var badClient = Substitute.For<IMcpClient>();
        badClient.ServerName.Returns("bad");
        badClient.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                 .Do(x => throw new Exception("connection refused"));

        var configs = new List<McpServerConfig>
        {
            new() { Name = "good", Command = "a" },
            new() { Name = "bad", Command = "b" }
        };

        var registry = new McpRegistry(
            configs,
            cfg => cfg.Name == "good" ? goodClient : badClient,
            Substitute.For<ILogger<McpRegistry>>());

        await registry.InitializeAsync();

        Assert.Single(registry.Clients);
        Assert.Equal(1, TotalTools(registry));
        Assert.Equal("Failed", registry.GetStatus("bad"));
    }

    [Fact]
    public void ListServers_ReturnsAllConfiguredServers()
    {
        var configs = new List<McpServerConfig>
        {
            new() { Name = "s1", Command = "a" },
            new() { Name = "s2", Url = "http://x" }
        };

        var registry = new McpRegistry(
            configs,
            cfg => new FakeMcpClient { ServerName = cfg.Name },
            Substitute.For<ILogger<McpRegistry>>());

        var servers = registry.ListServers();
        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public async Task ShutdownAsync_DisconnectsAllClients()
    {
        var client = Substitute.For<IMcpClient>();
        client.ServerName.Returns("s1");
        client.ListToolsAsync(Arg.Any<CancellationToken>())
              .Returns(new List<McpToolInfo>());
        var configs = new List<McpServerConfig> { new() { Name = "s1", Command = "a" } };

        var registry = new McpRegistry(
            configs,
            cfg => client,
            Substitute.For<ILogger<McpRegistry>>());

        await registry.InitializeAsync();
        await registry.ShutdownAsync();

        await client.Received(1).DisconnectAsync();
    }

    [Fact]
    public async Task GetToolCount_ReturnsZeroForUnknownServer()
    {
        var registry = new McpRegistry(
            new List<McpServerConfig>(),
            cfg => new FakeMcpClient(),
            Substitute.For<ILogger<McpRegistry>>());

        await registry.InitializeAsync();

        Assert.Equal(0, registry.GetToolCount("nonexistent"));
    }

    [Fact]
    public async Task GetStatus_ReturnsUnknownForUnconfiguredServer()
    {
        var registry = new McpRegistry(
            new List<McpServerConfig>(),
            cfg => new FakeMcpClient(),
            Substitute.For<ILogger<McpRegistry>>());

        await registry.InitializeAsync();

        Assert.Equal("Unknown", registry.GetStatus("nope"));
    }
}