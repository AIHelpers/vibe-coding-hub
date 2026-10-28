using System.Diagnostics;
using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Tools.Shell;

/// <summary>Settings for <see cref="DockerCommandSandbox"/>.</summary>
public sealed record DockerSandboxOptions
{
    public bool Enabled { get; init; }
    public string Image { get; init; } = "ubuntu:24.04";
    /// <summary>Allow network inside the container (package restore, npm install). Off by default.</summary>
    public bool Network { get; init; }
    public string Memory { get; init; } = "4g";
    public string Cpus { get; init; } = "2";

    /// <summary>
    /// Reads AIAGENT_SANDBOX (docker|off), AIAGENT_SANDBOX_IMAGE, AIAGENT_SANDBOX_NETWORK (on|off),
    /// AIAGENT_SANDBOX_MEMORY, AIAGENT_SANDBOX_CPUS. Everything else is the default.
    /// </summary>
    public static DockerSandboxOptions FromEnvironment() => FromConfiguration(null);

    /// <summary>
    /// The settings in force: the values saved in Settings → Safety, where every AIAGENT_SANDBOX* environment variable that is
    /// set overrides its own value (a variable set to "off" switches the sandbox off even if Settings turned it on).
    /// </summary>
    public static DockerSandboxOptions FromConfiguration(SandboxConfiguration? configured, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        string? Env(string name) => getEnv(name) is { Length: > 0 } v ? v : null;
        static string? NonBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var saved = configured ?? new SandboxConfiguration();
        var defaults = new DockerSandboxOptions();
        return new DockerSandboxOptions
        {
            Enabled = Env("AIAGENT_SANDBOX") is { } mode
                ? string.Equals(mode, "docker", StringComparison.OrdinalIgnoreCase)
                : saved.Enabled,
            Image = Env("AIAGENT_SANDBOX_IMAGE") ?? NonBlank(saved.Image) ?? defaults.Image,
            Network = Env("AIAGENT_SANDBOX_NETWORK") is { } network
                ? string.Equals(network, "on", StringComparison.OrdinalIgnoreCase)
                : saved.Network,
            Memory = Env("AIAGENT_SANDBOX_MEMORY") ?? NonBlank(saved.Memory) ?? defaults.Memory,
            Cpus = Env("AIAGENT_SANDBOX_CPUS") ?? NonBlank(saved.Cpus) ?? defaults.Cpus
        };
    }
}

/// <summary>
/// Runs each command in a throw-away Docker container: workspace mounted read-write at /workspace, no network by
/// default, memory/CPU capped, all capabilities dropped, no privilege escalation. Only the workspace is writable
/// from inside; nothing else on the host is visible. Note: env vars set via execute_command's env parameter are not
/// forwarded into the container, and the image must contain the toolchain the project needs.
/// </summary>
public class DockerCommandSandbox : ICommandSandbox
{
    private readonly Func<DockerSandboxOptions> _options;
    private string? _availability;
    private bool _checked;

    /// <summary>A sandbox with fixed settings (null = the environment variables, read once).</summary>
    public DockerCommandSandbox(DockerSandboxOptions? options = null)
    {
        var fixedOptions = options ?? DockerSandboxOptions.FromEnvironment();
        _options = () => fixedOptions;
    }

    /// <summary>
    /// A sandbox whose settings are read on every use, so a change in Settings applies to the next command without a restart.
    /// </summary>
    public DockerCommandSandbox(Func<DockerSandboxOptions> optionsProvider) => _options = optionsProvider;

    public bool Enabled => _options().Enabled;

    public string Description
    {
        get
        {
            var o = _options();
            return $"docker (image {o.Image}, network {(o.Network ? "on" : "off")})";
        }
    }

    public async Task<string?> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (_checked) return _availability;
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("version");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("{{.Server.Version}}");
            using var p = Process.Start(psi);
            if (p == null) _availability = "Docker could not be started.";
            else
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                var err = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(cts.Token);
                _availability = p.ExitCode == 0 ? null : $"Docker is installed but its daemon is not reachable: {(await err).Trim()}";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _availability = "Docker did not respond within 15 seconds.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _availability = $"Docker is not available: {ex.Message}";
        }
        _checked = true;
        return _availability;
    }

    public SandboxLaunch Wrap(string command, string workspaceRoot, string workingDirectory)
        => new("docker", BuildArguments(command, workspaceRoot, workingDirectory));

    internal IReadOnlyList<string> BuildArguments(string command, string workspaceRoot, string workingDirectory)
    {
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(workingDirectory)).Replace('\\', '/');
        var containerDir = relative == "." || relative.StartsWith("..", StringComparison.Ordinal)
            ? "/workspace"
            : "/workspace/" + relative;

        var o = _options();
        var args = new List<string>
        {
            "run", "--rm", "-i",
            "--network", o.Network ? "bridge" : "none",
            "--memory", o.Memory,
            "--cpus", o.Cpus,
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "-v", $"{root}:/workspace",
            "-w", containerDir,
            o.Image,
            "bash", "-c", command
        };
        return args;
    }
}
