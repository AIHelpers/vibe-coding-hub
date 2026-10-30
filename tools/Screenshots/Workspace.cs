using System.Globalization;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Screenshots;

/// <summary>
/// The scratch world the screenshots are taken in: a private <c>AIAGENT_HOME</c>, a copy of the TodoApp sample as a
/// git repository with the example team (characters, skills, flows) committed, and a seeded <c>config.json</c> that
/// selects the scripted provider. Paths are fixed per theme so two runs show the same text.
/// </summary>
internal sealed class Workspace
{
    public const string ProjectName = "TodoApp";
    public const string FixedGitDate = "2026-10-01T09:00:00+00:00";

    public string Root { get; }
    public string Home { get; }
    public string Project { get; }
    public string RepoRoot { get; }

    private Workspace(string root, string repoRoot)
    {
        Root = root;
        Home = Path.Combine(root, "home");
        Project = Path.Combine(root, "projects", ProjectName);
        RepoRoot = repoRoot;
    }

    /// <summary>Walks up from <paramref name="start"/> to the folder that holds <c>AiCodeAgent.slnx</c>.</summary>
    public static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var full = Path.GetFullPath(explicitRoot);
            if (!File.Exists(Path.Combine(full, "AiCodeAgent.slnx")))
                throw new InvalidOperationException($"--repo '{full}' does not contain AiCodeAgent.slnx.");
            return full;
        }

        return FindRepoRoot(Directory.GetCurrentDirectory())
               ?? FindRepoRoot(AppContext.BaseDirectory)
               ?? throw new InvalidOperationException(
                   "Cannot find the repository (AiCodeAgent.slnx). Run the tool from inside the repository or pass --repo <dir>.");
    }

    /// <summary>The scratch folder for one theme pass, e.g. <c>%TEMP%/vch-screenshots/dark</c>.</summary>
    public static string ScratchPath(string theme) => Path.Combine(Path.GetTempPath(), "vch-screenshots", theme);

    /// <summary>Builds the workspace from nothing (an old one is deleted first).</summary>
    public static async Task<Workspace> CreateAsync(Options options, string theme)
    {
        var repo = ResolveRepoRoot(options.RepoRoot);
        var workspace = new Workspace(ScratchPath(theme), repo);

        DeleteDirectory(workspace.Root);
        Directory.CreateDirectory(workspace.Home);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.Project)!);

        workspace.ApplyEnvironment(options);
        await workspace.InstallProjectAsync(options).ConfigureAwait(false);
        await workspace.SeedConfigurationAsync(theme).ConfigureAwait(false);
        return workspace;
    }

    /// <summary>
    /// Everything that makes a run repeatable lives in the process environment: the private home, the scripted provider,
    /// git without the user's own configuration and with fixed dates, an English UI.
    /// </summary>
    private void ApplyEnvironment(Options options)
    {
        var gitConfig = Path.Combine(Root, "gitconfig");
        File.WriteAllText(gitConfig, "[init]\n\tdefaultBranch = main\n");

        var variables = new Dictionary<string, string>
        {
            [AppPaths.HomeEnvironmentVariable] = Home,
            [ScriptedProviderSupport.EnableVariable] = "1",
            ["AIAGENT_SCRIPTED_DELAY_SCALE"] = options.DelayScale.ToString("0.###", CultureInfo.InvariantCulture),
            ["GIT_CONFIG_GLOBAL"] = gitConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Vibe Coding Hub",
            ["GIT_AUTHOR_EMAIL"] = "demo@vibecodinghub.local",
            ["GIT_COMMITTER_NAME"] = "Vibe Coding Hub",
            ["GIT_COMMITTER_EMAIL"] = "demo@vibecodinghub.local",
            ["GIT_AUTHOR_DATE"] = FixedGitDate,
            ["GIT_COMMITTER_DATE"] = FixedGitDate,
            ["AVALONIA_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);

        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
    }

    /// <summary>The sample as "Try the sample project" makes it, plus the example team and a .gitignore, all committed.</summary>
    private async Task InstallProjectAsync(Options options)
    {
        var installer = new SampleProjectInstaller(new EmbeddedSampleFileSource());
        var result = await installer.InstallAsync(Path.GetDirectoryName(Project)).ConfigureAwait(false);
        if (!Path.GetFullPath(result.Path).Equals(Path.GetFullPath(Project), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The sample was installed to '{result.Path}', expected '{Project}'.");
        if (!result.GitReady)
            throw new InvalidOperationException("Git is required for the screenshots (diff and commit scenes): " + result.Warning);

        // build output must not show up as changes in the source control scenes
        File.WriteAllText(Path.Combine(Project, ".gitignore"), "bin/\nobj/\nlogs/\n");

        var team = Path.Combine(Project, ".aiagent");
        var examples = Path.Combine(RepoRoot, "examples");
        CopyDirectory(Path.Combine(examples, "skills"), Path.Combine(team, "skills"));
        CopyFiles(Path.Combine(examples, "characters"), "*.md", Path.Combine(team, "characters"), exclude: "README.md");
        CopyFiles(Path.Combine(examples, "pipelines"), "*.json", Path.Combine(team, "pipelines"), exclude: "templates.json");

        if (options.FastChecks)
        {
            // verify_changes reads this file first; the commands finish instantly and need neither the SDK nor NuGet.
            Directory.CreateDirectory(team);
            File.WriteAllText(Path.Combine(team, "verify.json"),
                "{\n  \"commands\": [\n" +
                "    { \"name\": \"build\", \"command\": \"echo Build succeeded.\" },\n" +
                "    { \"name\": \"test\", \"command\": \"echo Passed: 6, Failed: 0\" }\n" +
                "  ]\n}\n");
        }

        var git = new ProcessGitRunner();
        foreach (var arguments in new[]
                 {
                     new[] { "add", "-A" },
                     new[] { "commit", "--no-gpg-sign", "-m", "Add the example team and a .gitignore" }
                 })
        {
            var (code, output) = await git.RunAsync(Project, arguments, CancellationToken.None).ConfigureAwait(false);
            if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
        }
    }

    /// <summary>A config that skips the wizard, never calls the network and talks to the scripted provider.</summary>
    private async Task SeedConfigurationAsync(string theme)
    {
        var service = new ConfigurationService(Home);
        await service.LoadAsync().ConfigureAwait(false);
        var config = service.Config;

        config.DefaultProvider = ScriptedProviderSupport.ProviderName;
        config.Providers[ScriptedProviderSupport.ProviderName] = ScriptedProviderSupport.DefaultConfiguration;
        config.Agent.WorkingDirectory = Project;
        config.Agent.DefaultPermissionMode = "Ask";
        config.Agent.RequirementsClarifier = RequirementsClarifierMode.Off;

        config.Ui.Theme = theme;
        config.Ui.SetupWizardDone = true;
        config.Ui.CheckForUpdates = false;
        config.Ui.ResumeLastSession = ResumeLastSessionMode.Never;
        config.Ui.ActivityBarExpanded = true;
        config.Ui.RecentFolders.Clear();
        config.Ui.SampleGuide = new SampleGuideState { ProjectPath = Project, Completed = 0, Finished = false };

        await service.SaveAsync().ConfigureAwait(false);
    }

    public void Delete() => DeleteDirectory(Root);

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Example folder not found: {source}");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void CopyFiles(string source, string pattern, string target, string exclude)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Example folder not found: {source}");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, pattern, SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file).Equals(exclude, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // git marks object files read-only on Windows
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
