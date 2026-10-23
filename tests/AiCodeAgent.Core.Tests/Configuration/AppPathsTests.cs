using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Tests.Hermetic;
using Xunit;

namespace AiCodeAgent.Core.Tests.Configuration;

public class AppPathsTests
{
    [Fact]
    public void ResolveUserRoot_DefaultsToDotAiagentUnderHome()
    {
        Assert.Equal(Path.Combine("/home/roman", ".aiagent"), AppPaths.ResolveUserRoot(null, "/home/roman"));
    }

    [Fact]
    public void ResolveUserRoot_WithoutHome_FallsBackToRelativeFolder()
    {
        Assert.Equal(".aiagent", AppPaths.ResolveUserRoot(null, null));
    }

    [Fact]
    public void ResolveUserRoot_EnvironmentOverrideWins()
    {
        var overridden = Path.Combine(Path.GetTempPath(), "portable-aiagent");

        Assert.Equal(Path.GetFullPath(overridden), AppPaths.ResolveUserRoot(overridden, "/home/roman"));
    }

    [Fact]
    public void ResolveUserRoot_BlankOverrideIsIgnored()
    {
        Assert.Equal(Path.Combine("/home/roman", ".aiagent"), AppPaths.ResolveUserRoot("   ", "/home/roman"));
    }

    [Fact]
    public void FeatureFolders_LiveUnderUserRoot()
    {
        var root = AppPaths.UserRoot;

        Assert.Equal(Path.Combine(root, "skills"), AppPaths.Skills);
        Assert.Equal(Path.Combine(root, "presets"), AppPaths.Presets);
        Assert.Equal(Path.Combine(root, "pricing.json"), AppPaths.Pricing);
    }

    [Fact]
    public void UserRoot_IsTheTemporaryHomeDuringTests()
    {
        Assert.Equal(Path.GetFullPath(HermeticTestHome.TempRoot), Path.GetFullPath(AppPaths.UserRoot));
    }
}
