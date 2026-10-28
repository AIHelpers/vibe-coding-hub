using System.Text.Json;
using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Core.Tests.Flows;

/// <summary>E5-S1: flows load from the built-ins, ~/.aiagent/pipelines and &lt;repo&gt;/.aiagent/pipelines; the project wins.</summary>
public class SdlcPipelineLoaderScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pipeline-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _globalDir;
    private readonly string _repo;

    public SdlcPipelineLoaderScopeTests()
    {
        _globalDir = Path.Combine(_root, "home", ".aiagent", "pipelines");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static string ProjectPipelines(string repo) => Path.Combine(repo, ".aiagent", "pipelines");

    private SdlcPipelineLoader NewLoader(string? project) =>
        new(NullLogger<SdlcPipelineLoader>.Instance, _globalDir, project);

    private static SdlcPipelineDefinition Flow(string name, string description) => new()
    {
        Name = name,
        Description = description,
        Stages = new() { new() { Role = "implementer", Name = "Do", PromptTemplate = "{task}" } }
    };

    private static string WriteFlow(string dir, string name, string description, string? fileName = null)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, (fileName ?? name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(Flow(name, description), SdlcPipelineLoader.FileJsonOptions));
        return path;
    }

    [Fact]
    public void ProjectFlow_IsListedAsProject_WithItsFile()
    {
        var file = WriteFlow(ProjectPipelines(_repo), "feature-team", "from the repo");

        var loader = NewLoader(_repo);

        var entry = loader.GetEntry("feature-team")!;
        Assert.Equal(PipelineScope.Project, entry.Scope);
        Assert.Equal("project", entry.ScopeLabel);
        Assert.Equal(file, entry.FilePath);
        Assert.Null(entry.OverridesText);
        Assert.Equal("from the repo", loader.GetPipeline("feature-team")!.Description);
        Assert.Contains(loader.GetEntries(), e => e.Name == "feature-team" && e.Scope == PipelineScope.Project);
        Assert.Equal(file, loader.GetFilePath("feature-team"));
    }

    [Fact]
    public void BuiltIns_AreListedAsBuiltIn_WithoutAFile()
    {
        var loader = NewLoader(_repo);

        var entry = loader.GetEntry("full-sdlc")!;
        Assert.Equal(PipelineScope.BuiltIn, entry.Scope);
        Assert.Equal("builtin", entry.ScopeLabel);
        Assert.Null(entry.FilePath);
        Assert.True(loader.IsBuiltIn("full-sdlc"));
        Assert.False(loader.IsBuiltIn("nope"));
    }

    [Fact]
    public void SameNameInGlobalAndProject_ProjectWins_AndSaysItOverridesGlobal()
    {
        WriteFlow(_globalDir, "feature-team", "global version");
        WriteFlow(ProjectPipelines(_repo), "feature-team", "project version");

        var loader = NewLoader(_repo);

        Assert.Equal("project version", loader.GetPipeline("feature-team")!.Description);
        Assert.Equal("project version", loader.Pipelines["feature-team"].Description);
        var entry = loader.GetEntry("feature-team")!;
        Assert.Equal(PipelineScope.Project, entry.Scope);
        Assert.Equal(PipelineScope.Global, entry.Overrides);
        Assert.Equal("overrides global", entry.OverridesText);
        // The hidden global one is still there.
        Assert.Equal("global version", loader.GetEntry("feature-team", PipelineScope.Global)!.Pipeline.Description);
        Assert.Single(loader.GetEntries(), e => e.Name == "feature-team");
    }

    [Fact]
    public void Precedence_IsBuiltIn_ThenGlobal_ThenProject()
    {
        WriteFlow(_globalDir, "quick-fix", "my global quick-fix");
        var withoutProject = NewLoader(null);
        Assert.Equal("my global quick-fix", withoutProject.GetPipeline("quick-fix")!.Description);
        Assert.Equal("overrides built-in", withoutProject.GetEntry("quick-fix")!.OverridesText);
        Assert.False(withoutProject.IsBuiltIn("quick-fix"));

        WriteFlow(ProjectPipelines(_repo), "quick-fix", "repo quick-fix");
        WriteFlow(ProjectPipelines(_repo), "full-sdlc", "repo full-sdlc");
        var withProject = NewLoader(_repo);
        Assert.Equal("repo quick-fix", withProject.GetPipeline("quick-fix")!.Description);
        Assert.Equal("overrides global", withProject.GetEntry("quick-fix")!.OverridesText);
        Assert.Equal("repo full-sdlc", withProject.GetPipeline("full-sdlc")!.Description);
        Assert.Equal("overrides built-in", withProject.GetEntry("full-sdlc")!.OverridesText);
    }

    [Fact]
    public void ChangingTheProjectDirectory_ReloadsAndRaisesChanged()
    {
        var repoA = Path.Combine(_root, "a");
        var repoB = Path.Combine(_root, "b");
        WriteFlow(ProjectPipelines(repoA), "flow-a", "a");
        WriteFlow(ProjectPipelines(repoB), "flow-b", "b");
        var loader = NewLoader(repoA);
        var changed = 0;
        loader.Changed += (_, _) => changed++;
        Assert.NotNull(loader.GetPipeline("flow-a"));
        Assert.Null(loader.GetPipeline("flow-b"));

        loader.SetProjectDirectory(repoB);

        Assert.Equal(1, changed);
        Assert.Null(loader.GetPipeline("flow-a"));
        Assert.Equal(PipelineScope.Project, loader.GetEntry("flow-b")!.Scope);
        Assert.Equal(Path.GetFullPath(repoB), loader.ProjectDirectory);

        loader.SetProjectDirectory(repoB); // same folder: nothing to reload
        Assert.Equal(1, changed);

        loader.SetProjectDirectory(null);
        Assert.Equal(2, changed);
        Assert.Null(loader.GetPipeline("flow-b"));
        Assert.Null(loader.ProjectDirectory);
        Assert.NotNull(loader.GetPipeline("full-sdlc"));
    }

    [Fact]
    public void Reload_PicksUpAFileAddedToTheProjectFolder()
    {
        var loader = NewLoader(_repo);
        Assert.Null(loader.GetPipeline("late"));

        WriteFlow(ProjectPipelines(_repo), "late", "added by git pull");
        loader.Reload();

        Assert.Equal(PipelineScope.Project, loader.GetEntry("late")!.Scope);
    }

    [Fact]
    public void AFolderWhosePipelinesAreTheGlobalOnes_IsNoProject()
    {
        var home = Path.Combine(_root, "home"); // <home>/.aiagent/pipelines is the global folder
        WriteFlow(_globalDir, "mine", "global");

        var loader = NewLoader(home);

        Assert.Null(loader.ProjectDirectory);
        Assert.Equal(PipelineScope.Global, loader.GetEntry("mine")!.Scope);
        Assert.Single(loader.GetEntries(), e => e.Name == "mine");
    }

    [Fact]
    public void InvalidProjectFile_IsSkipped_AndNamesComeFromTheJson()
    {
        var dir = ProjectPipelines(_repo);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir, "empty.json"), "{\"name\":\"empty\",\"stages\":[]}");
        WriteFlow(dir, "renamed-inside", "x", fileName: "other-file-name");

        var loader = NewLoader(_repo);

        Assert.Null(loader.GetPipeline("broken"));
        Assert.Null(loader.GetPipeline("empty"));
        Assert.Equal(Path.Combine(dir, "other-file-name.json"), loader.GetEntry("renamed-inside")!.FilePath);
    }

    [Fact]
    public async Task Save_WritesToTheChosenScope()
    {
        var loader = NewLoader(_repo);
        var changed = 0;
        loader.Changed += (_, _) => changed++;

        var projectFile = await loader.SavePipelineAsync(Flow("team-flow", "shared"), PipelineScope.Project);
        var globalFile = await loader.SavePipelineAsync(Flow("my-flow", "mine"), PipelineScope.Global);

        Assert.Equal(Path.Combine(ProjectPipelines(_repo), "team-flow.json"), projectFile);
        Assert.True(File.Exists(projectFile));
        Assert.Equal(Path.Combine(_globalDir, "my-flow.json"), globalFile);
        Assert.True(File.Exists(globalFile));
        Assert.False(File.Exists(Path.Combine(_globalDir, "team-flow.json")));
        Assert.Equal(PipelineScope.Project, loader.GetEntry("team-flow")!.Scope);
        Assert.Equal(PipelineScope.Global, loader.GetEntry("my-flow")!.Scope);
        Assert.Equal(2, changed);

        // A fresh loader sees the same files.
        var fresh = NewLoader(_repo);
        Assert.Equal(PipelineScope.Project, fresh.GetEntry("team-flow")!.Scope);
    }

    [Fact]
    public async Task Save_DefaultsToGlobal_AndKeepsEditingTheSameFile()
    {
        var dir = ProjectPipelines(_repo);
        var existing = WriteFlow(dir, "legacy", "v1", fileName: "differently-named");
        var loader = NewLoader(_repo);

        await loader.SavePipelineAsync(Flow("global-flow", "global by default"));
        var path = await loader.SavePipelineAsync(Flow("legacy", "v2"), PipelineScope.Project);

        Assert.True(File.Exists(Path.Combine(_globalDir, "global-flow.json")));
        Assert.Equal(existing, path);
        Assert.Equal("v2", loader.GetPipeline("legacy")!.Description);
        Assert.False(File.Exists(Path.Combine(dir, "legacy.json")));
    }

    [Fact]
    public async Task Save_ToProjectWithoutAProjectFolder_OrToBuiltIn_Throws()
    {
        var loader = NewLoader(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.SavePipelineAsync(Flow("test-flow", "x"), PipelineScope.Project));
        await Assert.ThrowsAsync<ArgumentException>(() => loader.SavePipelineAsync(Flow("test-flow", "x"), PipelineScope.BuiltIn));
        await Assert.ThrowsAsync<ArgumentException>(() => loader.SavePipelineAsync(Flow("Bad Name", "x"), PipelineScope.Global));
    }

    [Fact]
    public async Task SavingGlobally_DoesNotHideTheProjectFlowOfTheSameName()
    {
        var loader = NewLoader(_repo);
        await loader.SavePipelineAsync(Flow("shared", "project one"), PipelineScope.Project);

        await loader.SavePipelineAsync(Flow("shared", "global one"), PipelineScope.Global);

        Assert.Equal("project one", loader.GetPipeline("shared")!.Description);
        Assert.Equal("overrides global", loader.GetEntry("shared")!.OverridesText);
    }

    [Fact]
    public async Task DeletingTheProjectFlow_RevealsTheGlobalOne()
    {
        WriteFlow(_globalDir, "shared", "global one");
        var loader = NewLoader(_repo);
        await loader.SavePipelineAsync(Flow("shared", "project one"), PipelineScope.Project);

        Assert.True(loader.DeletePipeline("shared"));

        Assert.Equal(PipelineScope.Global, loader.GetEntry("shared")!.Scope);
        Assert.False(File.Exists(Path.Combine(ProjectPipelines(_repo), "shared.json")));
        Assert.True(File.Exists(Path.Combine(_globalDir, "shared.json")));
        Assert.True(loader.DeletePipeline("shared", PipelineScope.Global));
        Assert.Null(loader.GetPipeline("shared"));
        Assert.False(loader.DeletePipeline("shared"));
    }

    [Fact]
    public async Task Copy_FromBuiltIn_ToProject_WritesAFileThatLoadsAsProject()
    {
        var loader = NewLoader(_repo);

        var copied = await loader.CopyPipelineAsync("full-sdlc", PipelineScope.Project);

        Assert.Equal(PipelineScope.Project, copied.Scope);
        Assert.Equal(Path.Combine(ProjectPipelines(_repo), "full-sdlc.json"), copied.FilePath);
        Assert.True(File.Exists(copied.FilePath));
        Assert.Equal("overrides built-in", copied.OverridesText);
        Assert.Equal(5, copied.Pipeline.Stages.Count);
    }

    [Fact]
    public async Task Copy_RefusesToOverwrite_UnlessForced_AndPicksTheNearestSource()
    {
        WriteFlow(_globalDir, "feature-team", "global version");
        var loader = NewLoader(_repo);

        var copied = await loader.CopyPipelineAsync("feature-team", PipelineScope.Project);
        Assert.Equal("global version", copied.Pipeline.Description);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.CopyPipelineAsync("feature-team", PipelineScope.Project));
        Assert.Contains("--force", ex.Message);

        await loader.SavePipelineAsync(Flow("feature-team", "project edit"), PipelineScope.Project);
        // Project -> global: the project version is the source.
        var back = await loader.CopyPipelineAsync("feature-team", PipelineScope.Global, overwrite: true);
        Assert.Equal("project edit", back.Pipeline.Description);
        Assert.Equal("project edit", loader.GetEntry("feature-team", PipelineScope.Global)!.Pipeline.Description);
    }

    [Fact]
    public async Task Copy_RejectsUnknownNames_BuiltInTargets_AndMissingSources()
    {
        var loader = NewLoader(_repo);
        WriteFlow(_globalDir, "only-global", "g");
        loader.Reload();

        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.CopyPipelineAsync("does-not-exist", PipelineScope.Project));
        await Assert.ThrowsAsync<ArgumentException>(() => loader.CopyPipelineAsync("full-sdlc", PipelineScope.BuiltIn));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.CopyPipelineAsync("only-global", PipelineScope.Global));
        Assert.Contains("nothing to copy", ex.Message);
    }

    [Fact]
    public void DefaultSaveScope_IsProject_OnlyInsideAGitRepository()
    {
        // Skip the negative case when the temp folder itself happens to sit inside a git repository.
        var tempInsideRepo = SdlcPipelineLoader.IsGitRepository(_repo);
        Assert.Equal(PipelineScope.Global, NewLoader(null).DefaultSaveScope);
        if (!tempInsideRepo) Assert.Equal(PipelineScope.Global, NewLoader(_repo).DefaultSaveScope);

        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Assert.Equal(PipelineScope.Project, NewLoader(_repo).DefaultSaveScope);

        // A sub-folder of the repository counts too, and a .git file (worktrees, submodules) does.
        var sub = Path.Combine(_repo, "src", "app");
        Directory.CreateDirectory(sub);
        Assert.True(SdlcPipelineLoader.IsGitRepository(sub));
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../repo/.git/worktrees/w");
        Assert.True(SdlcPipelineLoader.IsGitRepository(worktree));
        Assert.False(SdlcPipelineLoader.IsGitRepository(null));
        Assert.False(SdlcPipelineLoader.IsGitRepository(""));
    }

    [Theory]
    [InlineData("project", true, PipelineScope.Project)]
    [InlineData("GLOBAL", true, PipelineScope.Global)]
    [InlineData(" builtin ", true, PipelineScope.BuiltIn)]
    [InlineData("built-in", true, PipelineScope.BuiltIn)]
    [InlineData("team", false, PipelineScope.Global)]
    [InlineData(null, false, PipelineScope.Global)]
    public void ScopeNames_ParseAndPrintRoundTrip(string? text, bool ok, PipelineScope expected)
    {
        Assert.Equal(ok, PipelineScopeExtensions.TryParse(text, out var scope));
        Assert.Equal(expected, scope);
        if (ok) Assert.True(PipelineScopeExtensions.TryParse(scope.Label(), out var again) && again == scope);
    }
}
