using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Tools.Shell;

namespace AiCodeAgent.Tools.Tests.Shell;

/// <summary>E4-S5: the sandbox settings come from config.json; the AIAGENT_SANDBOX* variables still override them.</summary>
public class SandboxConfigurationTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static SandboxConfiguration Saved() => new()
    {
        Enabled = true,
        Image = "node:22",
        Network = true,
        Memory = "8g",
        Cpus = "4"
    };

    [Fact]
    public void SavedValues_AreUsed_WhenNoVariableIsSet()
    {
        var o = DockerSandboxOptions.FromConfiguration(Saved(), Env());

        Assert.True(o.Enabled);
        Assert.Equal("node:22", o.Image);
        Assert.True(o.Network);
        Assert.Equal("8g", o.Memory);
        Assert.Equal("4", o.Cpus);
    }

    [Fact]
    public void WithoutConfigurationOrVariables_TheSandboxIsOffWithTheDefaults()
    {
        var o = DockerSandboxOptions.FromConfiguration(null, Env());

        Assert.False(o.Enabled);
        Assert.Equal("ubuntu:24.04", o.Image);
        Assert.False(o.Network);
        Assert.Equal("4g", o.Memory);
        Assert.Equal("2", o.Cpus);
    }

    [Fact]
    public void EachVariable_OverridesOnlyItsOwnValue()
    {
        var o = DockerSandboxOptions.FromConfiguration(Saved(), Env(("AIAGENT_SANDBOX_IMAGE", "python:3.13"), ("AIAGENT_SANDBOX_CPUS", "1")));

        Assert.Equal("python:3.13", o.Image);
        Assert.Equal("1", o.Cpus);
        Assert.True(o.Enabled);          // not overridden
        Assert.Equal("8g", o.Memory);    // not overridden
    }

    [Fact]
    public void SandboxVariableSetToOff_SwitchesTheSandboxOff_EvenIfSettingsTurnedItOn()
    {
        Assert.False(DockerSandboxOptions.FromConfiguration(Saved(), Env(("AIAGENT_SANDBOX", "off"))).Enabled);
        Assert.True(DockerSandboxOptions.FromConfiguration(new SandboxConfiguration(), Env(("AIAGENT_SANDBOX", "docker"))).Enabled);
    }

    [Fact]
    public void NetworkVariable_OnAndOff_BothWin()
    {
        Assert.False(DockerSandboxOptions.FromConfiguration(Saved(), Env(("AIAGENT_SANDBOX_NETWORK", "off"))).Network);
        Assert.True(DockerSandboxOptions.FromConfiguration(new SandboxConfiguration(), Env(("AIAGENT_SANDBOX_NETWORK", "on"))).Network);
    }

    [Fact]
    public void BlankSavedValues_FallBackToTheDefaults()
    {
        var o = DockerSandboxOptions.FromConfiguration(new SandboxConfiguration { Image = " ", Memory = "", Cpus = "  " }, Env());

        Assert.Equal("ubuntu:24.04", o.Image);
        Assert.Equal("4g", o.Memory);
        Assert.Equal("2", o.Cpus);
    }

    [Fact]
    public void TheSandbox_ReadsItsSettingsOnEveryUse_SoAChangeNeedsNoRestart()
    {
        var config = new SandboxConfiguration { Enabled = false, Image = "first:1" };
        var sandbox = new DockerCommandSandbox(() => DockerSandboxOptions.FromConfiguration(config, Env()));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ws"));

        Assert.False(sandbox.Enabled);

        config.Enabled = true;
        config.Image = "second:2";
        config.Network = true;

        Assert.True(sandbox.Enabled);
        var args = sandbox.Wrap("echo hi", root, root).Arguments.ToList();
        Assert.Equal("bridge", args[args.IndexOf("--network") + 1]);
        Assert.Contains("second:2", args);
        Assert.Contains("second:2", sandbox.Description);
    }
}
