using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Tests.Configuration;

/// <summary>E3-S2: what the wizard relies on in the configuration.</summary>
public sealed class FirstRunConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "firstrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task ConfigFileExisted_IsFalseOnTheFirstStart_ThenTrue()
    {
        var first = new ConfigurationService(_dir);
        await first.LoadAsync();
        Assert.False(first.ConfigFileExisted);
        Assert.True(File.Exists(Path.Combine(_dir, "config.json")));

        var second = new ConfigurationService(_dir);
        await second.LoadAsync();
        Assert.True(second.ConfigFileExisted);
    }

    [Fact]
    public async Task SaveAsync_KeepsThePlainKeyInMemory_ButWritesItEncrypted()
    {
        var service = new ConfigurationService(_dir);
        await service.LoadAsync();
        service.SetApiKey("openai", "sk-secret-123");

        await service.SaveAsync();

        Assert.Equal("sk-secret-123", service.Config.Providers["openai"].ApiKey);
        var onDisk = File.ReadAllText(Path.Combine(_dir, "config.json"));
        Assert.DoesNotContain("sk-secret-123", onDisk);

        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("sk-secret-123", reloaded.Config.Providers["openai"].ApiKey);
    }

    [Fact]
    public async Task WizardSettings_SurviveARoundTrip()
    {
        var service = new ConfigurationService(_dir);
        await service.LoadAsync();
        service.Config.Ui.SetupWizardDone = true;
        service.Config.Agent.DefaultPermissionMode = "AutoEdit";
        RecentFolderList.Push(service.Config.Ui.RecentFolders, _dir);
        await service.SaveAsync();

        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();

        Assert.True(reloaded.Config.Ui.SetupWizardDone);
        Assert.Equal("AutoEdit", reloaded.Config.Agent.DefaultPermissionMode);
        Assert.Equal(_dir, reloaded.Config.Ui.RecentFolders[0]);
    }

    [Fact]
    public void FreshConfig_HasTheWizardOpenAndAskAsPermissionMode()
    {
        var config = AppConfiguration.GetDefaults();

        Assert.False(config.Ui.SetupWizardDone);
        Assert.Equal("Ask", config.Agent.DefaultPermissionMode);
        Assert.Empty(config.Ui.RecentFolders);
    }

    [Fact]
    public void RecentFolderList_PutsNewestFirst_WithoutDuplicates_AndCaps()
    {
        var list = new List<string>();

        RecentFolderList.Push(list, @"C:\a");
        RecentFolderList.Push(list, @"C:\b");
        RecentFolderList.Push(list, @"c:\A\"); // same folder, other case, trailing slash

        Assert.Equal(new[] { @"c:\A", @"C:\b" }, list);

        for (var i = 0; i < 20; i++) RecentFolderList.Push(list, $@"C:\p{i}");
        Assert.Equal(RecentFolderList.DefaultMax, list.Count);
        Assert.Equal(@"C:\p19", list[0]);
    }

    [Fact]
    public void RecentFolderList_IgnoresBlank_AndListsOnlyExistingFolders()
    {
        var list = new List<string>();
        RecentFolderList.Push(list, "  ");
        RecentFolderList.Push(list, null);
        Assert.Empty(list);

        Directory.CreateDirectory(_dir);
        Assert.Equal(new[] { _dir }, RecentFolderList.Existing(new[] { Path.Combine(_dir, "gone"), _dir }));
    }
}
