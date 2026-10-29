using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

public enum McpHealth { Unknown, Connected, Error, Disabled }

/// <summary>One server in Settings → Integrations → MCP servers.</summary>
public sealed partial class McpServerItem : ObservableObject
{
    public McpServerItem(McpServerConfig config) => Config = config;

    /// <summary>The entry as written in the file (references are not expanded).</summary>
    public McpServerConfig Config { get; }

    public string Name => Config.Name;
    public McpScope Scope => Config.Scope;
    public string ScopeLabel => Scope == McpScope.Project ? "project" : "global";
    public bool IsEnabled => Config.Enabled;
    public string ToggleText => IsEnabled ? "Disable" : "Enable";
    public string Target => Config.IsRemote ? Config.Url! : $"{Config.Command} {string.Join(' ', Config.Args)}".Trim();

    /// <summary>Set when a project entry with the same name hides this global one.</summary>
    public bool IsOverridden { get; init; }
    public string OverriddenText => IsOverridden ? "hidden by the project entry with the same name" : string.Empty;

    /// <summary>True when the entry still holds a plain-text token or password (move it to an environment variable).</summary>
    public bool HasPlainSecret { get; init; }

    [ObservableProperty] private McpHealth _health;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private int? _toolCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConfirmingRemove;

    /// <summary>The tools found by the last test or connection.</summary>
    public IReadOnlyList<McpToolInfo> Tools { get; set; } = Array.Empty<McpToolInfo>();

    public bool IsConnected => Health == McpHealth.Connected;
    public bool IsError => Health == McpHealth.Error;
    public bool IsDisabled => Health == McpHealth.Disabled;
    public bool IsUnknown => Health == McpHealth.Unknown;
    public string ToolCountText => ToolCount is { } n ? (n == 1 ? "1 tool" : $"{n} tools") : "—";

    partial void OnHealthChanged(McpHealth value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(IsUnknown));
    }

    partial void OnToolCountChanged(int? value) => OnPropertyChanged(nameof(ToolCountText));

    public IAsyncRelayCommand? TestCommand { get; set; }
    public IAsyncRelayCommand? ToggleCommand { get; set; }
    public IRelayCommand? EditCommand { get; set; }
    public IAsyncRelayCommand? ToolsCommand { get; set; }
    public IRelayCommand? AskRemoveCommand { get; set; }
    public IAsyncRelayCommand? ConfirmRemoveCommand { get; set; }
    public IRelayCommand? CancelRemoveCommand { get; set; }
}

/// <summary>A tool of the server being browsed.</summary>
public sealed record McpToolRow(string Name, string Description, string Pattern);

/// <summary>A character with a switch for "may use this server's tools".</summary>
public sealed partial class McpCharacterRow : ObservableObject
{
    public McpCharacterRow(string id, string title, bool allowed, bool canEdit, string note)
    {
        Id = id;
        Title = title;
        _allowed = allowed;
        CanEdit = canEdit;
        Note = note;
    }

    public string Id { get; }
    public string Title { get; }
    public bool CanEdit { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;

    [ObservableProperty] private bool _allowed;

    public IAsyncRelayCommand? ToggleCommand { get; set; }
}

/// <summary>Everything the view model needs from outside; tests replace the pieces.</summary>
public sealed class McpStatusOptions
{
    /// <summary>Reads environment variables (for <c>${env:NAME}</c> references).</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The running connections, for the status shown before anyone presses Test. Null = none.</summary>
    public IMcpRegistry? Live { get; init; }

    /// <summary>Reconnects the app after mcp.json changed (so the agent sees new tools). Null = restart the app.</summary>
    public Func<Task>? ApplyChanges { get; init; }

    /// <summary>For the per-character allow-list. Null = no allow-list in the tool browser.</summary>
    public ICharacterRegistry? Characters { get; init; }

    /// <summary>The project folder, used to fill the Filesystem preset.</summary>
    public string? ProjectDirectory { get; init; }

    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Settings → Integrations → MCP servers (E6-S3): the list of global and project servers with a status dot, tool count, Test,
/// Enable/Disable, Edit and Remove; the Add-server form with presets; and the tool browser with a per-character allow-list.
/// Secrets are always environment references, never plain text in the files.
/// </summary>
public sealed partial class McpStatusViewModel : ObservableObject
{
    private readonly McpConfigStore _store;
    private readonly Func<McpServerConfig, IMcpClient> _clientFactory;
    private readonly McpStatusOptions _options;
    private McpServerItem? _editing;

    public McpStatusViewModel(McpConfigStore store, Func<McpServerConfig, IMcpClient> clientFactory, McpStatusOptions? options = null)
    {
        _store = store;
        _clientFactory = clientFactory;
        _options = options ?? new McpStatusOptions();
    }

    public ObservableCollection<McpServerItem> Servers { get; } = new();
    public IReadOnlyList<McpPreset> Presets => McpPresets.All;

    public string GlobalFile => _store.FileOf(McpScope.Global) ?? string.Empty;
    public string ProjectFile => _store.FileOf(McpScope.Project) ?? string.Empty;
    public bool HasProject => _store.HasProject;
    public bool HasServers => Servers.Count > 0;
    public bool HasNoServers => Servers.Count == 0;

    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    // ------------------------------------------------------------------ list

    /// <summary>Reads both files again and shows what the running app is connected to.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var previous = Servers.ToDictionary(s => s.ScopeLabel + "/" + s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        var global = new List<McpServerConfig>();
        var project = new List<McpServerConfig>();
        try
        {
            global.AddRange(_store.Read(McpScope.Global));
            project.AddRange(_store.Read(McpScope.Project));
        }
        catch (McpConfigException ex)
        {
            StatusText = ex.Message;
        }

        Servers.Clear();
        var projectNames = project.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var config in project.Concat(global))
        {
            var item = new McpServerItem(config)
            {
                IsOverridden = config.Scope == McpScope.Global && projectNames.Contains(config.Name),
                HasPlainSecret = McpSecrets.Find(config).Count > 0
            };
            Wire(item);
            if (previous.TryGetValue(item.ScopeLabel + "/" + item.Name, out var old) && old.Config.Enabled == config.Enabled)
            {
                item.Health = old.Health;
                item.StatusText = old.StatusText;
                item.ToolCount = old.ToolCount;
                item.Tools = old.Tools;
            }
            else
            {
                await ApplyLiveStatusAsync(item).ConfigureAwait(true);
            }
            Servers.Add(item);
        }
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(HasNoServers));
        SummaryText = Servers.Count == 0
            ? "No MCP servers are configured. Add one to give the agent more tools."
            : $"{Servers.Count} server(s). A project entry hides a global one with the same name. Changes are applied right away.";
    }

    private async Task ApplyLiveStatusAsync(McpServerItem item)
    {
        if (!item.IsEnabled)
        {
            item.Health = McpHealth.Disabled;
            item.StatusText = "Disabled";
            return;
        }
        var live = _options.Live;
        if (live == null || item.IsOverridden) { item.Health = McpHealth.Unknown; item.StatusText = "Not tested yet"; return; }

        var status = live.GetStatus(item.Name);
        if (status == "Connected")
        {
            item.Health = McpHealth.Connected;
            item.StatusText = "Connected";
            item.ToolCount = await Task.Run(() => live.GetToolCount(item.Name)).ConfigureAwait(true);
        }
        else if (status == "Failed")
        {
            item.Health = McpHealth.Error;
            item.StatusText = live.GetError(item.Name) ?? "Not connected";
        }
        else
        {
            item.Health = McpHealth.Unknown;
            item.StatusText = "Not tested yet";
        }
    }

    private void Wire(McpServerItem item)
    {
        item.TestCommand = new AsyncRelayCommand(() => TestAsync(item));
        item.ToggleCommand = new AsyncRelayCommand(() => ToggleEnabledAsync(item));
        item.EditCommand = new RelayCommand(() => BeginEdit(item));
        item.ToolsCommand = new AsyncRelayCommand(() => BrowseToolsAsync(item));
        item.AskRemoveCommand = new RelayCommand(() => item.IsConfirmingRemove = true);
        item.CancelRemoveCommand = new RelayCommand(() => item.IsConfirmingRemove = false);
        item.ConfirmRemoveCommand = new AsyncRelayCommand(() => RemoveAsync(item));
    }

    // ------------------------------------------------------------------ test, enable, remove

    /// <summary>Starts the server in a throw-away connection, lists its tools and stops it again.</summary>
    public async Task TestAsync(McpServerItem item)
    {
        if (item.IsBusy) return;
        item.IsBusy = true;
        item.StatusText = "Testing…";
        try
        {
            var expanded = McpEnv.Expand(item.Config, _options.Environment);
            if (expanded.MissingEnv.Count > 0)
            {
                Fail(item, "Environment variable " + string.Join(", ", expanded.MissingEnv) + " is not set. Set it and restart the app, then test again.");
                return;
            }

            await using var client = _clientFactory(expanded with { Enabled = true });
            using var cts = new CancellationTokenSource(_options.TestTimeout);
            try
            {
                await client.ConnectAsync(cts.Token).ConfigureAwait(true);
                var tools = await client.ListToolsAsync(cts.Token).ConfigureAwait(true);
                item.Tools = tools;
                item.ToolCount = tools.Count;
                item.Health = item.IsEnabled ? McpHealth.Connected : McpHealth.Disabled;
                item.StatusText = item.IsEnabled ? "Connected" : $"Test passed: {tools.Count} tool(s). The server is disabled.";
            }
            catch (OperationCanceledException)
            {
                Fail(item, $"No answer after {(int)_options.TestTimeout.TotalSeconds} seconds.");
            }
            catch (Exception ex)
            {
                Fail(item, ex.Message);
            }
            finally
            {
                try { await client.DisconnectAsync().ConfigureAwait(true); } catch { /* best effort */ }
            }
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static void Fail(McpServerItem item, string message)
    {
        item.Health = item.IsEnabled ? McpHealth.Error : McpHealth.Disabled;
        item.StatusText = message;
        item.ToolCount = null;
        item.Tools = Array.Empty<McpToolInfo>();
    }

    public async Task ToggleEnabledAsync(McpServerItem item)
    {
        try
        {
            _store.SetEnabled(item.Scope, item.Name, !item.IsEnabled);
        }
        catch (Exception ex) when (ex is McpConfigException or IOException)
        {
            StatusText = ex.Message;
            return;
        }
        await RefreshAsync().ConfigureAwait(true);
        await ApplyAsync($"{item.Name} {(item.IsEnabled ? "disabled" : "enabled")}.").ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RemoveAsync(McpServerItem item)
    {
        try
        {
            _store.Remove(item.Scope, item.Name);
        }
        catch (Exception ex) when (ex is McpConfigException or IOException)
        {
            StatusText = ex.Message;
            return;
        }
        if (ReferenceEquals(BrowsedServer, item)) CloseTools();
        await RefreshAsync().ConfigureAwait(true);
        await ApplyAsync($"{item.Name} removed from the {item.ScopeLabel} mcp.json.").ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task ApplyAsync(string done)
    {
        if (_options.ApplyChanges == null)
        {
            StatusText = done + " Restart the app to connect the change.";
            return;
        }
        StatusText = done + " Reconnecting…";
        try
        {
            await _options.ApplyChanges().ConfigureAwait(true);
            StatusText = done;
        }
        catch (Exception ex)
        {
            StatusText = done + " Reconnecting failed: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ add / edit

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editorTitle = string.Empty;
    [ObservableProperty] private string _editorName = string.Empty;
    [ObservableProperty] private bool _editorIsProject;
    [ObservableProperty] private bool _editorIsRemote;
    [ObservableProperty] private string _editorCommand = string.Empty;
    [ObservableProperty] private string _editorArgs = string.Empty;
    [ObservableProperty] private string _editorEnv = string.Empty;
    [ObservableProperty] private string _editorUrl = string.Empty;
    [ObservableProperty] private string _editorTokenVariable = string.Empty;
    [ObservableProperty] private string _editorError = string.Empty;
    [ObservableProperty] private string _editorHint = string.Empty;

    public bool EditorIsLocal => !EditorIsRemote;
    partial void OnEditorIsRemoteChanged(bool value) => OnPropertyChanged(nameof(EditorIsLocal));

    [RelayCommand]
    private void BeginAdd()
    {
        _editing = null;
        ClearEditor();
        EditorTitle = "Add MCP server";
        IsEditing = true;
        EditorHint = "Pick a preset or fill in a custom server. Secrets go into environment variables: write ${env:NAME} and keep the value out of the file.";
    }

    [RelayCommand]
    private void ApplyPreset(McpPreset? preset)
    {
        if (preset == null) return;
        var config = preset.ToConfig(_options.ProjectDirectory);
        EditorName = preset.Id;
        EditorIsRemote = config.IsRemote;
        EditorCommand = config.Command ?? string.Empty;
        EditorArgs = string.Join("\n", config.Args);
        EditorEnv = string.Join("\n", config.Env.Select(kv => $"{kv.Key}={kv.Value}"));
        EditorUrl = config.Url ?? string.Empty;
        EditorTokenVariable = preset.TokenVariable ?? string.Empty;
        EditorError = string.Empty;
        var missing = preset.RequiredVariables.Where(v => string.IsNullOrEmpty(_options.Environment(v))).ToList();
        EditorHint = (preset.Note ?? preset.Description) +
                     (missing.Count > 0 ? $" {string.Join(", ", missing)} is not set in this app's environment yet; set it and restart the app before you test." : string.Empty);
    }

    public void BeginEdit(McpServerItem item)
    {
        _editing = item;
        ClearEditor();
        var c = item.Config;
        EditorTitle = $"Edit {item.Name}";
        EditorName = c.Name;
        EditorIsProject = c.Scope == McpScope.Project;
        EditorIsRemote = c.IsRemote;
        EditorCommand = c.Command ?? string.Empty;
        EditorArgs = string.Join("\n", c.Args);
        EditorEnv = string.Join("\n", c.Env.Select(kv => $"{kv.Key}={kv.Value}"));
        EditorUrl = c.Url ?? string.Empty;
        EditorTokenVariable = TokenVariableOf(c.Token) ?? string.Empty;
        EditorHint = item.HasPlainSecret
            ? "This entry holds a plain-text secret. Move it to an environment variable and write ${env:NAME} here."
            : string.Empty;
        IsEditing = true;
    }

    private static string? TokenVariableOf(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var vars = McpEnv.Variables(token);
        return vars.Count == 1 && token == McpEnv.Reference(vars[0]) ? vars[0] : null;
    }

    private void ClearEditor()
    {
        EditorName = string.Empty;
        EditorIsProject = false;
        EditorIsRemote = false;
        EditorCommand = string.Empty;
        EditorArgs = string.Empty;
        EditorEnv = string.Empty;
        EditorUrl = string.Empty;
        EditorTokenVariable = string.Empty;
        EditorError = string.Empty;
        EditorHint = string.Empty;
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditing = false;
        _editing = null;
        EditorError = string.Empty;
    }

    /// <summary>Builds the entry from the form, or says what is wrong with it.</summary>
    internal bool TryBuildConfig(out McpServerConfig config, out string error)
    {
        config = new McpServerConfig();
        var name = EditorName.Trim();
        if (McpConfigStore.ValidateName(name) is { } nameError) { error = nameError; return false; }

        var args = EditorArgs.Split('\n').Select(a => a.TrimEnd('\r')).Where(a => a.Trim().Length > 0).ToList();
        var env = new Dictionary<string, string>();
        foreach (var raw in EditorEnv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0 || !McpEnv.IsValidVariableName(line[..eq].Trim()))
            {
                error = $"'{line}' is not NAME=value. Write one environment variable per line, for example GITHUB_PERSONAL_ACCESS_TOKEN=${{env:GITHUB_TOKEN}}.";
                return false;
            }
            env[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        string? token = null;
        if (EditorIsRemote)
        {
            var url = EditorUrl.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Enter the server's http:// or https:// address.";
                return false;
            }
            var variable = EditorTokenVariable.Trim();
            if (variable.Length > 0)
            {
                if (McpEnv.ContainsReference(variable)) token = variable;
                else if (McpEnv.IsValidVariableName(variable)) token = McpEnv.Reference(variable);
                else { error = "Enter the NAME of the environment variable that holds the token (for example GITHUB_TOKEN), not the token itself."; return false; }
            }
            config = new McpServerConfig { Name = name, Url = url, Token = token, Env = env, Enabled = _editing?.IsEnabled ?? true };
        }
        else
        {
            var command = EditorCommand.Trim();
            if (command.Length == 0) { error = "Enter the command that starts the server (for example npx)."; return false; }
            config = new McpServerConfig { Name = name, Command = command, Args = args, Env = env, Enabled = _editing?.IsEnabled ?? true };
        }

        // Secrets are refused in both files: tokens, passwords and keys must be ${env:NAME} references.
        var secrets = McpSecrets.Find(config);
        if (secrets.Count > 0) { error = string.Join(" ", secrets); return false; }
        if (EditorIsProject && !_store.HasProject) { error = "There is no project folder open, so there is no project mcp.json."; return false; }
        error = string.Empty;
        return true;
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (!TryBuildConfig(out var config, out var error)) { EditorError = error; return; }
        var scope = EditorIsProject ? McpScope.Project : McpScope.Global;
        try
        {
            if (_editing != null && _editing.Scope != scope)
            {
                _store.Upsert(scope, config);
                _store.Remove(_editing.Scope, _editing.Name);
            }
            else
            {
                _store.Upsert(scope, config, _editing?.Name);
            }
        }
        catch (McpSecretException ex) { EditorError = ex.Message; return; }
        catch (Exception ex) when (ex is McpConfigException or IOException) { EditorError = ex.Message; return; }

        IsEditing = false;
        _editing = null;
        EditorError = string.Empty;
        await RefreshAsync().ConfigureAwait(true);
        await ApplyAsync($"Saved {config.Name} to the {(scope == McpScope.Project ? "project" : "global")} mcp.json.").ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);

        // Show right away whether it connects and which tools it offers.
        var saved = Servers.FirstOrDefault(s => s.Scope == scope && string.Equals(s.Name, config.Name, StringComparison.OrdinalIgnoreCase));
        if (saved is { IsEnabled: true } && saved.Health != McpHealth.Connected) await TestAsync(saved).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ tool browser

    public ObservableCollection<McpToolRow> ToolRows { get; } = new();
    public ObservableCollection<McpCharacterRow> CharacterRows { get; } = new();

    [ObservableProperty] private McpServerItem? _browsedServer;
    [ObservableProperty] private bool _isBrowsingTools;
    [ObservableProperty] private string _toolsHeader = string.Empty;
    [ObservableProperty] private string _characterStatus = string.Empty;

    public bool HasToolRows => ToolRows.Count > 0;
    public bool HasCharacterRows => CharacterRows.Count > 0;
    public bool HasCharacterRegistry => _options.Characters != null;

    /// <summary>The allow-list entry for the browsed server: <c>mcp:github/*</c>.</summary>
    public string ServerPattern => BrowsedServer == null ? string.Empty : ToolPatterns.McpPattern(BrowsedServer.Name);

    public async Task BrowseToolsAsync(McpServerItem item)
    {
        BrowsedServer = item;
        IsBrowsingTools = true;
        ToolsHeader = $"Tools of {item.Name}";
        if (item.Tools.Count == 0) await TestAsync(item).ConfigureAwait(true);
        ToolRows.Clear();
        foreach (var t in item.Tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            ToolRows.Add(new McpToolRow(t.Name, t.Description, ToolPatterns.McpPattern(item.Name, t.Name)));
        OnPropertyChanged(nameof(HasToolRows));
        OnPropertyChanged(nameof(ServerPattern));
        ToolsHeader = item.Tools.Count > 0
            ? $"{item.Tools.Count} tool(s) of {item.Name}"
            : $"No tools to show for {item.Name}: {item.StatusText}";
        await LoadCharactersAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseTools()
    {
        IsBrowsingTools = false;
        BrowsedServer = null;
        ToolRows.Clear();
        CharacterRows.Clear();
        OnPropertyChanged(nameof(HasToolRows));
        OnPropertyChanged(nameof(HasCharacterRows));
    }

    private async Task LoadCharactersAsync()
    {
        CharacterRows.Clear();
        CharacterStatus = string.Empty;
        var registry = _options.Characters;
        var pattern = ServerPattern;
        if (registry != null && pattern.Length > 0)
        {
            try
            {
                foreach (var info in (await registry.ListAsync().ConfigureAwait(true)).Where(c => c.ValidationErrors.Count == 0).OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var editable = !info.IsBuiltIn && !info.IsFromPack && info.FilePath != null;
                    var allowed = false;
                    if (editable) allowed = CharacterToolEdit.IsAllowed(await registry.LoadAsync(info.Id).ConfigureAwait(true), pattern);
                    var note = info.IsBuiltIn ? "built in: create your own character to change its tools"
                        : info.IsFromPack ? $"from the pack '{info.PackName}' (read-only)" : string.Empty;
                    var row = new McpCharacterRow(info.Id, info.DisplayName is { Length: > 0 } d ? d : info.Id, allowed, editable, note);
                    row.ToggleCommand = new AsyncRelayCommand(() => ToggleCharacterAsync(row));
                    CharacterRows.Add(row);
                }
            }
            catch (Exception ex)
            {
                CharacterStatus = "Could not read the characters: " + ex.Message;
            }
        }
        OnPropertyChanged(nameof(HasCharacterRows));
        OnPropertyChanged(nameof(HasCharacterRegistry));
    }

    /// <summary>Adds or removes <c>mcp:server/*</c> in the character's <c>tools.add</c>.</summary>
    public async Task ToggleCharacterAsync(McpCharacterRow row)
    {
        var registry = _options.Characters;
        if (registry == null || !row.CanEdit || BrowsedServer == null) return;
        var pattern = ServerPattern;
        var want = !row.Allowed;
        try
        {
            var content = await registry.LoadAsync(row.Id).ConfigureAwait(true);
            await registry.UpdateAsync(row.Id, CharacterToolEdit.SetAllowed(content, pattern, want)).ConfigureAwait(true);
            row.Allowed = want;
            CharacterStatus = want
                ? $"{row.Title} may now use the tools of {BrowsedServer.Name} ({pattern} in tools.add)."
                : $"{row.Title} no longer lists {pattern}.";
        }
        catch (Exception ex)
        {
            CharacterStatus = $"Could not change {row.Title}: {ex.Message}";
        }
    }
}
