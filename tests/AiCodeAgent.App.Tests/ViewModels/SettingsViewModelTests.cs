using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Usage;
using AiCodeAgent.Tools.Shell;
using Avalonia.Headless.XUnit;

namespace AiCodeAgent.App.Tests.ViewModels;

/// <summary>
/// E4-S5: every option on the Settings page is written to the key or file the engine already reads, and read back
/// from it. Each test saves through the view-model, opens the files again with a fresh service and checks both the
/// keys and a second view-model.
/// </summary>
public class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "settings-vm-" + Guid.NewGuid().ToString("N"));

    public SettingsViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string PricingPath => Path.Combine(_dir, "pricing.json");

    private sealed class FakeHooks : IHookRegistry
    {
        public List<HookDefinition> Active { get; } = new();
        public List<HookDefinition> Waiting { get; } = new();
        public string? TrustedFor { get; private set; }

        public IReadOnlyList<HookDefinition> Hooks => Active;
        public IReadOnlyList<HookDefinition> UntrustedProjectHooks => Waiting;

        public Task<IReadOnlyList<HookDefinition>> LoadAsync(string? workingDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HookDefinition>>(Active);

        public Task TrustProjectAsync(string? workingDirectory, CancellationToken cancellationToken = default)
        {
            TrustedFor = workingDirectory;
            Active.AddRange(Waiting);
            Waiting.Clear();
            return Task.CompletedTask;
        }
    }

    private SettingsEnvironment Env(
        Dictionary<string, string>? variables = null,
        IHookRegistry? hooks = null,
        IReadOnlyList<McpServerConfig>? mcp = null,
        Action<string>? openFolder = null) => new()
    {
        GetEnvironmentVariable = name => variables != null && variables.TryGetValue(name, out var v) ? v : null,
        PricingPath = PricingPath,
        ConfigFolder = _dir,
        Hooks = hooks,
        HookWorkingDirectory = _dir,
        LoadMcpServers = () => mcp ?? Array.Empty<McpServerConfig>(),
        OpenFolder = openFolder ?? (_ => { })
    };

    private async Task<(SettingsViewModel Vm, ConfigurationService Service)> Open(SettingsEnvironment? env = null)
    {
        var service = new ConfigurationService(_dir);
        await service.LoadAsync();
        return (new SettingsViewModel(service, null, null, env ?? Env()), service);
    }

    // ---- Agent -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Agent_Section_RoundTrips()
    {
        var (vm, _) = await Open();
        vm.MaxIterationsText = "120";
        vm.SelectedReasoningEffort = "High";
        vm.SelectedRequirementsClarifier = "Always";
        vm.SelectedPermissionMode = "AutoEdit";
        await vm.SaveCommand.ExecuteAsync(null);

        var (reopened, service) = await Open();

        Assert.Equal(120, service.Config.Agent.MaxIterations);
        Assert.Equal("High", service.Config.Agent.DefaultReasoningEffort);
        Assert.Equal(RequirementsClarifierMode.Always, service.Config.Agent.RequirementsClarifier);
        Assert.Equal("AutoEdit", service.Config.Agent.DefaultPermissionMode);
        Assert.Equal("120", reopened.MaxIterationsText);
        Assert.Equal("High", reopened.SelectedReasoningEffort);
        Assert.Equal("Always", reopened.SelectedRequirementsClarifier);
        Assert.Equal("AutoEdit", reopened.SelectedPermissionMode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("many")]
    [InlineData("")]
    public async Task Agent_MaxIterations_MustBeAWholeNumberFrom1To500(string text)
    {
        var (vm, service) = await Open();
        vm.MaxIterationsText = text;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SaveFailed);
        Assert.Contains("Max iterations", vm.SaveStatus);
        Assert.Equal(50, service.Config.Agent.MaxIterations);   // nothing was written
    }

    // ---- Safety ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Safety_Sandbox_RoundTrips_AndTheEngineReadsTheSameValues()
    {
        var (vm, _) = await Open();
        vm.SandboxEnabled = true;
        vm.SandboxImage = "node:22";
        vm.SandboxNetwork = true;
        vm.SandboxMemory = "8g";
        vm.SandboxCpus = "1.5";
        await vm.SaveCommand.ExecuteAsync(null);

        var (reopened, service) = await Open();

        var sandbox = service.Config.Agent.Sandbox;
        Assert.True(sandbox.Enabled);
        Assert.Equal("node:22", sandbox.Image);
        Assert.True(sandbox.Network);
        Assert.Equal("8g", sandbox.Memory);
        Assert.Equal("1.5", sandbox.Cpus);
        Assert.True(reopened.SandboxEnabled);
        Assert.Equal("node:22", reopened.SandboxImage);
        Assert.True(reopened.SandboxNetwork);
        Assert.Equal("8g", reopened.SandboxMemory);
        Assert.Equal("1.5", reopened.SandboxCpus);

        // What the sandbox uses at run time is built from exactly these keys.
        var engine = DockerSandboxOptions.FromConfiguration(sandbox, _ => null);
        Assert.True(engine.Enabled);
        Assert.Equal("node:22", engine.Image);
        Assert.Contains("In effect: Docker sandbox on", reopened.SandboxSummary);
    }

    [Theory]
    [InlineData("lots", "2")]
    [InlineData("4g", "0")]
    [InlineData("4g", "fast")]
    public async Task Safety_Sandbox_RejectsValuesDockerWouldRefuse(string memory, string cpus)
    {
        var (vm, service) = await Open();
        vm.SandboxMemory = memory;
        vm.SandboxCpus = cpus;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SaveFailed);
        Assert.Equal("4g", service.Config.Agent.Sandbox.Memory);
    }

    [Fact]
    public async Task Safety_AnEnvironmentVariable_IsReported_AndStillWins()
    {
        var env = Env(new Dictionary<string, string>
        {
            ["AIAGENT_SANDBOX"] = "docker",
            ["AIAGENT_SANDBOX_IMAGE"] = "python:3.13"
        });
        var (vm, _) = await Open(env);

        Assert.Contains("Overridden by AIAGENT_SANDBOX", vm.SandboxEnabledNote);
        Assert.Contains("Overridden by AIAGENT_SANDBOX_IMAGE", vm.SandboxImageNote);
        Assert.Equal(string.Empty, vm.SandboxMemoryNote);   // not set, so nothing to report
        Assert.Contains("In effect: Docker sandbox on", vm.SandboxSummary);
        Assert.Contains("python:3.13", vm.SandboxSummary);
        Assert.False(vm.SandboxEnabled);   // the saved value is untouched by the variable
    }

    [Fact]
    public async Task Safety_Hooks_AreListedWithTheirTrustState_AndCanBeTrusted()
    {
        var hooks = new FakeHooks();
        hooks.Active.Add(new HookDefinition { Name = "lint", Event = HookEvent.PostToolUse, Command = "dotnet format", Blocking = false });
        hooks.Waiting.Add(new HookDefinition { Name = "deploy-guard", Event = HookEvent.PreToolUse, Command = "./guard.sh", Blocking = true });
        var (vm, _) = await Open(Env(hooks: hooks));

        Assert.Equal(2, vm.HookRows.Count);
        Assert.True(vm.HookRows.Single(r => r.Name == "lint").Trusted);
        var waiting = vm.HookRows.Single(r => r.Name == "deploy-guard");
        Assert.False(waiting.Trusted);
        Assert.True(waiting.Blocking);
        Assert.Contains("Not trusted", waiting.Status);
        Assert.True(vm.HasUntrustedHooks);

        await vm.TrustProjectHooksCommand.ExecuteAsync(null);

        Assert.Equal(_dir, hooks.TrustedFor);
        Assert.All(vm.HookRows, r => Assert.True(r.Trusted));
        Assert.False(vm.HasUntrustedHooks);
    }

    [Fact]
    public async Task Safety_WithoutAHookRegistry_SaysSo()
    {
        var (vm, _) = await Open();

        Assert.Empty(vm.HookRows);
        Assert.Contains("not available", vm.HooksSummary);
    }

    // ---- Cost --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cost_BudgetAndPriceTable_RoundTrip_ThroughConfigAndPricingJson()
    {
        var (vm, _) = await Open();
        vm.BudgetCapText = "0.01";
        vm.PriceRows.Add(new PriceRowViewModel("claude-sonnet", "3", "15"));
        vm.PriceRows.Add(new PriceRowViewModel("my-model", "1,5", "6", "0.15", "1.9"));
        await vm.SaveCommand.ExecuteAsync(null);

        var (reopened, service) = await Open();

        Assert.Equal(0.01m, service.Config.Agent.MaxBudgetUsd);
        Assert.Equal("0.01", reopened.BudgetCapText);

        var rows = ModelPricing.LoadOverrideRows(PricingPath);
        Assert.Equal(new[] { "claude-sonnet", "my-model" }, rows.Select(r => r.Key));
        Assert.Equal(new ModelPrice(3m, 15m), rows[0].Value);
        Assert.Equal(new ModelPrice(1.5m, 6m, 0.15m, 1.9m), rows[1].Value);

        Assert.Equal(2, reopened.PriceRows.Count);
        Assert.Equal("my-model", reopened.PriceRows[1].Model);
        Assert.Equal("1.5", reopened.PriceRows[1].InputText);
        Assert.Equal("0.15", reopened.PriceRows[1].CacheReadText);
    }

    [Fact]
    public async Task Cost_AnEmptyBudget_MeansNoCap()
    {
        var (vm, service) = await Open();
        vm.BudgetCapText = "0.50";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0.5m, service.Config.Agent.MaxBudgetUsd);

        vm.BudgetCapText = "  ";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(service.Config.Agent.MaxBudgetUsd);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Cost_ABadBudget_IsRefused_AndNothingIsWritten(string text)
    {
        var (vm, service) = await Open();
        vm.BudgetCapText = text;
        vm.PriceRows.Add(new PriceRowViewModel("m", "1", "2"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SaveFailed);
        Assert.Contains("Budget", vm.SaveStatus);
        Assert.Null(service.Config.Agent.MaxBudgetUsd);
        Assert.False(File.Exists(PricingPath));
    }

    [Fact]
    public async Task Cost_ABadPriceRow_IsRefused()
    {
        var (vm, _) = await Open();
        vm.PriceRows.Add(new PriceRowViewModel("m", "cheap", "2"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.SaveFailed);
        Assert.Contains("\"m\"", vm.SaveStatus);

        vm.PriceRows.Clear();
        vm.PriceRows.Add(new PriceRowViewModel("", "1", "2"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.SaveFailed);

        vm.PriceRows.Clear();
        vm.PriceRows.Add(new PriceRowViewModel("m", "1", "2"));
        vm.PriceRows.Add(new PriceRowViewModel("M", "3", "4"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.SaveFailed);
        Assert.Contains("twice", vm.SaveStatus);
        Assert.False(File.Exists(PricingPath));
    }

    [Fact]
    public async Task Cost_AnUntouchedEmptyRow_IsSkipped_AndRemovingAllRowsEmptiesTheFile()
    {
        var (vm, _) = await Open();
        vm.PriceRows.Add(new PriceRowViewModel("m", "1", "2"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(ModelPricing.LoadOverrideRows(PricingPath));

        vm.RemovePriceRowCommand.Execute(vm.PriceRows[0]);
        vm.AddPriceRowCommand.Execute(null);   // a blank row the user never filled in
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.SaveFailed);
        Assert.Empty(ModelPricing.LoadOverrideRows(PricingPath));
    }

    // ---- Integrations ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Integrations_BrowserPath_RoundTrips()
    {
        var (vm, _) = await Open();
        vm.BrowserPath = @"C:\Tools\chromium\chrome.exe";
        await vm.SaveCommand.ExecuteAsync(null);

        var (reopened, service) = await Open();

        Assert.Equal(@"C:\Tools\chromium\chrome.exe", service.Config.Agent.BrowserPath);
        Assert.Equal(@"C:\Tools\chromium\chrome.exe", reopened.BrowserPath);
    }

    [Fact]
    public async Task Integrations_BrowserVariable_IsReported()
    {
        var (vm, _) = await Open(Env(new Dictionary<string, string> { ["AIAGENT_BROWSER"] = "/opt/chrome" }));

        Assert.Contains("Overridden by AIAGENT_BROWSER", vm.BrowserPathNote);
    }

    [Fact]
    public async Task Integrations_McpServers_AreListed()
    {
        var servers = new List<McpServerConfig>
        {
            new() { Name = "github", Command = "npx", Args = new() { "-y", "@modelcontextprotocol/server-github" } },
            new() { Name = "docs", Url = "https://example.com/mcp", Enabled = false }
        };
        var (vm, _) = await Open(Env(mcp: servers));

        Assert.Equal(2, vm.McpServerRows.Count);
        Assert.Equal("npx -y @modelcontextprotocol/server-github", vm.McpServerRows[0].Target);
        Assert.Equal("https://example.com/mcp", vm.McpServerRows[1].Target);
        Assert.Equal("docs (disabled)", vm.McpServerRows[1].Title);
        Assert.EndsWith("mcp.json", vm.McpConfigPath);
    }

    // ---- Appearance --------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Appearance_ThemeAndTerminalShell_RoundTrip()
    {
        var (vm, _) = await Open();
        vm.SelectedTheme = "Light";
        vm.TerminalShell = "pwsh -NoLogo";
        await vm.SaveCommand.ExecuteAsync(null);

        var (reopened, service) = await Open();

        Assert.Equal("light", service.Config.Ui.Theme);
        Assert.Equal("pwsh -NoLogo", service.Config.Ui.TerminalShell);
        Assert.Equal("Light", reopened.SelectedTheme);
        Assert.Equal("pwsh -NoLogo", reopened.TerminalShell);
    }

    // ---- Advanced ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Advanced_OnStartAndUpdates_RoundTrip()
    {
        var (vm, _) = await Open();
        vm.SelectedResumeLastSession = "Never";
        vm.CheckForUpdates = false;
        await vm.SaveCommand.ExecuteAsync(null);

        var (reopened, service) = await Open();

        Assert.Equal(ResumeLastSessionMode.Never, service.Config.Ui.ResumeLastSession);
        Assert.False(service.Config.Ui.CheckForUpdates);
        Assert.Equal("Never", reopened.SelectedResumeLastSession);
        Assert.False(reopened.CheckForUpdates);
    }

    [Fact]
    public async Task Advanced_OpenConfigFolder_OpensTheFolderWithTheConfigFiles()
    {
        string? opened = null;
        var (vm, _) = await Open(Env(openFolder: path => opened = path));

        vm.OpenConfigFolderCommand.Execute(null);

        Assert.Equal(_dir, opened);
        Assert.Equal(_dir, vm.ConfigFolder);
    }

    [AvaloniaFact]
    public async Task Advanced_Reset_AsksFirst_ThenRestoresDefaults_AndKeepsProvidersAndPricing()
    {
        var (vm, service) = await Open();
        vm.MaxIterationsText = "200";
        vm.BudgetCapText = "5";
        vm.SandboxEnabled = true;
        vm.BrowserPath = "/opt/chrome";
        vm.SelectedTheme = "Light";
        vm.TerminalShell = "zsh";
        vm.PriceRows.Add(new PriceRowViewModel("m", "1", "2"));
        await vm.SaveCommand.ExecuteAsync(null);
        var providers = service.Config.Providers.Keys.OrderBy(k => k).ToList();

        vm.RequestResetCommand.Execute(null);
        Assert.True(vm.IsConfirmingReset);
        Assert.Equal(200, service.Config.Agent.MaxIterations);   // asking changes nothing

        vm.CancelResetCommand.Execute(null);
        Assert.False(vm.IsConfirmingReset);

        vm.RequestResetCommand.Execute(null);
        await vm.ConfirmResetCommand.ExecuteAsync(null);

        var (reopened, reloaded) = await Open();
        Assert.Equal(50, reloaded.Config.Agent.MaxIterations);
        Assert.Null(reloaded.Config.Agent.MaxBudgetUsd);
        Assert.False(reloaded.Config.Agent.Sandbox.Enabled);
        Assert.Null(reloaded.Config.Agent.BrowserPath);
        Assert.Null(reloaded.Config.Ui.TerminalShell);
        Assert.Equal("dark", reloaded.Config.Ui.Theme);
        Assert.Equal(providers, reloaded.Config.Providers.Keys.OrderBy(k => k).ToList());
        Assert.Single(ModelPricing.LoadOverrideRows(PricingPath));   // pricing.json is kept
        Assert.Equal("50", reopened.MaxIterationsText);
        Assert.False(vm.IsConfirmingReset);
        Assert.Equal("50", vm.MaxIterationsText);                    // the open form shows the defaults too
    }
}
