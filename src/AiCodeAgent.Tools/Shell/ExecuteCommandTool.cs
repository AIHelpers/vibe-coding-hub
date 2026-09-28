using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Shell;

public class ExecuteCommandTool : BaseTool
{
    /// <summary>Cap on captured stdout/stderr so a runaway or chatty command can't exhaust memory or blow the context window.</summary>
    private const int MaxOutputChars = 30_000; // ~30 KB

    // Commands that are inherently dangerous regardless of arguments
    private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "del", "erase", "format", "dd", "mkfs", "fdisk",
        "shutdown", "reboot", "halt", "sudo", "su", "chmod", "chown",
        "chattr", "passwd", "kill", "pkill", "taskkill", "reg", "regedit",
        "mount", "umount", "fstrim", "pvcreate", "vgcreate", "lvcreate"
    };

    // Regex patterns for dangerous command patterns (more robust than substring matching)
    private static readonly Regex[] DangerousPatterns =
    [
        new(@"\brm\s+-[rR]f?\s+[/~]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brm\s+-[rR]f?\s+\.", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@">\s*/dev/sd[a-z]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bmkfs\.\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdd\s+if=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdd\s+of=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@":\(\)\s*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled), // fork bomb
        new(@"\bchmod\s+777\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bchmod\s+-R\s+777", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bchown\s+-R\s+0:0\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bchown\s+-R\s+\w+:\w+\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bsudo\s+(rm|dd|shutdown|reboot|halt|mkfs\S*|fdisk\S*|format\S*|kill\S*|pkill\S*|taskkill\S*|chmod\S*|chown\S*|passwd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public ExecuteCommandTool(ILogger<ExecuteCommandTool> logger) : base(logger) { }

    public override string Name => "execute_command";
    public override string Description =>
        "Execute a shell command and return its output. " +
        "Use for building, testing, running scripts, git operations, etc.";
    public override RiskLevel Risk => RiskLevel.Execute;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["command"] = new() { Type = "string", Description = "Command to execute" },
                ["working_dir"] = new() { Type = "string", Description = "Working directory (default: current)" },
                ["timeout"] = new() { Type = "integer", Description = "Timeout in seconds (default: 30)" },
                ["env"] = new() { Type = "string", Description = "Additional environment variables as JSON" }
            },
            Required = ["command"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var command = GetArg<string>(call, "command");
        var workingDir = GetArg<string>(call, "working_dir", context.WorkingDirectory);
        var timeout = GetArg<int>(call, "timeout", 30);
        var envJson = GetArg<string>(call, "env", string.Empty);

        // The pattern/command denylist below is a HINT, not a gate: it's
        // trivially bypassed (`rm -fr /`, `find / -delete`,
        // `python -c "os.remove(...)"`, a differently-spelled flag, ...) and
        // it also blocked perfectly ordinary commands like `rm -rf ./build`.
        // The actual safety boundary is the permission/approval flow the
        // orchestrator runs BEFORE this tool is ever invoked — by the time
        // ExecuteAsync runs, a human (or the FullAuto classifier, which now
        // denies-by-default and rejects chaining/redirection) has already
        // decided this specific command may run. We still surface a match as
        // a warning so it's visible in the transcript.
        var dangerWarning = ContainsDangerousPattern(command) || ContainsDangerousCommand(command)
            ? "⚠ This command matched a known-dangerous pattern; review the output carefully.\n"
            : string.Empty;

        if (context.IsReadOnly && ContainsDangerousCommand(command))
            return Error("Potentially dangerous commands are disabled in read-only mode.");

        var resolvedDir = ResolvePath(workingDir, context);
        if (!Directory.Exists(resolvedDir))
            return Error($"Working directory not found: {workingDir}");

        try
        {
            var (executable, arguments) = ParseCommand(command);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = resolvedDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            // Use ArgumentList to prevent command injection
            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            // Add context environment
            foreach (var (key, value) in context.Environment)
                process.StartInfo.Environment[key] = value;

            // Add per-call environment overrides. This parameter was
            // advertised in the tool schema but previously never read.
            if (!string.IsNullOrWhiteSpace(envJson))
            {
                try
                {
                    var extraEnv = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(envJson);
                    if (extraEnv != null)
                    {
                        foreach (var (key, value) in extraEnv)
                            process.StartInfo.Environment[key] = value;
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Logger.LogWarning(ex, "Ignoring malformed `env` JSON for execute_command: {Env}", envJson);
                }
            }

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var stdoutTruncated = false;
            var stderrTruncated = false;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null || stdoutTruncated) return;
                if (stdoutBuilder.Length + e.Data.Length + 1 > MaxOutputChars)
                {
                    var remaining = Math.Max(0, MaxOutputChars - stdoutBuilder.Length);
                    stdoutBuilder.Append(e.Data.AsSpan(0, Math.Min(remaining, e.Data.Length)));
                    stdoutBuilder.Append("\n...[stdout truncated at 30 KB]");
                    stdoutTruncated = true;
                    return;
                }
                stdoutBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null || stderrTruncated) return;
                if (stderrBuilder.Length + e.Data.Length + 1 > MaxOutputChars)
                {
                    var remaining = Math.Max(0, MaxOutputChars - stderrBuilder.Length);
                    stderrBuilder.Append(e.Data.AsSpan(0, Math.Min(remaining, e.Data.Length)));
                    stderrBuilder.Append("\n...[stderr truncated at 30 KB]");
                    stderrTruncated = true;
                    return;
                }
                stderrBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Kill the WHOLE process tree — a bare process.Kill() only
                // killed the shell wrapper (cmd.exe/bash -c), leaving
                // whatever it had spawned running in the background forever.
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Error($"Command timed out after {timeout} seconds: {command}");
            }

            var stdout = stdoutBuilder.ToString().Trim();
            var stderr = stderrBuilder.ToString().Trim();
            var exitCode = process.ExitCode;

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(dangerWarning))
                sb.Append(dangerWarning);
            sb.AppendLine($"$ {command}");
            sb.AppendLine($"Exit code: {exitCode}");
            if (!string.IsNullOrEmpty(stdout))
            {
                sb.AppendLine("STDOUT:");
                sb.AppendLine(stdout);
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                sb.AppendLine("STDERR:");
                sb.AppendLine(stderr);
            }

            return exitCode == 0
                ? Success(sb.ToString())
                : new ToolResult
                {
                    ToolName = Name,
                    Content = sb.ToString(),
                    IsError = exitCode != 0
                };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error executing command: {Command}", command);
            return Error($"Failed to execute command: {ex.Message}");
        }
    }

    private static (string executable, string[] args) ParseCommand(string command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd.exe", ["/c", command]);
        return ("/bin/bash", ["-c", command]);
    }

    /// <summary>
    /// Checks if any word in the command (split by shell-safe delimiters) is a known dangerous command.
    /// This is more robust than checking only the first word.
    /// </summary>
    private static bool ContainsDangerousCommand(string command)
    {
        // Split by common shell delimiters to find all commands in the pipeline/chain
        var tokens = command.Split([' ', '\t', '|', ';', '&', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            // Strip leading path components (e.g., /usr/bin/rm -> rm)
            var commandName = Path.GetFileNameWithoutExtension(token);
            if (DangerousCommands.Contains(commandName))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the command matches any dangerous regex patterns.
    /// Uses compiled regex for robust pattern matching that can't be easily bypassed.
    /// </summary>
    private static bool ContainsDangerousPattern(string command)
    {
        foreach (var pattern in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
                return true;
        }
        return false;
    }
}
