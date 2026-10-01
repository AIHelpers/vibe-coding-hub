using System.Diagnostics;
using System.Text;
using AiCodeAgent.Core.Agent;

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
    /// AIAGENT_SANDBOX_MEMORY, AIAGENT_SANDBOX_CPUS.
    /// </summary>
    public static DockerSandboxOptions FromEnvironment()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var defaults = new DockerSandboxOptions();
        return defaults with
        {
            Enabled = string.Equals(Env("AIAGENT_SANDBOX"), "docker", StringComparison.OrdinalIgnoreCase),
            Image = Env("AIAGENT_SANDBOX_IMAGE") ?? defaults.Image,
            Network = string.Equals(Env("AIAGENT_SANDBOX_NETWORK"), "on", StringComparison.OrdinalIgnoreCase),
            Memory = Env("AIAGENT_SANDBOX_MEMORY") ?? defaults.Memory,
            Cpus = Env("AIAGENT_SANDBOX_CPUS") ?? defaults.Cpus
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
    private readonly DockerSandboxOptions _options;
    private string? _availability;
    private bool _checked;

    public DockerCommandSandbox(DockerSandboxOptions? options = null) => _options = options ?? DockerSandboxOptions.FromEnvironment();

    public bool Enabled => _options.Enabled;

    public string Description =>
        $"docker (image {_options.Image}, network {(_options.Network ? "on" : "off")})";

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

        var args = new List<string>
        {
            "run", "--rm", "-i",
            "--network", _options.Network ? "bridge" : "none",
            "--memory", _options.Memory,
            "--cpus", _options.Cpus,
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "-v", $"{root}:/workspace",
            "-w", containerDir,
            _options.Image,
            "bash", "-c", command
        };
        return args;
    }
}
