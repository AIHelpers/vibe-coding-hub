using System.Collections.Concurrent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Permission service that manages tool approval workflows.
/// Supports Ask, AutoEdit, FullAuto, and Plan modes.
/// Persists per-project allowlists.
/// Modes can be set globally or per-agent (for multi-agent sessions).
/// </summary>
public class PermissionService : IPermissionService
{
    private readonly ILogger<PermissionService> _logger;
    private readonly ConcurrentDictionary<string, bool> _persistentAllowlist = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PermissionMode> _agentModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _allowlistPath;
    
    private PermissionMode _currentMode = PermissionMode.Ask;

    public PermissionMode CurrentMode => _currentMode;

    public PermissionService(ILogger<PermissionService> logger)
    {
        _logger = logger;
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent");
        Directory.CreateDirectory(configDir);
        _allowlistPath = Path.Combine(configDir, "allowlist.json");
        LoadAllowlistAsync().GetAwaiter().GetResult();
    }

    public void SetMode(PermissionMode mode, string? agentId = null)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            _currentMode = mode;
            _logger.LogInformation("Permission mode set to {Mode}", mode);
        }
        else
        {
            _agentModes[agentId] = mode;
            _logger.LogInformation("Permission mode set to {Mode} for agent {AgentId}", mode, agentId);
        }
    }

    public PermissionMode GetMode(string? agentId = null)
    {
        if (!string.IsNullOrEmpty(agentId) && _agentModes.TryGetValue(agentId, out var agentMode))
            return agentMode;
        return _currentMode;
    }

    public async Task<bool> RequestApprovalAsync(ToolCall call, RiskLevel risk, AgentOptions options, string? agentId = null)
    {
        var mode = GetMode(agentId);

        // Plan mode: only allow Read operations
        if (mode == PermissionMode.Plan)
        {
            if (risk != RiskLevel.Read)
                return false;
            return true; // Read operations are always allowed in Plan mode
        }

        // FullAuto mode: always approve
        if (mode == PermissionMode.FullAuto || options.AutoApprove)
            return true;

        // Check persistent allowlist
        var allowKey = $"{call.Name}:{GetCommandSignature(call)}";
        if (_persistentAllowlist.TryGetValue(allowKey, out var allowed) && allowed)
            return true;

        // AutoEdit mode: auto-approve Read and Write, ask for Execute
        if (mode == PermissionMode.AutoEdit)
        {
            if (risk == RiskLevel.Read || risk == RiskLevel.Write)
                return true;
            // Execute operations still need approval
            _logger.LogInformation("AutoEdit mode: requesting approval for {ToolName}", call.Name);
            return false; // Will be handled by ApprovalRequestEvent
        }

        // Ask mode: always ask
        if (risk == RiskLevel.Read)
            return true; // Read operations are always safe

        _logger.LogInformation("Requesting approval for {ToolName} (risk: {Risk})", call.Name, risk);
        return false; // Will be handled by ApprovalRequestEvent
    }

    public void AddToAllowlist(string toolName, string commandSignature)
    {
        var key = $"{toolName}:{commandSignature}";
        _persistentAllowlist[key] = true;
        _ = SaveAllowlistAsync();
    }

    private string GetCommandSignature(ToolCall call)
    {
        // Generate a simplified signature for the allowlist
        if (call.Arguments.TryGetValue("command", out var cmd) && cmd != null)
            return cmd.ToString()?.Length > 50 ? cmd.ToString()![..50] : cmd.ToString() ?? "";
        if (call.Arguments.TryGetValue("path", out var path) && path != null)
            return path.ToString() ?? "";
        return call.Name;
    }

    private async Task LoadAllowlistAsync()
    {
        try
        {
            if (!File.Exists(_allowlistPath)) return;
            var json = await File.ReadAllTextAsync(_allowlistPath);
            var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (entries != null)
            {
                foreach (var (key, value) in entries)
                    _persistentAllowlist[key] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load allowlist");
        }
    }

    private async Task SaveAllowlistAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                _persistentAllowlist.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_allowlistPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save allowlist");
        }
    }
}