using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Network;
using AiCodeAgent.Core.Updates;
using AiCodeAgent.Providers;
using AiCodeAgent.Providers.Ollama;
using AiCodeAgent.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.App.Tests.ViewModels;

/// <summary>Tests that switch the process-wide <see cref="NetworkPolicy.Shared"/> run one at a time.</summary>
[CollectionDefinition("SharedNetworkPolicy", DisableParallelization = true)]
public class SharedNetworkPolicyCollection { }

/// <summary>
/// E7-S4 acceptance: with a local Ollama provider and offline mode on, a task makes zero non-localhost requests, and the
/// network activity list shows what was refused instead. Plus the Settings switch and its persistence.
/// </summary>
[Collection("SharedNetworkPolicy")]
public class OfflineModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "offline-" + Guid.NewGuid().ToString("N"));
    private readonly NetworkPolicy _shared = NetworkPolicy.Shared;

    public OfflineModeTests()
    {
        Directory.CreateDirectory(_dir);
        _shared.Clear();
        _shared.ClearProviderHosts();
    }

    public void Dispose()
    {
        _shared.SetOffline(false);
        _shared.Clear();
        _shared.ClearProviderHosts();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A stand-in for Ollama on a free loopback port.</summary>
    private sealed class FakeOllama : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public int Requests;

        public FakeOllama()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    Interlocked.Increment(ref Requests);
                    var path = ctx.Request.Url!.AbsolutePath;
                    var json = path == "/api/tags"
                        ? "{\"models\":[{\"name\":\"llama3\"}]}"
                        : "{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"done\"},\"done\":true}";
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* stopped */ }
            _listener.Close();
        }
    }

    [Fact]
    public async Task Local_ollama_task_in_offline_mode_makes_zero_non_localhost_requests()
    {
        using var ollama = new FakeOllama();
        _shared.SetOffline(true);

        // 1. The model calls of the task go to the local provider.
        using var provider = new OllamaProvider(
            new ProviderConfiguration { Name = "ollama", BaseUrl = $"http://localhost:{ollama.Port}", DefaultModel = "llama3", TimeoutSeconds = 30 },
            NullLogger<OllamaProvider>.Instance);
        var request = new CompletionRequest
        {
            SystemPrompt = "You are a coding agent.",
            Messages = new List<Message> { new() { Role = MessageRole.User, Content = "say done" } }
        };
        var reply = await provider.CompleteAsync(request);
        Assert.Contains("done", reply.Content);
        Assert.Contains("llama3", await provider.GetAvailableModelsAsync());

        // 2. Everything else a task or the app might reach for is refused.
        var update = await new UpdateChecker(userAgentVersion: "1").CheckAsync(SemanticVersion.Parse("1.0.0"), UpdatePlatform.Current());
        Assert.Equal(UpdateStatus.Failed, update.Status);
        Assert.Contains("Offline mode", update.Error);

        using var web = new HttpClient(NetworkPolicyHandler.Wrap(NetworkPurpose.WebFetch, new SocketsHttpHandler { AllowAutoRedirect = false }));
        await Assert.ThrowsAsync<NetworkBlockedException>(() => web.GetAsync("https://example.com/docs"));

        var mcp = McpClients.Create(new McpServerConfig { Name = "remote", Url = "https://mcp.example.com/rpc" });
        await Assert.ThrowsAnyAsync<Exception>(() => mcp.ConnectAsync());

        // 3. The activity list says exactly that.
        Assert.True(ollama.Requests >= 2);
        Assert.Equal(0, _shared.NonLocalRequests());
        var hosts = _shared.Hosts();
        var local = Assert.Single(hosts, h => h.Host == "localhost");
        Assert.True(local.IsLocal);
        Assert.True(local.Requests >= 2);
        Assert.Equal(0, local.Blocked);
        foreach (var blockedHost in new[] { "api.github.com", "example.com", "mcp.example.com" })
        {
            var row = Assert.Single(hosts, h => h.Host == blockedHost);
            Assert.Equal(0, row.Requests);
            Assert.True(row.Blocked >= 1);
        }
    }

    [Fact]
    public void A_configured_remote_provider_stays_reachable_in_offline_mode()
    {
        _shared.SetOffline(true);
        using var provider = new OllamaProvider(
            new ProviderConfiguration { Name = "remote", BaseUrl = "https://llm.internal.example:8443", DefaultModel = "m" },
            NullLogger<OllamaProvider>.Instance);

        Assert.True(_shared.Check(new Uri("https://llm.internal.example:8443/api/chat")).Allowed);
        Assert.False(_shared.Check(new Uri("https://api.openai.com/v1/chat/completions")).Allowed);
    }

    [Fact]
    public async Task The_offline_switch_is_saved_and_applied()
    {
        var service = new ConfigurationService(_dir);
        await service.LoadAsync();
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        NetworkPolicyConfiguration.Bind(service, policy);
        var vm = new SettingsViewModel(service);
        vm.UseNetworkPolicy(policy);
        Assert.False(vm.OfflineMode);
        Assert.False(policy.OfflineMode);

        vm.OfflineMode = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(service.Config.Network.OfflineMode);
        Assert.True(policy.OfflineMode); // applied on save, no restart
        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Config.Network.OfflineMode);
        Assert.True(new SettingsViewModel(reloaded).OfflineMode);

        vm.OfflineMode = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(policy.OfflineMode);
    }

    [Fact]
    public void The_network_list_shows_the_hosts_this_session_and_can_be_cleared()
    {
        var service = new ConfigurationService(_dir);
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(_ => null);
        var vm = new SettingsViewModel(service);
        vm.UseNetworkPolicy(policy);
        Assert.Empty(vm.NetworkRows);
        Assert.Contains("Nothing has left this app", vm.NetworkSummary);

        policy.Record(new Uri("https://api.openai.com/v1/chat"), "POST", NetworkPurpose.Provider, blocked: false);
        policy.Record(new Uri("http://localhost:11434/api/tags"), "GET", NetworkPurpose.Provider, blocked: false);
        policy.Record(new Uri("https://api.github.com/x"), "GET", NetworkPurpose.UpdateCheck, blocked: true);
        vm.RefreshNetworkCommand.Execute(null);

        Assert.Equal(3, vm.NetworkRows.Count);
        Assert.Contains(vm.NetworkRows, r => r.Host == "localhost" && r.Where == "this computer");
        var github = vm.NetworkRows.Single(r => r.Host == "api.github.com");
        Assert.True(github.HasBlocked);
        Assert.Contains("blocked", vm.NetworkSummary);

        vm.ClearNetworkLogCommand.Execute(null);
        Assert.Empty(vm.NetworkRows);
    }

    [Fact]
    public void An_environment_forced_offline_mode_is_explained()
    {
        var service = new ConfigurationService(_dir);
        var policy = new NetworkPolicy();
        policy.ReadEnvironment(name => name == NetworkPolicy.OfflineEnvironmentVariable ? "true" : null);
        var vm = new SettingsViewModel(service);
        vm.UseNetworkPolicy(policy);

        Assert.True(vm.HasOfflineModeNote);
        Assert.Contains(NetworkPolicy.OfflineEnvironmentVariable, vm.OfflineModeNote);
    }
}
