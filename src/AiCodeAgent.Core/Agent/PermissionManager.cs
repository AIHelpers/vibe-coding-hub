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
    private PermissionMode? _projectCeiling;
    private string[] _projectDenyCommands = Array.Empty<string>();

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
        _scoped[scope] = _scoped[scope] with { Mode = mode, IsModeConfigured = true };
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
    public Task LoadProjectProfileAsync(string? workingDirectory, CancellationToken ct = default)
    {
        _projectCeiling = null;
        _projectDenyCommands = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Task.CompletedTask;

        var path = Path.Combine(workingDirectory, ".aiagent", "permissions.json");
        if (!File.Exists(path))
            return Task.CompletedTask;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("mode", out var m) && Enum.TryParse<PermissionMode>(m.GetString(), true, out var ceiling))
                _projectCeiling = ceiling;
            if (root.TryGetProperty("deny", out var deny) && deny.ValueKind == JsonValueKind.Array)
                _projectDenyCommands = deny.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read project permission profile {Path}", path);
        }
        return Task.CompletedTask;
    }

    private bool MatchesProjectDeny(ToolCall call)
    {
        if (_projectDenyCommands.Length == 0) return false;
        if (!call.Arguments.TryGetValue("command", out var cmd) || cmd == null) return false;
        var command = (cmd.ToString() ?? string.Empty).Trim();
        return _projectDenyCommands.Any(d =>
            command.StartsWith(d, StringComparison.OrdinalIgnoreCase) &&
            (command.Length == d.Length || char.IsWhiteSpace(command[d.Length]) || PermissionClassifier.ContainsShellControlOperator(command)));
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
                    _scoped[PermissionScope.Organization] = _scoped[PermissionScope.Organization] with { Mode = orgMode, IsModeConfigured = true };
                if (perms.TryGetProperty("project", out var proj) && proj.TryGetProperty("mode", out var pm) && Enum.TryParse<PermissionMode>(pm.GetString(), out var projMode))
                    _scoped[PermissionScope.Project] = _scoped[PermissionScope.Project] with { Mode = projMode, IsModeConfigured = true };
                if (perms.TryGetProperty("personal", out var pers) && pers.TryGetProperty("mode", out var persm) && Enum.TryParse<PermissionMode>(persm.GetString(), out var pMode))
                    _scoped[PermissionScope.Personal] = _scoped[PermissionScope.Personal] with { Mode = pMode, IsModeConfigured = true };
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
        // 0. The project's own deny-list beats everything, including allow-rules.
        if (risk != RiskLevel.Read && MatchesProjectDeny(call))
            return Task.FromResult(PermissionDecision.Deny);

        // 1. Allow-rules (explicit per-command allow) always win.
        if (MatchesAllowRule(call))
            return Task.FromResult(PermissionDecision.Allow);

        // 2. Read-only session blocks writes/execute — checked BEFORE
        // granular rights, so a read-only session can't be unlocked by
        // someone ticking "Edit" in the granular-rights checkboxes
        // (issue 5: the checkboxes used to be checked first).
        if (options.IsReadOnly && risk != RiskLevel.Read)
            return Task.FromResult(PermissionDecision.Deny);

        // 2b. Plan mode (requested by the run itself or configured) is
        // strictly read-only and is checked BEFORE granular rights, so ticking
        // "Edit"/"Execute" can't punch through it.
        if (risk != RiskLevel.Read &&
            (options.PermissionMode == PermissionMode.Plan ||
             GetModeAsync(agentId, ct).GetAwaiter().GetResult() == PermissionMode.Plan))
            return Task.FromResult(PermissionDecision.Deny);

        // 3. Granular rights override the coarse mode when set.
        if (options.Rights != null)
        {
            var granular = GranularRightsDecision(risk, options.Rights);
            if (granular != null)
                return Task.FromResult(granular.Value);
        }

        // 4. Mode-based decision.
        var mode = GetModeAsync(agentId, ct).GetAwaiter().GetResult();
        if (_projectCeiling is { } ceiling && PermissivenessRank(mode) > PermissivenessRank(ceiling))
            mode = ceiling;
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
        // Personal, if explicitly configured, wins over Project, which wins
        // over Organization's own mode. But an explicitly configured
        // Organization mode is a CEILING, not just a fallback: a project or
        // personal setting can never end up MORE permissive than a policy
        // the organization deliberately set (previously Personal overrode
        // Organization unconditionally, so an org policy could never be
        // enforced).
        var personal = _scoped[PermissionScope.Personal];
        var project = _scoped[PermissionScope.Project];
        var org = _scoped[PermissionScope.Organization];

        var requested = personal.IsModeConfigured ? personal.Mode
            : project.IsModeConfigured ? project.Mode
            : org.Mode;

        if (org.IsModeConfigured && PermissivenessRank(requested) > PermissivenessRank(org.Mode))
            return org.Mode;

        return requested;
    }

    /// <summary>
    /// How much a mode lets run without asking, from most restrictive (0) to
    /// least (3). Used to enforce the Organization-scope ceiling above.
    /// </summary>
    private static int PermissivenessRank(PermissionMode mode) => mode switch
    {
        PermissionMode.Plan => 0,
        PermissionMode.Ask => 1,
        PermissionMode.AutoEdit => 2,
        PermissionMode.FullAuto => 3,
        _ => 1
    };

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
                    var command = (cmd.ToString() ?? string.Empty).Trim();
                    // Whole-word prefix only ("git status" must not allow
                    // "git status; rm -rf ." or "git statusx"), and never a
                    // command that chains further shell commands.
                    if (command.StartsWith(rule.CommandPattern, System.StringComparison.OrdinalIgnoreCase) &&
                        (command.Length == rule.CommandPattern.Length || char.IsWhiteSpace(command[rule.CommandPattern.Length])) &&
                        !PermissionClassifier.ContainsShellControlOperator(command))
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