using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Tools.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Everything the Settings page reads from outside <see cref="ConfigurationService"/>: environment variables, the pricing
/// file, the hook registry, the MCP files and the file manager. Tests replace the pieces; the app uses the defaults.
/// </summary>
public sealed class SettingsEnvironment
{
    /// <summary>Reads an environment variable (null when unset). Env vars override the UI, and the page says so.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The <c>pricing.json</c> the price table edits (the same file <c>ModelPricing</c> reads).</summary>
    public string PricingPath { get; init; } = AppPaths.Pricing;

    /// <summary>The per-user folder with config.json, pricing.json, mcp.json and the logs.</summary>
    public string ConfigFolder { get; init; } = AppPaths.UserRoot;

    /// <summary>Loaded hooks and their trust state; null when the host has no hook registry.</summary>
    public IHookRegistry? Hooks { get; init; }

    /// <summary>The folder whose project hooks "Trust" approves (the app loads hooks for its start-up folder).</summary>
    public string HookWorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>The MCP servers from <c>~/.aiagent/mcp.json</c> and the project's <c>.aiagent/mcp.json</c>.</summary>
    public Func<IReadOnlyList<McpServerConfig>> LoadMcpServers { get; init; } = () => McpConfigLoader.Load(Directory.GetCurrentDirectory());

    /// <summary>Opens a folder in the platform file manager.</summary>
    public Action<string> OpenFolder { get; init; } = DefaultOpenFolder;

    /// <summary>The user-level MCP file, shown on the Integrations section.</summary>
    public string McpConfigPath => Path.Combine(ConfigFolder, "mcp.json");

    private static void DefaultOpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        // UseShellExecute opens a folder in Explorer, Finder or the desktop default.
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}

/// <summary>One editable row of the price table (USD per million tokens), kept as text so a half-typed number is not lost.</summary>
public partial class PriceRowViewModel : ObservableObject
{
    public PriceRowViewModel() { }

    public PriceRowViewModel(string model, string input, string output, string cacheRead = "", string cacheWrite = "")
    {
        _model = model;
        _inputText = input;
        _outputText = output;
        _cacheReadText = cacheRead;
        _cacheWriteText = cacheWrite;
    }

    /// <summary>Model id or a fragment of it (matched case-insensitively, the longest fragment wins), e.g. <c>claude-sonnet</c>.</summary>
    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _outputText = string.Empty;

    /// <summary>Optional. Empty = 10% of the input price.</summary>
    [ObservableProperty]
    private string _cacheReadText = string.Empty;

    /// <summary>Optional. Empty = 125% of the input price.</summary>
    [ObservableProperty]
    private string _cacheWriteText = string.Empty;

    /// <summary>A row the user added but never filled in; it is skipped on save.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(InputText) && string.IsNullOrWhiteSpace(OutputText)
        && string.IsNullOrWhiteSpace(CacheReadText) && string.IsNullOrWhiteSpace(CacheWriteText);
}

/// <summary>A hook as listed in Settings → Safety, with its trust state.</summary>
public sealed record HookRowViewModel(string Name, string Event, string Command, bool Blocking, bool Trusted)
{
    public string Title => $"{Name} · {Event}{(Blocking ? " · blocking" : string.Empty)}";

    public string Status => Trusted ? "Active" : "Not trusted: it will not run until you trust this project";
}

/// <summary>An MCP server as listed in Settings → Integrations.</summary>
public sealed record McpServerRowViewModel(string Name, string Target, bool Enabled)
{
    public string Title => Enabled ? Name : Name + " (disabled)";
}
