using System.Text.Json;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// File-based <see cref="IHookRegistry"/>. Loads hook definitions from
/// global <c>~/.aiagent/settings.json</c> and project
/// <c>.aiagent/settings.json</c>, merging them (project overrides global
/// for the same Name).
/// </summary>
public class HookRegistry : IHookRegistry
{
    private readonly ILogger<HookRegistry>? _logger;
    private readonly bool _requireProjectTrust;
    private readonly string _trustFilePath;
    private IReadOnlyList<HookDefinition> _hooks = Array.Empty<HookDefinition>();
    private IReadOnlyList<HookDefinition> _untrusted = Array.Empty<HookDefinition>();

    /// <param name="requireProjectTrust">
    /// When true (default) hooks from a project's settings file only load once the user has trusted that
    /// exact file content — opening a cloned repository must never silently execute its shell commands.
    /// </param>
    public HookRegistry(ILogger<HookRegistry>? logger = null, bool requireProjectTrust = true, string? trustFilePath = null)
    {
        _logger = logger;
        _requireProjectTrust = requireProjectTrust;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _trustFilePath = trustFilePath ?? (string.IsNullOrEmpty(home)
            ? Path.Combine(".aiagent", "trusted-hook-projects.json")
            : Path.Combine(home, ".aiagent", "trusted-hook-projects.json"));
    }

    public IReadOnlyList<HookDefinition> Hooks => _hooks;

    public IReadOnlyList<HookDefinition> UntrustedProjectHooks => _untrusted;

    public async Task TrustProjectAsync(string? workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return;
        var settingsPath = Path.Combine(workingDirectory, ".aiagent", "settings.json");
        if (!File.Exists(settingsPath)) return;

        var hash = await ComputeHashAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        var trusted = ReadTrustFile();
        trusted[Path.GetFullPath(workingDirectory)] = hash;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_trustFilePath) ?? ".");
            await File.WriteAllTextAsync(_trustFilePath, JsonSerializer.Serialize(trusted), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist hook trust to {Path}", _trustFilePath);
        }

        await LoadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private Dictionary<string, string> ReadTrustFile()
    {
        try
        {
            if (File.Exists(_trustFilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_trustFilePath));
                if (d != null) return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read hook trust file {Path}", _trustFilePath);
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<HookDefinition>> LoadAsync(string? workingDirectory, CancellationToken cancellationToken = default)
    {
        var merged = new Dictionary<string, HookDefinition>(StringComparer.OrdinalIgnoreCase);

        // Global settings
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSettingsPath = string.IsNullOrEmpty(home)
            ? Path.Combine(".aiagent", "settings.json")
            : Path.Combine(home, ".aiagent", "settings.json");

        await LoadFileIntoAsync(globalSettingsPath, merged, cancellationToken).ConfigureAwait(false);

        // Project settings
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var projectSettingsPath = Path.Combine(workingDirectory, ".aiagent", "settings.json");
            _untrusted = Array.Empty<HookDefinition>();
            if (_requireProjectTrust && File.Exists(projectSettingsPath))
            {
                var hash = await ComputeHashAsync(projectSettingsPath, cancellationToken).ConfigureAwait(false);
                var trusted = ReadTrustFile();
                if (!trusted.TryGetValue(Path.GetFullPath(workingDirectory), out var known) ||
                    !string.Equals(known, hash, StringComparison.OrdinalIgnoreCase))
                {
                    // Parse (but do not activate) so the UI can show exactly what would run.
                    var pending = new Dictionary<string, HookDefinition>(StringComparer.OrdinalIgnoreCase);
                    await LoadFileIntoAsync(projectSettingsPath, pending, cancellationToken).ConfigureAwait(false);
                    _untrusted = pending.Values.ToList();
                    _logger?.LogWarning("Ignoring {Count} project hook(s) from untrusted {Path}", _untrusted.Count, projectSettingsPath);
                    _hooks = merged.Values.ToList();
                    return _hooks;
                }
            }
            await LoadFileIntoAsync(projectSettingsPath, merged, cancellationToken).ConfigureAwait(false);
        }

        _hooks = merged.Values.ToList();
        return _hooks;
    }

    private async Task LoadFileIntoAsync(string path, Dictionary<string, HookDefinition> merged, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hooks", out var hooksEl) || hooksEl.ValueKind != JsonValueKind.Array)
                return;

            foreach (var el in hooksEl.EnumerateArray())
            {
                var def = ParseHook(el);
                if (def is null) continue;
                merged[def.Name] = def; // project overrides global for same name
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load hooks from {Path}", path);
        }
    }

    private static HookDefinition? ParseHook(JsonElement el)
    {
        try
        {
            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var eventStr = el.TryGetProperty("event", out var ev) ? ev.GetString() ?? "PreToolUse" : "PreToolUse";
            if (!Enum.TryParse<HookEvent>(eventStr, true, out var hookEvent))
                hookEvent = HookEvent.PreToolUse;

            var command = el.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return null;

            var blocking = el.TryGetProperty("blocking", out var b) && b.GetBoolean();
            var timeout = el.TryGetProperty("timeoutSeconds", out var t) ? t.GetInt32() : 30;
            var toolFilter = el.TryGetProperty("toolFilter", out var tf) ? tf.GetString() : null;

            return new HookDefinition
            {
                Name = name,
                Event = hookEvent,
                Command = command,
                Blocking = blocking,
                TimeoutSeconds = timeout,
                ToolFilter = toolFilter
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Default <see cref="IHookRunner"/>. Executes hook commands as shell
/// processes, captures output, and determines deny for blocking hooks.
/// </summary>
public class HookRunner : IHookRunner
{
    private readonly IHookRegistry _registry;
    private readonly ILogger<HookRunner>? _logger;

    public HookRunner(IHookRegistry registry, ILogger<HookRunner>? logger = null)
    {
        _registry = registry;
        _logger = logger;
    }

    public IReadOnlyList<HookDefinition> ListHooks() => _registry.Hooks;

    public async Task<HookRunResult> RunAsync(HookEvent hookEvent, HookContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<HookResult>();
        var hooks = _registry.Hooks.Where(h => h.Event == hookEvent).ToList();

        foreach (var hook in hooks)
        {
            // Apply tool filter for PreToolUse/PostToolUse
            if (!string.IsNullOrEmpty(hook.ToolFilter)
                && context.ToolCall is not null
                && !string.Equals(hook.ToolFilter, context.ToolCall.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = await RunOneAsync(hook, context, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            // If a blocking hook denies, we can stop early
            if (hook.Blocking && result.Deny)
                break;
        }

        return new HookRunResult { Results = results };
    }

    private async Task<HookResult> RunOneAsync(HookDefinition hook, HookContext context, CancellationToken cancellationToken)
    {
        try
        {
            var command = ExpandPlaceholders(hook.Command);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = GetShell(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ApplyShellArgs(psi, command);

            // Values derived from the model / tool output are passed as
            // environment variables, never spliced into the command text
            // (that would let a crafted tool argument inject shell syntax).
            foreach (var (key, value) in BuildHookEnvironment(context))
                psi.Environment[key] = value;

            if (!string.IsNullOrEmpty(context.WorkingDirectory) && Directory.Exists(context.WorkingDirectory))
                psi.WorkingDirectory = context.WorkingDirectory;

            using var process = new System.Diagnostics.Process { StartInfo = psi };

            var outputBuilder = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMs = hook.TimeoutSeconds > 0 ? hook.TimeoutSeconds * 1000 : System.Threading.Timeout.Infinite;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out
                try { process.Kill(true); } catch { }
                return new HookResult
                {
                    Definition = hook,
                    Output = outputBuilder.ToString(),
                    ExitCode = -1,
                    // A blocking hook is a gate: if it can't answer in time the
                    // safe outcome is "blocked", not "allowed".
                    Deny = hook.Blocking,
                    Success = false,
                    ErrorMessage = $"Hook timed out after {hook.TimeoutSeconds}s",
                    TimedOut = true
                };
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return new HookResult
                {
                    Definition = hook,
                    Output = outputBuilder.ToString(),
                    ExitCode = -1,
                    Deny = hook.Blocking,
                    Success = false,
                    ErrorMessage = "Hook cancelled"
                };
            }

            var output = outputBuilder.ToString().Trim();
            var exitCode = process.ExitCode;
            var deny = hook.Blocking && (exitCode != 0 || output.Contains("DENY", StringComparison.OrdinalIgnoreCase));

            return new HookResult
            {
                Definition = hook,
                Output = output,
                ExitCode = exitCode,
                Deny = deny,
                Success = exitCode == 0
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hook {Name} failed to execute", hook.Name);
            return new HookResult
            {
                Definition = hook,
                ExitCode = -1,
                Deny = hook.Blocking, // failed-to-run gate fails closed
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static readonly bool IsWindows =
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

    private const int MaxEnvValueChars = 8000;

    /// <summary>
    /// Replaces <c>{session}</c>, <c>{tool}</c>, <c>{args}</c>, <c>{result}</c> and <c>{error}</c> with a
    /// reference to an environment variable holding the value. The shell expands the variable itself,
    /// so the value is never parsed as shell syntax.
    /// </summary>
    private static string ExpandPlaceholders(string command)
    {
        string Ref(string name) => IsWindows ? $"!{name}!" : $"${name}";
        return new System.Text.StringBuilder(command)
            .Replace("{session}", Ref("HOOK_SESSION"))
            .Replace("{tool}", Ref("HOOK_TOOL"))
            .Replace("{args}", Ref("HOOK_ARGS"))
            .Replace("{result}", Ref("HOOK_RESULT"))
            .Replace("{error}", Ref("HOOK_ERROR"))
            .ToString();
    }

    private static Dictionary<string, string> BuildHookEnvironment(HookContext context)
    {
        static string Cap(string? v) =>
            string.IsNullOrEmpty(v) ? string.Empty : (v.Length > MaxEnvValueChars ? v[..MaxEnvValueChars] : v);

        return new Dictionary<string, string>
        {
            ["HOOK_SESSION"] = Cap(context.SessionId),
            ["HOOK_TOOL"] = Cap(context.ToolCall?.Name),
            ["HOOK_ARGS"] = context.ToolCall is null ? string.Empty : Cap(System.Text.Json.JsonSerializer.Serialize(context.ToolCall.Arguments)),
            ["HOOK_RESULT"] = Cap(context.ToolResult?.Content),
            ["HOOK_ERROR"] = Cap(context.Error?.Message)
        };
    }

    private static string GetShell() => IsWindows ? "cmd.exe" : "/bin/sh";

    private static void ApplyShellArgs(System.Diagnostics.ProcessStartInfo psi, string command)
    {
        if (IsWindows)
        {
            // /V:ON enables !VAR! delayed expansion, which (unlike %VAR%) happens after parsing.
            psi.Arguments = $"/V:ON /c {command}";
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
    }
}
