using AiCodeAgent.Tools.Shell;

namespace AiCodeAgent.Tools.Tests.Shell;

public class DockerCommandSandboxTests
{
    private static DockerCommandSandbox Sandbox(bool network = false) =>
        new(new DockerSandboxOptions { Enabled = true, Image = "img:1", Network = network });

    [Fact]
    public void Wrap_UsesDocker_WithIsolationFlags()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ws"));
        var launch = Sandbox().Wrap("echo hi", root, root);

        Assert.Equal("docker", launch.Executable);
        var args = launch.Arguments.ToList();
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Equal("none", args[args.IndexOf("--network") + 1]);
        Assert.Equal("ALL", args[args.IndexOf("--cap-drop") + 1]);
        Assert.Contains("no-new-privileges", args);
        Assert.Equal("/workspace", args[args.IndexOf("-w") + 1]);
        Assert.Equal($"{root}:/workspace", args[args.IndexOf("-v") + 1]);
        // The command is one argv element: no shell-injection through string concatenation.
        Assert.Equal(new[] { "img:1", "bash", "-c", "echo hi" }, args.TakeLast(4));
    }

    [Fact]
    public void Wrap_MapsSubdirectoryToContainerPath()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ws"));
        var args = Sandbox().Wrap("ls", root, Path.Combine(root, "src", "app")).Arguments.ToList();
        Assert.Equal("/workspace/src/app", args[args.IndexOf("-w") + 1]);
    }

    [Fact]
    public void Wrap_NetworkOn_UsesBridge()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var args = Sandbox(network: true).Wrap("ls", root, root).Arguments.ToList();
        Assert.Equal("bridge", args[args.IndexOf("--network") + 1]);
    }

    [Fact]
    public void Enabled_ReflectsOptions()
    {
        Assert.True(Sandbox().Enabled);
        Assert.False(new DockerCommandSandbox(new DockerSandboxOptions()).Enabled);
    }
}
