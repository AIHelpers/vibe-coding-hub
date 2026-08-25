using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Higher-level permission manager (Feature 10). Implements the four
/// permission modes with a <see cref="PermissionClassifier"/> for Auto mode,
/// a list of allow-rules (org → project → personal), and scoped settings.
/// Falls back to <see cref="IPermissionService"/> for the actual prompt when
/// the decision is <see cref="PermissionDecision.Ask"/>.
/// </summary>
public class PermissionManager : IPermissionManager
{
    private static readonly PermissionMode[] Cycle =
    [
        PermissionMode.Ask, PermissionMode.AutoEdit, PermissionMode.FullAuto, PermissionMode.Plan
    ];

    private readonly ILogger<PermissionManager> _logger;
    private readonly PermissionClassifier _classifier = new();
    private readonly ConcurrentDictionary<PermissionScope, ScopedPermissionSettings> _scoped = new();
    private readonly List<PermissionRule> _rules = new();
    private readonly object _rulesLock = new();
    private readonly ConcurrentDictionary<string, PermissionMode> _agentModes = new();

    public PermissionManager(ILogger<PermissionManager> logger)
    {
        _logger = logger;
        _scoped[PermissionScope.Organization] = new ScopedPermissionSettings { Scope = PermissionScope.Organization };
        _scoped[PermissionScope.Project] = new ScopedPermissionSettings { Scope = PermissionScope.Project };
        _scoped[PermissionScope.Personal] = new ScopedPermissionSettings { Scope = PermissionScope.Personal };
    }

    /// <inheritdoc />
    public Task<PermissionMode> GetModeAsync(string? agentId = null, CancellationToken ct = default)
    {
        if (agentId != null && _agentModes.TryGetValue(agentId, out var agentMode))
            return Task.FromResult(agentMode);
        // Effective mode = highest-scope mode set, preferring Personal > Project > Org.
        var mode = EffectiveScopedMode();
        return Task.FromResult(mode);
    }

    /// <inheritdoc />
    public Task SetModeAsync(PermissionMode mode, PermissionScope scope = PermissionScope.Personal, string? agentId = null, CancellationToken ct = default)
    {
        if (agentId != null)
        {
            _agentModes[agentId] = mode;
            return Task.CompletedTask;
        }
        _scoped[scope] = _scoped[scope] with { Mode = mode };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<PermissionMode> CycleModeAsync(string? agentId = null, CancellationToken ct = default)
    {
        var current = await GetModeAsync(agentId, ct).ConfigureAwait(false);
        var idx = System.Array.IndexOf(Cycle, current);
        var next = idx < 0 ? PermissionMode.Ask : Cycle[(idx + 1) % Cycle.Length];
        await SetModeAsync(next, PermissionScope.Personal, agentId, ct).ConfigureAwait(false);
        return next;
    }

    /// <inheritdoc />
    public Task AllowAsync(PermissionRule rule, CancellationToken ct = default)
    {
        lock (_rulesLock)
        {
            // Avoid duplicate rules.
            if (!_rules.Any(r => r.ToolName == rule.ToolName && r.CommandPattern == rule.CommandPattern))
                _rules.Add(rule);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task LoadScopedSettingsAsync(string? settingsPath = null, CancellationToken ct = default)
    {
        var path = settingsPath ?? DefaultSettingsPath();
        if (!File.Exists(path))
            return Task.CompletedTask;

        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("permissions", out var perms))
            {
                if (perms.TryGetProperty("organization", out var org) && org.TryGetProperty("mode", out var om) && Enum.TryParse<PermissionMode>(om.GetString(), out var orgMode))
                    _scoped[PermissionScope.Organization] = _scoped[PermissionScope.Organization] with { Mode = orgMode };
                if (perms.TryGetProperty("project", out var proj) && proj.TryGetProperty("mode", out var pm) && Enum.TryParse<PermissionMode>(pm.GetString(), out var projMode))
                    _scoped[PermissionScope.Project] = _scoped[PermissionScope.Project] with { Mode = projMode };
                if (perms.TryGetProperty("personal", out var pers) && pers.TryGetProperty("mode", out var persm) && Enum.TryParse<PermissionMode>(persm.GetString(), out var pMode))
                    _scoped[PermissionScope.Personal] = _scoped[PermissionScope.Personal] with { Mode = pMode };
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load scoped permission settings from {Path}", path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PermissionDecision> CanExecuteAsync(ToolCall call, RiskLevel risk, AgentOptions options, string? agentId = null, CancellationToken ct = default)
    {
        // 1. Allow-rules (explicit per-command allow) always win.
        if (MatchesAllowRule(call))
            return Task.FromResult(PermissionDecision.Allow);

        // 2. Granular rights override the coarse mode when set.
        if (options.Rights != null)
        {
            var granular = GranularRightsDecision(risk, options.Rights);
            if (granular != null)
                return Task.FromResult(granular.Value);
        }

        // 3. Read-only session blocks writes/execute.
        if (options.IsReadOnly && risk != RiskLevel.Read)
            return Task.FromResult(PermissionDecision.Deny);

        // 4. Mode-based decision.
        var mode = GetModeAsync(agentId, ct).GetAwaiter().GetResult();
        var decision = mode switch
        {
            PermissionMode.Plan => risk == RiskLevel.Read ? PermissionDecision.Allow : PermissionDecision.Deny,
            PermissionMode.AutoEdit => risk == RiskLevel.Execute ? PermissionDecision.Ask : PermissionDecision.Allow,
            PermissionMode.FullAuto => _classifier.Classify(call, risk),
            _ => risk == RiskLevel.Read ? PermissionDecision.Allow : PermissionDecision.Ask // Ask mode: reads are safe
        };

        return Task.FromResult(decision);
    }

    private PermissionMode EffectiveScopedMode()
    {
        // Highest non-default scope wins. Personal > Project > Organization.
        var personal = _scoped[PermissionScope.Personal].Mode;
        if (personal != PermissionMode.Ask) return personal;
        var project = _scoped[PermissionScope.Project].Mode;
        if (project != PermissionMode.Ask) return project;
        return _scoped[PermissionScope.Organization].Mode;
    }

    private bool MatchesAllowRule(ToolCall call)
    {
        lock (_rulesLock)
        {
            foreach (var rule in _rules)
            {
                if (!string.Equals(rule.ToolName, call.Name, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(rule.CommandPattern))
                    return true;
                if (call.Arguments.TryGetValue("command", out var cmd) && cmd != null)
                {
                    var command = cmd.ToString() ?? string.Empty;
                    if (command.StartsWith(rule.CommandPattern, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }

    private static PermissionDecision? GranularRightsDecision(RiskLevel risk, GranularRights rights)
    {
        return risk switch
        {
            RiskLevel.Read => rights.AllowRead ? PermissionDecision.Allow : PermissionDecision.Ask,
            RiskLevel.Write => rights.AllowEdit ? PermissionDecision.Allow : PermissionDecision.Ask,
            RiskLevel.Execute => rights.AllowExecute ? PermissionDecision.Allow : PermissionDecision.Ask,
            _ => null
        };
    }

    private static string DefaultSettingsPath()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aiagent", "settings.json");
    }
}