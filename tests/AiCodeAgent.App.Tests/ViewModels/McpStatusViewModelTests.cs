using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Mcp;
using Xunit;

namespace AiCodeAgent.App.Tests.ViewModels;

/// <summary>E6-S3: the MCP manager lists servers with a status, tests them with a fake client and writes env references only.</summary>
public class McpStatusViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-vm-" + Guid.NewGuid().ToString("N"));
    private readonly McpConfigStore _store;
    private readonly Dictionary<string, string?> _env = new();
    private readonly List<McpServerConfig> _launched = new();
    private Func<McpServerConfig, FakeClient> _make;
    private int _applied;

    public McpStatusViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new McpConfigStore(Path.Combine(_dir, "user", "mcp.json"), Path.Combine(_dir, "proj", ".aiagent", "mcp.json"));
        _make = c => new FakeClient(c.Name, new[] { ("create_issue", "Creates an issue"), ("list_issues", "Lists issues") });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeClient : IMcpClient
    {
        private readonly List<McpToolInfo> _tools;
        public FakeClient(string name, IEnumerable<(string, string)> tools)
        {
            ServerName = name;
            _tools = tools.Select(t => new McpToolInfo { Name = t.Item1, Description = t.Item2 }).ToList();
        }
        public Exception? FailConnect { get; init; }
        public bool Hang { get; init; }
        public string ServerName { get; }
        public bool IsConnected { get; private set; }
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailConnect != null) throw FailConnect;
            IsConnected = true;
        }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public Task<List<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_tools);
        public Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<McpResourceInfo>());
        public Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private McpStatusViewModel Create(TimeSpan? timeout = null) => new(
        _store,
        c => { _launched.Add(c); return _make(c); },
        new McpStatusOptions
        {
            Environment = n => _env.GetValueOrDefault(n),
            ApplyChanges = () => { _applied++; return Task.CompletedTask; },
            ProjectDirectory = _dir,
            TestTimeout = timeout ?? TimeSpan.FromSeconds(5)
        });

    private static McpServerConfig GitHub() => McpPresets.Find("github")!.ToConfig();

    [Fact]
    public async Task List_ShowsGlobalAndProjectServers_WithDisabledState()
    {
        _store.Upsert(McpScope.Global, GitHub());
        _store.Upsert(McpScope.Project, McpPresets.Find("fetch")!.ToConfig());
        _store.SetEnabled(McpScope.Global, "github", false);

        var vm = Create();
        await vm.RefreshAsync();

        Assert.Equal(2, vm.Servers.Count);
        var gh = vm.Servers.Single(s => s.Name == "github");
        Assert.Equal(McpHealth.Disabled, gh.Health);
        Assert.Equal("Enable", gh.ToggleText);
        Assert.Equal("project", vm.Servers.Single(s => s.Name == "fetch").ScopeLabel);
    }

    [Fact]
    public async Task Test_Success_ShowsConnectedAndToolCount()
    {
        _env["GITHUB_TOKEN"] = "tok";
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create();
        await vm.RefreshAsync();
        var item = vm.Servers.Single();

        await vm.TestAsync(item);

        Assert.Equal(McpHealth.Connected, item.Health);
        Assert.Equal(2, item.ToolCount);
        Assert.Equal("2 tools", item.ToolCountText);
        // The client got the expanded token; the file keeps the reference.
        Assert.Equal("tok", _launched.Single().Env["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.DoesNotContain("tok\"", File.ReadAllText(_store.FileOf(McpScope.Global)!));
    }

    [Fact]
    public async Task Test_MissingEnvironmentVariable_FailsWithoutLaunching()
    {
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create();
        await vm.RefreshAsync();
        var item = vm.Servers.Single();

        await vm.TestAsync(item);

        Assert.Equal(McpHealth.Error, item.Health);
        Assert.Contains("GITHUB_TOKEN", item.StatusText);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task Test_ConnectionFailure_ShowsTheError()
    {
        _env["GITHUB_TOKEN"] = "tok";
        _make = c => new FakeClient(c.Name, Array.Empty<(string, string)>()) { FailConnect = new InvalidOperationException("spawn npx ENOENT") };
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create();
        await vm.RefreshAsync();
        var item = vm.Servers.Single();

        await vm.TestAsync(item);

        Assert.Equal(McpHealth.Error, item.Health);
        Assert.Contains("ENOENT", item.StatusText);
        Assert.Null(item.ToolCount);
    }

    [Fact]
    public async Task Test_Timeout_IsReportedAsError()
    {
        _env["GITHUB_TOKEN"] = "tok";
        _make = c => new FakeClient(c.Name, Array.Empty<(string, string)>()) { Hang = true };
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create(TimeSpan.FromMilliseconds(100));
        await vm.RefreshAsync();
        var item = vm.Servers.Single();

        await vm.TestAsync(item);

        Assert.Equal(McpHealth.Error, item.Health);
        Assert.Contains("No answer", item.StatusText);
    }

    [Fact]
    public async Task Toggle_WritesTheFile_AndAppliesTheChange()
    {
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create();
        await vm.RefreshAsync();

        await vm.ToggleEnabledAsync(vm.Servers.Single());

        Assert.False(_store.Read(McpScope.Global).Single().Enabled);
        Assert.Equal(McpHealth.Disabled, vm.Servers.Single().Health);
        Assert.Equal(1, _applied);

        await vm.ToggleEnabledAsync(vm.Servers.Single());
        Assert.True(_store.Read(McpScope.Global).Single().Enabled);
        Assert.Equal(2, _applied);
    }

    [Fact]
    public async Task Remove_DeletesTheEntry()
    {
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create();
        await vm.RefreshAsync();

        await vm.RemoveAsync(vm.Servers.Single());

        Assert.Empty(vm.Servers);
        Assert.Empty(_store.Read(McpScope.Global));
        Assert.True(vm.HasNoServers);
    }

    [Fact]
    public async Task AddGitHubPreset_ToProject_WritesNoSecrets_AndConnects()
    {
        _env["GITHUB_TOKEN"] = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var vm = Create();
        await vm.RefreshAsync();

        vm.BeginAddCommand.Execute(null);
        vm.ApplyPresetCommand.Execute(McpPresets.Find("github"));
        vm.EditorIsProject = true;
        await vm.SaveEditorCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.EditorError);
        Assert.False(vm.IsEditing);
        var text = File.ReadAllText(_store.FileOf(McpScope.Project)!);
        Assert.Contains("${env:GITHUB_TOKEN}", text);
        Assert.DoesNotContain("ghp_", text);
        var item = vm.Servers.Single(s => s.Name == "github");
        Assert.Equal(McpHealth.Connected, item.Health);
        Assert.Equal(2, item.ToolCount);
        Assert.True(_applied > 0);
    }

    [Fact]
    public async Task Editor_RejectsPlainTextSecrets_InEitherScope()
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.BeginAddCommand.Execute(null);
        vm.EditorName = "gh";
        vm.EditorCommand = "npx";
        vm.EditorEnv = "GITHUB_PERSONAL_ACCESS_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789";

        await vm.SaveEditorCommand.ExecuteAsync(null);

        Assert.NotEqual(string.Empty, vm.EditorError);
        Assert.True(vm.IsEditing);
        Assert.False(File.Exists(_store.FileOf(McpScope.Global)!));
    }

    [Fact]
    public async Task Editor_RemoteServer_TokenIsStoredAsReference()
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.BeginAddCommand.Execute(null);
        vm.EditorName = "remote";
        vm.EditorIsRemote = true;
        vm.EditorUrl = "https://example.com/mcp";
        vm.EditorTokenVariable = "REMOTE_TOKEN";
        _env["REMOTE_TOKEN"] = "abc";

        await vm.SaveEditorCommand.ExecuteAsync(null);

        var saved = Assert.Single(_store.Read(McpScope.Global));
        Assert.Equal("${env:REMOTE_TOKEN}", saved.Token);
    }

    [Theory]
    [InlineData("", "npx")]
    [InlineData("ok", "")]
    public async Task Editor_ValidatesRequiredFields(string name, string command)
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.BeginAddCommand.Execute(null);
        vm.EditorName = name;
        vm.EditorCommand = command;

        await vm.SaveEditorCommand.ExecuteAsync(null);

        Assert.NotEqual(string.Empty, vm.EditorError);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public async Task ToolBrowser_ListsToolsWithAllowListPatterns()
    {
        _env["GITHUB_TOKEN"] = "tok";
        _store.Upsert(McpScope.Global, GitHub());
        var vm = Create();
        await vm.RefreshAsync();

        await vm.BrowseToolsAsync(vm.Servers.Single());

        Assert.True(vm.IsBrowsingTools);
        Assert.Equal(new[] { "mcp:github/create_issue", "mcp:github/list_issues" }, vm.ToolRows.Select(t => t.Pattern).ToArray());
        Assert.Equal("mcp:github/*", vm.ServerPattern);
        Assert.Contains("Creates an issue", vm.ToolRows[0].Description);
    }
}
