using AiCodeAgent.Core.Packs;
using AiCodeAgent.Core.Updates;
using Xunit;

namespace AiCodeAgent.Core.Tests.Packs;

public class PackInstallerTests : IDisposable
{
    private readonly PackTestRepo _repo = new();
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pack-home-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "pack-proj-" + Guid.NewGuid().ToString("N"));

    public PackInstallerTests()
    {
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        _repo.Dispose();
        PackStore.DeleteDirectory(_home);
        PackStore.DeleteDirectory(_project);
    }

    private PackStore NewStore(string? name = null) => new(Path.Combine(_home, name ?? "packs"));

    private PackInstaller Installer(PackStore store, string? appVersion = "1.3.0") =>
        new(store, appVersion: appVersion == null ? null : SemanticVersion.Parse(appVersion));

    private string Url => _repo.Bare;

    private void Declare(string name, string gitRef) =>
        PackStore.SaveToProjectFile(_project, new PackReference { Name = name, Url = Url, Ref = gitRef });

    private string StandardPack(string name = "dotnet-team-pack", string? tag = "v1.0.0")
    {
        _repo.WriteManifest(name);
        _repo.WriteSkill("api-design");
        _repo.WriteCharacter("pack-architect");
        _repo.WriteFlow("pack-review");
        return _repo.Commit("initial", tag);
    }

    [Fact]
    public async Task Install_CopiesThePackAndRegistersIt()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        var store = NewStore();
        var installed = await Installer(store).InstallAsync(Url);

        Assert.Equal("dotnet-team-pack", installed.Name);
        Assert.Equal("1.0.0", installed.Version);
        Assert.True(File.Exists(Path.Combine(store.DirectoryOf(installed), "skills", "api-design", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(store.DirectoryOf(installed), "characters", "pack-architect.md")));
        Assert.True(File.Exists(Path.Combine(store.DirectoryOf(installed), "pipelines", "pack-review.json")));
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryOf(installed), ".git")));
        Assert.Single(store.List());
        var source = Assert.Single(store.Sources());
        Assert.Equal("pack:dotnet-team-pack", source.ScopeLabel);
    }

    [Fact]
    public async Task Prepare_ListsFilesAndWhatItAdds_WithoutInstalling()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        var store = NewStore();
        var installer = Installer(store);

        var preview = await installer.PrepareAsync(Url);

        Assert.True(preview.CanInstall);
        Assert.Contains("api-design", preview.Skills);
        Assert.Contains("pack-architect", preview.Characters);
        Assert.Contains("pack-review", preview.Flows);
        Assert.Contains(preview.Files, f => f.Path == "pack.json");
        Assert.Contains("skills/api-design/SKILL.md", preview.Render());
        Assert.Empty(store.List());
        installer.Discard(preview);
        Assert.False(Directory.Exists(preview.StagingDirectory));
    }

    [Fact]
    public async Task Prepare_FlagsHooksMcpAndScripts_ButNeverEnablesThem()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        _repo.Write("hooks.json", "{ \"hooks\": [] }");
        _repo.Write(".mcp.json", "{ \"mcpServers\": { \"filesystem\": { \"command\": \"x\" } } }");
        _repo.Write("tools/setup.sh", "echo hi");
        _repo.Commit("risky");

        var preview = await Installer(NewStore()).PrepareAsync(Url);

        Assert.True(preview.CanInstall);
        Assert.Contains(preview.Attention, a => a.Contains("hooks", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Attention, a => a.Contains("filesystem"));
        Assert.Contains(preview.Attention, a => a.Contains("setup.sh"));
        Assert.Contains("never enable hooks", preview.Render());
    }

    [Fact]
    public async Task Prepare_RejectsRepositoriesWithoutAManifestOrContent()
    {
        if (!PackTestRepo.GitAvailable()) return;
        _repo.Write("README.md", "not a pack");
        _repo.Commit();
        var store = NewStore();

        var preview = await Installer(store).PrepareAsync(Url);
        Assert.False(preview.CanInstall);
        Assert.NotEmpty(preview.Problems);
        await Assert.ThrowsAsync<PackException>(async () => await Installer(store).InstallAsync(Url));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Prepare_RejectsInvalidPackNames()
    {
        if (!PackTestRepo.GitAvailable()) return;
        _repo.WriteManifest("Not A Valid Name");
        _repo.WriteSkill("x");
        _repo.Commit();

        var preview = await Installer(NewStore()).PrepareAsync(Url);

        Assert.False(preview.CanInstall);
    }

    [Fact]
    public async Task Ref_PinsATagAndInstallsThatVersion()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        _repo.WriteSkill("later-skill");
        _repo.Commit("second", "v2.0.0");
        var store = NewStore();

        var installed = await Installer(store).InstallAsync(Url, "v1.0.0");

        Assert.True(installed.Pinned);
        Assert.Equal("v1.0.0", installed.RefLabel);
        Assert.EndsWith("@v1.0.0", installed.Directory);
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryOf(installed), "skills", "later-skill")));
    }

    [Fact]
    public async Task Ref_CanBeACommit()
    {
        if (!PackTestRepo.GitAvailable()) return;
        var first = StandardPack(tag: null);
        _repo.WriteSkill("later-skill");
        _repo.Commit("second");

        var installed = await Installer(NewStore()).InstallAsync(Url, first[..10]);

        Assert.True(installed.Pinned);
        Assert.Equal(first, installed.Commit);
    }

    [Fact]
    public async Task UnknownRef_Fails_AndLeavesNothingBehind()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        var store = NewStore();

        await Assert.ThrowsAsync<PackException>(async () => await Installer(store).PrepareAsync(Url, "no-such-ref"));

        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.StagingRoot) && Directory.EnumerateFileSystemEntries(store.StagingRoot).Any());
    }

    [Fact]
    public async Task Update_MovesABranchFollowingPackAndLeavesPinnedOnesAlone()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack("followed-pack", tag: "v1.0.0");
        var store = NewStore();
        var installer = Installer(store);
        var followed = await installer.InstallAsync(Url);               // follows the default branch
        var pinned = await installer.InstallAsync(Url, "v1.0.0", makeDefault: false);

        _repo.WriteSkill("new-skill");
        var newest = _repo.Commit("update");

        var results = await installer.UpdateAsync();

        Assert.Contains(results, r => r.State == PackUpdateState.Updated);
        Assert.Contains(results, r => r.State == PackUpdateState.Pinned);
        var current = store.Find("followed-pack");
        Assert.Equal(newest, current!.Commit);
        Assert.True(File.Exists(Path.Combine(store.DirectoryOf(current), "skills", "new-skill", "SKILL.md")));
        Assert.Contains(store.List(), p => p.RefLabel == "v1.0.0");
        Assert.False(Directory.Exists(store.DirectoryOf(followed)));
    }

    [Fact]
    public async Task Update_WhenNothingChanged_ReportsUpToDate()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        var installer = Installer(NewStore());
        await installer.InstallAsync(Url);

        var results = await installer.UpdateAsync("dotnet-team-pack");

        Assert.Equal(PackUpdateState.UpToDate, Assert.Single(results).State);
    }

    [Fact]
    public async Task Remove_DeletesFoldersAndRecords()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        var store = NewStore();
        var installer = Installer(store);
        var installed = await installer.InstallAsync(Url);

        var removed = installer.Remove("dotnet-team-pack");

        Assert.Single(removed);
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.DirectoryOf(installed)));
        Assert.Empty(store.Sources());
    }

    [Fact]
    public async Task Incompatible_WhenTheAppIsTooOld()
    {
        if (!PackTestRepo.GitAvailable()) return;
        _repo.WriteManifest("needs-new-app", requiresApp: ">=9.0");
        _repo.WriteSkill("x");
        _repo.Commit();
        var store = NewStore();
        var installer = Installer(store, appVersion: "1.3.0");

        var preview = await installer.PrepareAsync(Url);
        Assert.NotNull(preview.Incompatible);
        Assert.Throws<PackException>(() => installer.Commit(preview));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Compatible_WhenTheAppIsNewEnough()
    {
        if (!PackTestRepo.GitAvailable()) return;
        _repo.WriteManifest("needs-1-3", requiresApp: ">=1.3");
        _repo.WriteSkill("x");
        _repo.Commit();

        var installed = await Installer(NewStore(), appVersion: "1.3.0").InstallAsync(Url);

        Assert.Equal("needs-1-3", installed.Name);
    }

    [Fact]
    public async Task Sync_InstallsExactlyTheRefsInPacksJson_InAFreshStore()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        _repo.WriteSkill("v2-only");
        _repo.Commit("v2", "v2.0.0");

        Declare("dotnet-team-pack", "v1.0.0");

        // a teammate's machine: nothing installed yet
        var fresh = NewStore("fresh-packs");
        var results = await Installer(fresh).SyncAsync(_project);

        var result = Assert.Single(results);
        Assert.True(result.Installed, result.Message);
        var pack = Assert.Single(fresh.List());
        Assert.Equal("v1.0.0", pack.RefLabel);
        var source = Assert.Single(fresh.Sources(_project));
        Assert.True(File.Exists(Path.Combine(source.Root, "skills", "api-design", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(source.Root, "skills", "v2-only")));
    }

    [Fact]
    public async Task Sync_SkipsWhatIsAlreadyInstalled()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        Declare("dotnet-team-pack", "v1.0.0");
        var store = NewStore();
        var installer = Installer(store);

        await installer.SyncAsync(_project);
        var second = await installer.SyncAsync(_project);

        Assert.False(Assert.Single(second).Installed);
        Assert.Single(store.List());
    }

    [Fact]
    public async Task Sync_RefusesAPackWhoseNameDiffersFromThePacksJsonEntry()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        Declare("other-name", "v1.0.0");
        var store = NewStore();

        var results = await Installer(store).SyncAsync(_project);

        Assert.False(Assert.Single(results).Installed);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Sync_CanBeDeclinedByTheApproveCallback()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        Declare("dotnet-team-pack", "v1.0.0");
        var store = NewStore();

        var results = await Installer(store).SyncAsync(_project, approve: _ => false);

        Assert.False(Assert.Single(results).Installed);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task ProjectFileRoundTrips_AndPicksTheDeclaredRefOverTheDefault()
    {
        if (!PackTestRepo.GitAvailable()) return;
        StandardPack();
        _repo.WriteSkill("v2-only");
        _repo.Commit("v2", "v2.0.0");
        var store = NewStore();
        var installer = Installer(store);
        await installer.InstallAsync(Url, "v1.0.0", makeDefault: false);
        await installer.InstallAsync(Url, "v2.0.0", makeDefault: true);

        Declare("dotnet-team-pack", "v1.0.0");

        var declared = Assert.Single(PackStore.ReadProjectFile(_project));
        Assert.Equal("v1.0.0", declared.Ref);
        Assert.EndsWith("@v1.0.0", Assert.Single(store.Sources(_project)).Root);
        Assert.EndsWith("@v2.0.0", Assert.Single(store.Sources()).Root);
    }

    [Theory]
    [InlineData(">=1.3", "1.3.0", true)]
    [InlineData(">=1.3", "1.2.9", false)]
    [InlineData(">=1.3 <2.0", "1.9.0", true)]
    [InlineData(">=1.3 <2.0", "2.0.0", false)]
    [InlineData("=1.4.0", "1.4.0", true)]
    [InlineData(">1.0", "1.0.0", false)]
    public void VersionRange_Satisfies(string range, string version, bool expected)
    {
        Assert.Equal(expected, PackVersionRange.Satisfies(range, SemanticVersion.Parse(version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    [InlineData(">=")]
    public void VersionRange_RejectsGarbage(string range) => Assert.False(PackVersionRange.IsValid(range));
}
