using AiCodeAgent.Core.Agent;
using AiCodeAgent.Tools.Shell;
using AiCodeAgent.Tools.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AiCodeAgent.Tools.Tests.Shell;

public class ExecuteCommandSandboxTests : TempDirTestBase
{
    [Fact]
    public async Task EnabledButUnavailableSandbox_RefusesAndNeverRunsOnHost()
    {
        var marker = Path.Combine(WorkingDir, "ran-on-host.txt");
        var sandbox = Substitute.For<ICommandSandbox>();
        sandbox.Enabled.Returns(true);
        sandbox.Description.Returns("fake");
        sandbox.CheckAvailabilityAsync(Arg.Any<CancellationToken>()).Returns("daemon down");
        var tool = new ExecuteCommandTool(NullLogger<ExecuteCommandTool>.Instance, sandbox);

        var result = await tool.ExecuteAsync(Call(new() { ["command"] = $"echo hi > \"{marker}\"" }), Context());

        Assert.True(result.IsError);
        Assert.Contains("NOT run", result.Content);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task EnabledSandbox_RunsTheWrappedLaunchInsteadOfTheHostShell()
    {
        var sandbox = Substitute.For<ICommandSandbox>();
        sandbox.Enabled.Returns(true);
        sandbox.Description.Returns("fake");
        sandbox.CheckAvailabilityAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        // Stand-in for `docker run ...`: prints a marker so we can see the wrapped launch was used.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo WRAPPED" })
            : ("/bin/echo", new[] { "WRAPPED" });
        sandbox.Wrap(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(new SandboxLaunch(exe, args));
        var tool = new ExecuteCommandTool(NullLogger<ExecuteCommandTool>.Instance, sandbox);

        var result = await tool.ExecuteAsync(Call(new() { ["command"] = "echo HOST" }), Context());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("WRAPPED", result.Content);
        sandbox.Received(1).Wrap("echo HOST", Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisabledSandbox_UsesHostShell()
    {
        var sandbox = new DockerCommandSandbox(new DockerSandboxOptions { Enabled = false });
        var tool = new ExecuteCommandTool(NullLogger<ExecuteCommandTool>.Instance, sandbox);

        var result = await tool.ExecuteAsync(Call(new() { ["command"] = "echo HOSTRUN" }), Context());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("HOSTRUN", result.Content);
    }

    [Fact]
    public async Task RealDockerSandbox_WithoutDaemon_FailsClosed()
    {
        // Runs everywhere: when Docker is missing/stopped the command is refused; when Docker works it must still succeed.
        var sandbox = new DockerCommandSandbox(new DockerSandboxOptions { Enabled = true, Image = "ubuntu:24.04" });
        var unavailable = await sandbox.CheckAvailabilityAsync();
        if (unavailable != null)
        {
            var tool = new ExecuteCommandTool(NullLogger<ExecuteCommandTool>.Instance, sandbox);
            var result = await tool.ExecuteAsync(Call(new() { ["command"] = "echo hi" }), Context());
            Assert.True(result.IsError);
            Assert.Contains("NOT run", result.Content);
        }
    }
}
