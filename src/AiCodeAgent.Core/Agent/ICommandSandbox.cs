namespace AiCodeAgent.Core.Agent;

/// <summary>The process to start instead of the plain shell when a sandbox is active.</summary>
public sealed record SandboxLaunch(string Executable, IReadOnlyList<string> Arguments);

/// <summary>
/// Runs shell commands inside an isolated environment (e.g. a Docker container with no network and only the
/// workspace mounted). Consumed by execute_command and verify_changes.
/// </summary>
public interface ICommandSandbox
{
    /// <summary>True when sandboxing is switched on. When on, commands must NOT fall back to the host shell.</summary>
    bool Enabled { get; }

    /// <summary>Human-readable description for logs/UI, e.g. "docker (image ubuntu:24.04, network off)".</summary>
    string Description { get; }

    /// <summary>Returns why the sandbox cannot be used (e.g. Docker not installed), or null when it is ready.</summary>
    Task<string?> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>
    /// Builds the launch for <paramref name="command"/>. <paramref name="workspaceRoot"/> is mounted read-write;
    /// <paramref name="workingDirectory"/> must be inside it and becomes the command's cwd inside the sandbox.
    /// </summary>
    SandboxLaunch Wrap(string command, string workspaceRoot, string workingDirectory);
}
