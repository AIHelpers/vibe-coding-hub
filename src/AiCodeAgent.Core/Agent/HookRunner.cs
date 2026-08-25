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
    private IReadOnlyList<HookDefinition> _hooks = Array.Empty<HookDefinition>();

    public HookRegistry(ILogger<HookRegistry>? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<HookDefinition> Hooks => _hooks;

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
            var command = ExpandPlaceholders(hook.Command, context);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = GetShell(),
                Arguments = GetShellArgs(command),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

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
                    Deny = false,
                    Success = false,
                    ErrorMessage = $"Hook timed out after {hook.TimeoutSeconds}s",
                    TimedOut = true
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
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static string ExpandPlaceholders(string command, HookContext context)
    {
        var sb = new System.Text.StringBuilder(command);
        sb.Replace("{session}", context.SessionId ?? string.Empty);

        if (context.ToolCall is not null)
        {
            sb.Replace("{tool}", context.ToolCall.Name ?? string.Empty);
            sb.Replace("{args}", System.Text.Json.JsonSerializer.Serialize(context.ToolCall.Arguments));
        }
        else
        {
            sb.Replace("{tool}", string.Empty);
            sb.Replace("{args}", string.Empty);
        }

        if (context.ToolResult is not null)
        {
            sb.Replace("{result}", context.ToolResult.Content ?? string.Empty);
        }
        else
        {
            sb.Replace("{result}", string.Empty);
        }

        if (context.Error is not null)
        {
            sb.Replace("{error}", context.Error.Message ?? string.Empty);
        }
        else
        {
            sb.Replace("{error}", string.Empty);
        }

        return sb.ToString();
    }

    private static string GetShell() =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe"
            : "/bin/sh";

    private static string GetShellArgs(string command) =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? $"/c {command}"
            : $"-c \"{command.Replace("\"", "\\\"")}\"";
}