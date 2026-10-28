using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Usage;
using AiCodeAgent.Tools.Shell;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// The options that used to live only in environment variables and JSON files, grouped as the Settings page shows them:
/// Agent, Safety, Cost, Integrations and Advanced. Every value is written to the key or file the engine already reads
/// (config.json, pricing.json); an environment variable that is set still wins, and the page says which one.
/// </summary>
public partial class SettingsViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex MemoryPattern = new(@"^[0-9]+(\.[0-9]+)?[bkmgBKMG]?$", RegexOptions.Compiled);

    private SettingsEnvironment _env = new();

    // ---- Agent ---------------------------------------------------------------------------------------------------

    /// <summary>Most model calls one task may make before it stops (a whole number, 1 to 500).</summary>
    [ObservableProperty]
    private string _maxIterationsText = "50";

    public string[] ReasoningEffortOptions { get; } = { "Default", "Off", "Low", "Medium", "High" };

    /// <summary>Reasoning effort a new chat starts with. "Default" leaves the provider's own behaviour alone.</summary>
    [ObservableProperty]
    private string _selectedReasoningEffort = "Default";

    // ---- Safety --------------------------------------------------------------------------------------------------

    public string[] PermissionModeOptions { get; } = { "Ask", "AutoEdit", "FullAuto", "Plan" };

    /// <summary>The permission mode a new chat starts in.</summary>
    [ObservableProperty]
    private string _selectedPermissionMode = "Ask";

    [ObservableProperty]
    private bool _sandboxEnabled;

    [ObservableProperty]
    private string _sandboxImage = "ubuntu:24.04";

    [ObservableProperty]
    private bool _sandboxNetwork;

    [ObservableProperty]
    private string _sandboxMemory = "4g";

    [ObservableProperty]
    private string _sandboxCpus = "2";

    /// <summary>What the saved settings plus the environment variables add up to right now.</summary>
    [ObservableProperty]
    private string _sandboxSummary = string.Empty;

    public string SandboxEnabledNote => EnvironmentNote("AIAGENT_SANDBOX");
    public string SandboxImageNote => EnvironmentNote("AIAGENT_SANDBOX_IMAGE");
    public string SandboxNetworkNote => EnvironmentNote("AIAGENT_SANDBOX_NETWORK");
    public string SandboxMemoryNote => EnvironmentNote("AIAGENT_SANDBOX_MEMORY");
    public string SandboxCpusNote => EnvironmentNote("AIAGENT_SANDBOX_CPUS");

    public ObservableCollection<HookRowViewModel> HookRows { get; } = new();

    [ObservableProperty]
    private string _hooksSummary = string.Empty;

    [ObservableProperty]
    private bool _hasUntrustedHooks;

    // ---- Cost ----------------------------------------------------------------------------------------------------

    /// <summary>Spend cap per task in USD. Empty = no cap.</summary>
    [ObservableProperty]
    private string _budgetCapText = string.Empty;

    /// <summary>Rows of <c>pricing.json</c>: prices that replace or extend the built-in estimates.</summary>
    public ObservableCollection<PriceRowViewModel> PriceRows { get; } = new();

    /// <summary>The built-in estimates, so the user can see what a row would replace.</summary>
    public string BuiltInPricesText { get; } =
        "Built-in estimates (USD per million tokens, input / output): " +
        string.Join(" · ", ModelPricing.BuiltIn.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} {kv.Value.InputPerMillion.ToString("0.##", CultureInfo.InvariantCulture)} / {kv.Value.OutputPerMillion.ToString("0.##", CultureInfo.InvariantCulture)}")) +
        ". Models without a price (for example local ones) count as $0, so a budget cap never stops them.";

    // ---- Integrations --------------------------------------------------------------------------------------------

    public ObservableCollection<McpServerRowViewModel> McpServerRows { get; } = new();

    [ObservableProperty]
    private string _mcpSummary = string.Empty;

    public string McpConfigPath => _env.McpConfigPath;

    /// <summary>Chrome, Edge or Chromium for the browser check. Empty = look in the usual places.</summary>
    [ObservableProperty]
    private string _browserPath = string.Empty;

    public string BrowserPathNote => EnvironmentNote("AIAGENT_BROWSER");

    // ---- Advanced ------------------------------------------------------------------------------------------------

    public string ConfigFolder => _env.ConfigFolder;

    [ObservableProperty]
    private bool _isConfirmingReset;

    /// <summary>Result of the last Save or Reset (or the reason it was refused).</summary>
    [ObservableProperty]
    private string _saveStatus = string.Empty;

    [ObservableProperty]
    private bool _saveFailed;

    // ---- Load ----------------------------------------------------------------------------------------------------

    private string EnvironmentNote(string name) =>
        _env.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? $"Overridden by {name} (currently \"{value}\"); the value above is ignored."
            : string.Empty;

    private void LoadAdvancedSettings()
    {
        var agent = _configurationService.Config.Agent ?? new AgentConfiguration();

        MaxIterationsText = agent.MaxIterations.ToString(Inv);
        SelectedReasoningEffort = ReasoningEffortOptions.FirstOrDefault(o => string.Equals(o, agent.DefaultReasoningEffort, StringComparison.OrdinalIgnoreCase)) ?? "Default";
        SelectedPermissionMode = PermissionModeOptions.FirstOrDefault(o => string.Equals(o, agent.DefaultPermissionMode, StringComparison.OrdinalIgnoreCase)) ?? "Ask";

        var sandbox = agent.Sandbox ?? new SandboxConfiguration();
        SandboxEnabled = sandbox.Enabled;
        SandboxImage = sandbox.Image;
        SandboxNetwork = sandbox.Network;
        SandboxMemory = sandbox.Memory;
        SandboxCpus = sandbox.Cpus;
        RefreshSandboxSummary(sandbox);

        BudgetCapText = agent.MaxBudgetUsd is > 0 ? agent.MaxBudgetUsd.Value.ToString("0.00##", Inv) : string.Empty;
        BrowserPath = agent.BrowserPath ?? string.Empty;

        LoadPriceRows();
        RefreshHooks();
        RefreshMcpServers();
        IsConfirmingReset = false;
    }

    private void RefreshSandboxSummary(SandboxConfiguration saved)
    {
        var o = DockerSandboxOptions.FromConfiguration(saved, _env.GetEnvironmentVariable);
        SandboxSummary = o.Enabled
            ? $"In effect: Docker sandbox on · image {o.Image} · network {(o.Network ? "on" : "off")} · memory {o.Memory} · {o.Cpus} CPUs"
            : "In effect: off. Commands the agent runs go straight to this computer.";
    }

    private void LoadPriceRows()
    {
        PriceRows.Clear();
        foreach (var (key, price) in ModelPricing.LoadOverrideRows(_env.PricingPath))
        {
            PriceRows.Add(new PriceRowViewModel(
                key,
                price.InputPerMillion.ToString("0.####", Inv),
                price.OutputPerMillion.ToString("0.####", Inv),
                price.CacheReadPerMillion?.ToString("0.####", Inv) ?? string.Empty,
                price.CacheWritePerMillion?.ToString("0.####", Inv) ?? string.Empty));
        }
    }

    private void RefreshHooks()
    {
        HookRows.Clear();
        var registry = _env.Hooks;
        if (registry is null)
        {
            HooksSummary = "Hooks are not available in this session.";
            HasUntrustedHooks = false;
            return;
        }

        foreach (var h in registry.Hooks)
            HookRows.Add(new HookRowViewModel(h.Name, h.Event.ToString(), h.Command, h.Blocking, Trusted: true));
        foreach (var h in registry.UntrustedProjectHooks)
            HookRows.Add(new HookRowViewModel(h.Name, h.Event.ToString(), h.Command, h.Blocking, Trusted: false));

        var waiting = registry.UntrustedProjectHooks.Count;
        HasUntrustedHooks = waiting > 0;
        HooksSummary = HookRows.Count == 0
            ? "No hooks are configured. Hooks are shell commands that run on agent events; define them in settings.json."
            : $"{registry.Hooks.Count} active" + (waiting > 0 ? $", {waiting} waiting for your trust" : string.Empty) + ".";
    }

    private void RefreshMcpServers()
    {
        McpServerRows.Clear();
        try
        {
            foreach (var s in _env.LoadMcpServers())
                McpServerRows.Add(new McpServerRowViewModel(s.Name, s.IsRemote ? s.Url! : $"{s.Command} {string.Join(' ', s.Args)}".Trim(), s.Enabled));
        }
        catch (Exception ex)
        {
            McpSummary = "The MCP configuration could not be read: " + ex.Message;
            return;
        }

        McpSummary = McpServerRows.Count == 0
            ? "No MCP servers are configured."
            : $"{McpServerRows.Count} server(s) configured. Edit mcp.json to add or change them; the full manager arrives with E6-S3.";
    }

    // ---- Save ----------------------------------------------------------------------------------------------------

    private sealed record AdvancedSettings(
        int MaxIterations,
        string PermissionMode,
        string ReasoningEffort,
        SandboxConfiguration Sandbox,
        decimal? Budget,
        string? BrowserPath,
        List<KeyValuePair<string, ModelPrice>> Prices);

    private static bool TryParseNumber(string? text, out decimal value) =>
        decimal.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, Inv, out value);

    private bool Fail(string message)
    {
        SaveStatus = message;
        SaveFailed = true;
        return false;
    }

    /// <summary>Checks every value of the five sections. Nothing is written when one of them is wrong.</summary>
    private bool TryReadAdvancedSettings(out AdvancedSettings settings)
    {
        settings = null!;

        if (!int.TryParse(MaxIterationsText.Trim(), NumberStyles.None, Inv, out var iterations) || iterations < 1 || iterations > 500)
            return Fail("Max iterations must be a whole number from 1 to 500.");

        decimal? budget = null;
        if (!string.IsNullOrWhiteSpace(BudgetCapText))
        {
            if (!TryParseNumber(BudgetCapText, out var cap) || cap <= 0)
                return Fail("Budget per task must be an amount in USD above 0, or empty for no cap.");
            budget = cap;
        }

        var memory = string.IsNullOrWhiteSpace(SandboxMemory) ? "4g" : SandboxMemory.Trim();
        if (!MemoryPattern.IsMatch(memory))
            return Fail("Sandbox memory must look like 512m or 4g.");
        var cpus = string.IsNullOrWhiteSpace(SandboxCpus) ? "2" : SandboxCpus.Trim().Replace(',', '.');
        if (!TryParseNumber(cpus, out var cpuCount) || cpuCount <= 0)
            return Fail("Sandbox CPUs must be a number above 0, for example 2 or 1.5.");

        var prices = new List<KeyValuePair<string, ModelPrice>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in PriceRows)
        {
            if (row.IsEmpty)
                continue;
            var key = row.Model.Trim();
            if (key.Length == 0)
                return Fail("Every price row needs a model name (or part of one, such as claude-sonnet).");
            if (!seen.Add(key))
                return Fail($"The price table lists \"{key}\" twice.");

            const string numberHint = " must be numbers (USD per million tokens), 0 or more.";
            if (!TryParseNumber(row.InputText, out var input) || input < 0)
                return Fail($"The prices for \"{key}\"{numberHint}");
            if (!TryParseNumber(row.OutputText, out var output) || output < 0)
                return Fail($"The prices for \"{key}\"{numberHint}");

            decimal? cacheRead = null;
            if (!string.IsNullOrWhiteSpace(row.CacheReadText))
            {
                if (!TryParseNumber(row.CacheReadText, out var read) || read < 0)
                    return Fail($"The prices for \"{key}\"{numberHint}");
                cacheRead = read;
            }

            decimal? cacheWrite = null;
            if (!string.IsNullOrWhiteSpace(row.CacheWriteText))
            {
                if (!TryParseNumber(row.CacheWriteText, out var write) || write < 0)
                    return Fail($"The prices for \"{key}\"{numberHint}");
                cacheWrite = write;
            }

            prices.Add(new KeyValuePair<string, ModelPrice>(key, new ModelPrice(input, output, cacheRead, cacheWrite)));
        }

        settings = new AdvancedSettings(
            iterations,
            SelectedPermissionMode,
            SelectedReasoningEffort,
            new SandboxConfiguration
            {
                Enabled = SandboxEnabled,
                Image = string.IsNullOrWhiteSpace(SandboxImage) ? "ubuntu:24.04" : SandboxImage.Trim(),
                Network = SandboxNetwork,
                Memory = memory,
                Cpus = cpus
            },
            budget,
            string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim(),
            prices);
        return true;
    }

    /// <summary>Copies the checked values into config.json's model (saved by the caller) and writes pricing.json.</summary>
    private void ApplyAdvancedSettings(AdvancedSettings s)
    {
        var config = _configurationService.Config;
        config.Agent ??= new AgentConfiguration();
        var agent = config.Agent;
        agent.MaxIterations = s.MaxIterations;
        agent.DefaultPermissionMode = s.PermissionMode;
        agent.DefaultReasoningEffort = s.ReasoningEffort;
        agent.MaxBudgetUsd = s.Budget;
        agent.BrowserPath = s.BrowserPath;

        // Change the existing object in place: the sandbox and the orchestrator hold this AgentConfiguration and read it per use.
        agent.Sandbox ??= new SandboxConfiguration();
        agent.Sandbox.Enabled = s.Sandbox.Enabled;
        agent.Sandbox.Image = s.Sandbox.Image;
        agent.Sandbox.Network = s.Sandbox.Network;
        agent.Sandbox.Memory = s.Sandbox.Memory;
        agent.Sandbox.Cpus = s.Sandbox.Cpus;

        if (s.Prices.Count > 0 || File.Exists(_env.PricingPath))
            ModelPricing.SaveOverrides(s.Prices, _env.PricingPath);

        RefreshSandboxSummary(agent.Sandbox);
    }

    // ---- Commands ------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddPriceRow() => PriceRows.Add(new PriceRowViewModel());

    [RelayCommand]
    private void RemovePriceRow(PriceRowViewModel? row)
    {
        if (row is not null)
            PriceRows.Remove(row);
    }

    [RelayCommand]
    private void OpenConfigFolder() => _env.OpenFolder(_env.ConfigFolder);

    [RelayCommand]
    private async Task TrustProjectHooks()
    {
        if (_env.Hooks is null)
            return;
        await _env.Hooks.TrustProjectAsync(_env.HookWorkingDirectory);
        RefreshHooks();
    }

    [RelayCommand]
    private async Task BrowseBrowser()
    {
        if (_storageProvider == null)
            return;

        var result = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Chrome, Edge or Chromium",
            AllowMultiple = false
        });

        if (result.Count > 0)
            BrowserPath = result[0].Path.LocalPath;
    }

    [RelayCommand]
    private void RequestReset() => IsConfirmingReset = true;

    [RelayCommand]
    private void CancelReset() => IsConfirmingReset = false;

    /// <summary>
    /// Puts the options on this page back to their defaults. Providers, API keys, project folders, the selected model and
    /// pricing.json are not touched.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmReset()
    {
        var config = _configurationService.Config;
        var agentDefaults = new AgentConfiguration();
        config.Agent ??= new AgentConfiguration();
        var agent = config.Agent;
        agent.MaxIterations = agentDefaults.MaxIterations;
        agent.MaxTokens = agentDefaults.MaxTokens;
        agent.AutoApprove = agentDefaults.AutoApprove;
        agent.RequirementsClarifier = agentDefaults.RequirementsClarifier;
        agent.DefaultPermissionMode = agentDefaults.DefaultPermissionMode;
        agent.DefaultReasoningEffort = agentDefaults.DefaultReasoningEffort;
        agent.MaxBudgetUsd = null;
        agent.BrowserPath = null;
        agent.Sandbox = new SandboxConfiguration();

        var uiDefaults = new UiConfiguration();
        config.Ui ??= new UiConfiguration();
        config.Ui.Theme = uiDefaults.Theme;
        config.Ui.ResumeLastSession = uiDefaults.ResumeLastSession;
        config.Ui.CheckForUpdates = uiDefaults.CheckForUpdates;
        config.Ui.TerminalShell = null;

        await _configurationService.SaveAsync();
        LoadSettings();
        AiCodeAgent.App.Services.ThemeService.Apply(SelectedTheme);
        SaveStatus = "Settings were reset to their defaults. Providers, API keys, project folders and pricing.json were kept.";
        SaveFailed = false;
    }
}
