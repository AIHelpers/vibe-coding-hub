using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Packs;
using System.Text.Json;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// A single stage in an SDLC pipeline. Maps to an <see cref="AgentRolePreset"/> by role name;
/// the preset supplies the system prompt, allowed tools, and default permission mode for the stage.
/// </summary>
public record SdlcStageDefinition
{
    /// <summary>
    /// Node id in a flow (kebab-case, unique within the pipeline). Edges refer to it. Optional for
    /// linear pipelines; a flow derives one from <see cref="Name"/> when it is missing.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// Role name, resolved against <see cref="RolePresetLoader"/> (e.g. "planner", "implementer").
    /// May be empty when <see cref="Character"/> is set: the character's base role is used.
    /// </summary>
    public string Role { get; init; } = string.Empty;
    /// <summary>
    /// Optional character that runs this stage (e.g. "alex-architect"). Its base role, persona,
    /// tools and skills replace the plain <see cref="Role"/> preset. A run-time cast
    /// (<c>--cast planner=alex-architect</c>) overrides this.
    /// </summary>
    public string? Character { get; init; }
    /// <summary>Human-friendly stage name for display (e.g. "Analyze", "Implement").</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Prompt sent to the agent for this stage. "{task}" is replaced with the user's task text.</summary>
    public string PromptTemplate { get; init; } = string.Empty;
    /// <summary>Whether this stage runs. Set false to skip a stage without deleting it from the pipeline.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Ask the user to confirm before this stage starts. Built-in "Deploy" stages set this.</summary>
    public bool RequireConfirmation { get; init; }
    /// <summary>
    /// <c>"agent"</c> (default) or <c>"human"</c>. A human step runs no agent: it shows its prompt and asks the user to
    /// choose one of its <see cref="Outcomes"/> (with an optional note), and the edges route on the choice exactly as
    /// they do for an agent step.
    /// </summary>
    public string? Kind { get; init; }
    /// <summary>
    /// For <c>kind: "acp"</c> (prototype, E6-S5): the command that starts an external Agent Client Protocol agent,
    /// for example <c>gemini</c> with <see cref="AcpArgs"/> <c>["--acp"]</c>.
    /// </summary>
    public string? AcpCommand { get; init; }
    /// <summary>Arguments for <see cref="AcpCommand"/>.</summary>
    public List<string>? AcpArgs { get; init; }
    /// <summary>True for a step run by an external ACP agent.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAcp => string.Equals(Kind, "acp", StringComparison.OrdinalIgnoreCase);
    /// <summary>True for a step that asks the user instead of running an agent.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHuman => string.Equals(Kind, "human", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Verdicts this stage must choose from in a flow (e.g. ["approved", "rejected"]). The agent
    /// ends its answer with <c>OUTCOME: &lt;one of them&gt;</c> and edges route on it.
    /// </summary>
    public List<string>? Outcomes { get; init; }
    /// <summary>
    /// Model for this step: a concrete model id or a tier alias (<c>fast</c> | <c>balanced</c> | <c>frontier</c>)
    /// resolved through the model catalog. Precedence: stage &gt; character &gt; session default. Null = inherit.
    /// </summary>
    public string? Model { get; init; }
    /// <summary>
    /// Provider for this step (a key of the configured providers, e.g. "anthropic"). Null = the session's provider.
    /// Parallel steps may use different providers.
    /// </summary>
    public string? Provider { get; init; }
    /// <summary>Canvas position (left) in the Flows panel.</summary>
    public double? X { get; init; }
    /// <summary>Canvas position (top) in the Flows panel.</summary>
    public double? Y { get; init; }
}

/// <summary>An arrow in a flow: <see cref="To"/> runs after <see cref="From"/>.</summary>
public record FlowEdge
{
    /// <summary>Id of the stage that runs first.</summary>
    public string From { get; init; } = string.Empty;
    /// <summary>Id of the stage that runs after it.</summary>
    public string To { get; init; } = string.Empty;
    /// <summary>
    /// Outcome of <see cref="From"/> that makes this edge taken (one of its <c>outcomes</c>, or
    /// <c>loop-exhausted</c>). Null = always taken once <see cref="From"/> is done.
    /// </summary>
    public string? When { get; init; }
    /// <summary>
    /// How many times this edge may send work back to an earlier stage. Required on an edge that
    /// closes a cycle (e.g. reviewer → developer on "rejected").
    /// </summary>
    public int? MaxLoops { get; init; }
}

/// <summary>
/// A named, ordered sequence of SDLC stages — fully data-driven and configurable via JSON,
/// mirroring how <see cref="RolePresetLoader"/> makes agent roles configurable.
/// </summary>
public record SdlcPipelineDefinition
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<SdlcStageDefinition> Stages { get; init; } = new();
    /// <summary>
    /// Arrows between stages. When present the pipeline is a <b>flow</b>: stages run as soon as the
    /// stages before them finish (independent ones in parallel), and each gets the outputs of the
    /// stages connected into it. When null/empty the pipeline is linear (one shared history).
    /// </summary>
    public List<FlowEdge>? Edges { get; init; }
    /// <summary>Most stages a flow runs at the same time (default 3).</summary>
    public int? MaxParallel { get; init; }
    /// <summary>
    /// Spend cap in USD for one run of the flow. When the steps that have finished cost this much, running steps
    /// finish their current call, no new step starts and the flow ends as <c>budget_exceeded</c>. Null = no cap.
    /// </summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>True when this pipeline is a flow (has edges).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFlow => Edges is { Count: > 0 };
}

/// <summary>Where a pipeline comes from. Precedence on a name clash: project over global over pack over built-in.</summary>
public enum PipelineScope
{
    BuiltIn = 0,
    /// <summary>A file in an installed pack's <c>pipelines</c> folder: read-only, copy it to change it.</summary>
    Pack = 1,
    /// <summary>A file in <c>~/.aiagent/pipelines</c>: only this user.</summary>
    Global = 2,
    /// <summary>A file in <c>&lt;repo&gt;/.aiagent/pipelines</c>: shared with the team through git.</summary>
    Project = 3
}

public static class PipelineScopeExtensions
{
    /// <summary>The name used in the CLI and in listings: <c>builtin</c>, <c>global</c> or <c>project</c>.</summary>
    public static string Label(this PipelineScope scope) => scope switch
    {
        PipelineScope.BuiltIn => "builtin",
        PipelineScope.Pack => "pack",
        PipelineScope.Global => "global",
        _ => "project"
    };

    /// <summary>Reads <c>project</c>, <c>global</c> or <c>builtin</c> (also <c>built-in</c>), case-insensitively.</summary>
    public static bool TryParse(string? text, out PipelineScope scope)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "project": scope = PipelineScope.Project; return true;
            case "global": scope = PipelineScope.Global; return true;
            case "builtin" or "built-in": scope = PipelineScope.BuiltIn; return true;
            default: scope = PipelineScope.Global; return false;
        }
    }
}

/// <summary>A pipeline together with where it was loaded from.</summary>
/// <param name="Pipeline">The definition.</param>
/// <param name="Scope">Built-in, global or project.</param>
/// <param name="FilePath">The JSON file; null for a built-in pipeline.</param>
/// <param name="Overrides">The scope of the definition with the same name that this one hides, if any.</param>
public sealed record PipelineEntry(SdlcPipelineDefinition Pipeline, PipelineScope Scope, string? FilePath, PipelineScope? Overrides)
{
    public string Name => Pipeline.Name;

    /// <summary>The pack a <see cref="PipelineScope.Pack"/> flow comes from.</summary>
    public string? PackName { get; init; }

    /// <summary><c>builtin</c>, <c>global</c>, <c>project</c> or <c>pack:&lt;name&gt;</c>.</summary>
    public string ScopeLabel => Scope == PipelineScope.Pack && PackName != null ? "pack:" + PackName : Scope.Label();

    /// <summary>"overrides global" or "overrides built-in" when this definition hides another one; otherwise null.</summary>
    public string? OverridesText => Overrides is { } hidden
        ? "overrides " + (hidden == PipelineScope.BuiltIn ? "built-in" : hidden.Label())
        : null;
}

/// <summary>
/// Loads SDLC pipeline definitions, the same pattern as <see cref="RolePresetLoader"/>. Three layers, later ones win on a
/// name clash: the built-in "full-sdlc" and "quick-fix" pipelines, then <c>~/.aiagent/pipelines</c> (global), then
/// <c>&lt;project&gt;/.aiagent/pipelines</c> (project, committed next to the code it works on). Skills, characters and
/// <c>mcp.json</c> follow the same rule, so the whole lifecycle is configurable without recompiling the app.
/// </summary>
public class SdlcPipelineLoader
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ILogger<SdlcPipelineLoader> _logger;
    private readonly string _pipelinesDirectory;
    private readonly Dictionary<string, SdlcPipelineDefinition> _builtIns;
    private string? _projectRoot;
    private Dictionary<string, PipelineEntry> _global = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PipelineEntry> _project = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PipelineEntry> _pack = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<PackSource>>? _packSources;
    private Dictionary<string, SdlcPipelineDefinition> _pipelines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pipelines in force, by name: project over global over built-in.</summary>
    public IReadOnlyDictionary<string, SdlcPipelineDefinition> Pipelines => _pipelines;

    /// <summary>JSON used for pipeline files: camelCase, indented, nulls left out (so linear pipelines stay short).</summary>
    public static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="pipelinesDirectory">Folder with the user's global pipelines; defaults to <c>~/.aiagent/pipelines</c>.</param>
    /// <param name="projectDirectory">The project folder (the working directory); its <c>.aiagent/pipelines</c> is loaded too. Null = no project.</param>
    /// <param name="packSources">Installed packs whose <c>pipelines</c> folders are read (read-only); null = none.</param>
    public SdlcPipelineLoader(ILogger<SdlcPipelineLoader> logger, string? pipelinesDirectory = null, string? projectDirectory = null,
        Func<IReadOnlyList<PackSource>>? packSources = null)
    {
        _logger = logger;
        _packSources = packSources;
        _pipelinesDirectory = pipelinesDirectory ?? AppPaths.Pipelines;
        Directory.CreateDirectory(_pipelinesDirectory);
        _projectRoot = NormalizeProject(projectDirectory);
        _builtIns = BuildBuiltInPipelines();
        Rescan();
    }

    /// <summary>Raised after a pipeline was saved, copied or deleted, or the project folder changed (pipeline pickers refresh on it).</summary>
    public event EventHandler? Changed;

    /// <summary>Folder that holds the user's global pipeline files.</summary>
    public string PipelinesDirectory => _pipelinesDirectory;

    /// <summary>The project folder whose <c>.aiagent/pipelines</c> is loaded; null when there is none.</summary>
    public string? ProjectDirectory => _projectRoot;

    /// <summary><c>&lt;project&gt;/.aiagent/pipelines</c>; null when there is no project folder.</summary>
    public string? ProjectPipelinesDirectory => _projectRoot is null ? null : Path.Combine(_projectRoot, ".aiagent", "pipelines");

    /// <summary>
    /// Where a new flow is saved by default: the project when the project folder is a git repository (the flow is then shared
    /// with the team), otherwise global.
    /// </summary>
    public PipelineScope DefaultSaveScope =>
        _projectRoot is not null && IsGitRepository(_projectRoot) ? PipelineScope.Project : PipelineScope.Global;

    /// <summary>Point the loader at another project folder (the working directory changed) and reload.</summary>
    public void SetProjectDirectory(string? projectDirectory)
    {
        var normalized = NormalizeProject(projectDirectory);
        if (string.Equals(normalized, _projectRoot, PathComparison)) return;
        _projectRoot = normalized;
        Rescan();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when <paramref name="directory"/> is inside a git work tree (a <c>.git</c> folder or file in it or a parent).</summary>
    public static bool IsGitRepository(string? directory)
    {
        try
        {
            for (var dir = string.IsNullOrWhiteSpace(directory) ? null : new DirectoryInfo(directory); dir != null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return true;
            }
        }
        catch { /* unreadable folder: treat as not a repository */ }
        return false;
    }

    /// <summary>
    /// The project folder as used for loading. A folder whose pipelines folder is the global one (the home folder, say)
    /// is no project: it would list every pipeline twice.
    /// </summary>
    private string? NormalizeProject(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) return null;
        string root;
        try { root = Path.GetFullPath(projectDirectory.Trim()); }
        catch { return null; }
        var pipelines = Path.GetFullPath(Path.Combine(root, ".aiagent", "pipelines"));
        return string.Equals(pipelines.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_pipelinesDirectory).TrimEnd(Path.DirectorySeparatorChar), PathComparison)
            ? null
            : root;
    }

    /// <summary>True for a pipeline that ships with the app and has no global or project file hiding it.</summary>
    public bool IsBuiltIn(string name) => GetEntry(name)?.Scope == PipelineScope.BuiltIn;

    /// <summary>Path of the global file for <paramref name="name"/> (whether or not it exists).</summary>
    public string GetPipelinePath(string name) => Path.Combine(_pipelinesDirectory, $"{name}.json");

    /// <summary>Path a pipeline would be saved to in <paramref name="scope"/> (whether or not it exists).</summary>
    public string GetPipelinePath(string name, PipelineScope scope)
    {
        var directory = DirectoryFor(scope);
        return GetEntry(name, scope)?.FilePath ?? Path.Combine(directory, $"{name}.json");
    }

    /// <summary>The file of the pipeline in force; null for a built-in one.</summary>
    public string? GetFilePath(string name) => GetEntry(name)?.FilePath;

    private string DirectoryFor(PipelineScope scope) => scope switch
    {
        PipelineScope.Global => _pipelinesDirectory,
        PipelineScope.Project => ProjectPipelinesDirectory
            ?? throw new InvalidOperationException("There is no project folder to save a project pipeline into."),
        _ => throw new ArgumentException("Built-in and pack pipelines are read-only; choose the project or global scope.", nameof(scope))
    };

    /// <summary>Re-read the global and project pipeline folders (after files changed on disk).</summary>
    public void Reload() => Rescan();

    /// <summary>
    /// Delete the file of the pipeline in force (or the one in <paramref name="scope"/>). A definition it hid, in a lower
    /// scope or built in, becomes visible again.
    /// </summary>
    public bool DeletePipeline(string name, PipelineScope? scope = null)
    {
        var entry = scope is { } s ? GetEntry(name, s) : GetEntry(name);
        if (entry?.Scope == PipelineScope.Pack)
            throw new InvalidOperationException($"'{name}' comes from the pack '{entry.PackName}' and is read-only. Copy it to your project or global flows to change it, or remove the pack.");
        if (entry?.FilePath is not { } path || !File.Exists(path)) return false;
        File.Delete(path);
        Rescan();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public SdlcPipelineDefinition? GetPipeline(string name) =>
        _pipelines.TryGetValue(name, out var pipeline) ? pipeline : null;

    public IEnumerable<SdlcPipelineDefinition> GetAllPipelines() => _pipelines.Values;

    /// <summary>The pipeline in force for <paramref name="name"/> with its scope, file and what it overrides.</summary>
    public PipelineEntry? GetEntry(string name) =>
        GetEntry(name, PipelineScope.Project) ?? GetEntry(name, PipelineScope.Global) ?? GetEntry(name, PipelineScope.Pack) ?? GetEntry(name, PipelineScope.BuiltIn);

    /// <summary>The definition stored in one scope, even when a higher scope hides it. Its <c>Overrides</c> names the lower scope it hides.</summary>
    public PipelineEntry? GetEntry(string name, PipelineScope scope)
    {
        var project = _project;
        var global = _global;
        var pack = _pack;
        switch (scope)
        {
            case PipelineScope.Project:
                return project.TryGetValue(name, out var p)
                    ? p with { Overrides = global.ContainsKey(name) ? PipelineScope.Global : pack.ContainsKey(name) ? PipelineScope.Pack : _builtIns.ContainsKey(name) ? PipelineScope.BuiltIn : null }
                    : null;
            case PipelineScope.Global:
                return global.TryGetValue(name, out var g)
                    ? g with { Overrides = pack.ContainsKey(name) ? PipelineScope.Pack : _builtIns.ContainsKey(name) ? PipelineScope.BuiltIn : null }
                    : null;
            case PipelineScope.Pack:
                return pack.TryGetValue(name, out var k)
                    ? k with { Overrides = _builtIns.ContainsKey(name) ? PipelineScope.BuiltIn : null }
                    : null;
            default:
                return _builtIns.TryGetValue(name, out var b) ? new PipelineEntry(b, PipelineScope.BuiltIn, null, null) : null;
        }
    }

    /// <summary>Every pipeline in force with its scope, sorted by name.</summary>
    public IReadOnlyList<PipelineEntry> GetEntries() =>
        _pipelines.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => GetEntry(n)!).ToList();

    private void Rescan()
    {
        var packs = new Dictionary<string, PipelineEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _packSources?.Invoke() ?? Array.Empty<PackSource>())
            foreach (var (name, entry) in ReadDirectory(source.PipelinesDirectory, PipelineScope.Pack))
                packs[name] = entry with { PackName = source.Name };
        var global = ReadDirectory(_pipelinesDirectory, PipelineScope.Global);
        var project = ProjectPipelinesDirectory is { } dir
            ? ReadDirectory(dir, PipelineScope.Project)
            : new Dictionary<string, PipelineEntry>(StringComparer.OrdinalIgnoreCase);

        // Built-in -> pack -> global -> project: a later layer replaces an earlier one with the same name.
        var effective = new Dictionary<string, SdlcPipelineDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, pipeline) in _builtIns) effective[name] = pipeline;
        foreach (var (name, entry) in packs) effective[name] = entry.Pipeline;
        foreach (var (name, entry) in global) effective[name] = entry.Pipeline;
        foreach (var (name, entry) in project) effective[name] = entry.Pipeline;

        _pack = packs;
        _global = global;
        _project = project;
        _pipelines = effective;
    }

    private Dictionary<string, PipelineEntry> ReadDirectory(string directory, PipelineScope scope)
    {
        var result = new Dictionary<string, PipelineEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory)) return result;

            foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var pipeline = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, FileJsonOptions);

                    if (pipeline == null || string.IsNullOrEmpty(pipeline.Name) || pipeline.Stages.Count == 0)
                    {
                        _logger.LogWarning("Skipping invalid pipeline file: {File}", file);
                        continue;
                    }

                    result[pipeline.Name] = new PipelineEntry(pipeline, scope, file, null);
                    _logger.LogInformation("Loaded {Scope} pipeline '{Name}' from {File}", scope.Label(), pipeline.Name, file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load pipeline from {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {Scope} pipelines from {Directory}", scope.Label(), directory);
        }
        return result;
    }

    /// <summary>Save a pipeline definition into the global or the project pipelines folder. Returns the file written.</summary>
    public async Task<string> SavePipelineAsync(SdlcPipelineDefinition pipeline, PipelineScope scope = PipelineScope.Global)
    {
        if (string.IsNullOrWhiteSpace(pipeline.Name) || !Context.LocalFileStore.IsValidName(pipeline.Name))
            throw new ArgumentException($"Pipeline name '{pipeline.Name}' must be kebab-case (letters, numbers, hyphens).", nameof(pipeline));
        var directory = DirectoryFor(scope);
        Directory.CreateDirectory(directory);
        // Keep editing the same file when the pipeline already lives in this scope (its file may not be named after it).
        var filePath = GetEntry(pipeline.Name, scope)?.FilePath ?? Path.Combine(directory, $"{pipeline.Name}.json");
        var json = JsonSerializer.Serialize(pipeline, FileJsonOptions);
        await Context.LocalFileStore.WriteAllTextAtomicAsync(filePath, json + "\n");
        Rescan();
        _logger.LogInformation("Saved {Scope} pipeline '{Name}' to {File}", scope.Label(), pipeline.Name, filePath);
        Changed?.Invoke(this, EventArgs.Empty);
        return filePath;
    }

    /// <summary>
    /// Copy a pipeline into the project or the global folder, from the nearest other scope that has it (for the project:
    /// global, then built-in; for global: project, then built-in). Refuses to overwrite unless <paramref name="overwrite"/>.
    /// </summary>
    public async Task<PipelineEntry> CopyPipelineAsync(string name, PipelineScope target, bool overwrite = false)
    {
        if (target == PipelineScope.BuiltIn)
            throw new ArgumentException("A pipeline can only be copied to the project or global scope.", nameof(target));

        PipelineEntry? source = null;
        foreach (var scope in new[] { PipelineScope.Project, PipelineScope.Global, PipelineScope.Pack, PipelineScope.BuiltIn })
        {
            if (scope == target) continue;
            if (GetEntry(name, scope) is { } found) { source = found; break; }
        }
        if (source == null)
            throw new InvalidOperationException(GetEntry(name, target) != null
                ? $"Pipeline '{name}' exists only in the {target.Label()} scope; there is nothing to copy."
                : $"Unknown pipeline '{name}'. Run 'pipeline list' to see available pipelines.");
        if (!overwrite && GetEntry(name, target) != null)
            throw new InvalidOperationException($"A {target.Label()} pipeline '{name}' already exists. Use --force to overwrite it.");

        await SavePipelineAsync(source.Pipeline, target);
        return GetEntry(name, target)!;
    }

    private static Dictionary<string, SdlcPipelineDefinition> BuildBuiltInPipelines()
    {
        var pipelines = new Dictionary<string, SdlcPipelineDefinition>(StringComparer.OrdinalIgnoreCase);
        pipelines["full-sdlc"] = new SdlcPipelineDefinition
        {
            Name = "full-sdlc",
            Description = "Analyze, implement, review, test, and deploy - the full software development lifecycle.",
            Stages = new List<SdlcStageDefinition>
            {
                new()
                {
                    Role = "planner",
                    Name = "Analyze",
                    PromptTemplate =
                        "Task: {task}\n\n" +
                        "Analyze this task and produce a concrete implementation plan: files to read/touch, " +
                        "the steps to take, risks, dependencies, and edge cases."
                },
                new()
                {
                    Role = "implementer",
                    Name = "Implement",
                    PromptTemplate =
                        "Using the plan above, implement the following task:\n\n{task}\n\n" +
                        "Make the necessary code changes, then run diagnostics to verify the project still builds."
                },
                new()
                {
                    Role = "reviewer",
                    Name = "Review",
                    PromptTemplate =
                        "Review the changes made above for the task:\n\n{task}\n\n" +
                        "Check correctness, edge cases, style, and whether the plan was followed. " +
                        "List any issues found with specific file/line references."
                },
                new()
                {
                    Role = "tester",
                    Name = "Test",
                    PromptTemplate =
                        "Write and/or run tests (backend and frontend, as applicable) covering the changes " +
                        "for the task:\n\n{task}\n\nReport pass/fail results and any regressions found."
                },
                new()
                {
                    Role = "deployer",
                    Name = "Deploy",
                    RequireConfirmation = true,
                    PromptTemplate =
                        "Prepare deployment for the task:\n\n{task}\n\n" +
                        "Verify the build is green and tests passed, then run or describe the deploy steps " +
                        "and report the outcome. Stop and explain if anything looks unsafe or irreversible."
                }
            }
        };

        pipelines["quick-fix"] = new SdlcPipelineDefinition
        {
            Name = "quick-fix",
            Description = "Lightweight pipeline for small fixes: implement then review, skipping plan/test/deploy.",
            Stages = new List<SdlcStageDefinition>
            {
                new()
                {
                    Role = "implementer",
                    Name = "Implement",
                    PromptTemplate = "Task: {task}\n\nImplement this task directly with minimal, targeted changes, then run diagnostics."
                },
                new()
                {
                    Role = "reviewer",
                    Name = "Review",
                    PromptTemplate = "Review the change above for the task:\n\n{task}"
                }
            }
        };
        return pipelines;
    }
}

/// <summary>
/// Builds and runs an <see cref="SdlcPipelineDefinition"/> as a multi-agent session: each stage
/// runs under its matching role preset (system prompt, tools, permission mode) via the same shared
/// <see cref="AgentOrchestrator"/> instance, registered per role, and every stage shares one
/// conversation/session so later stages see everything earlier stages did and produced.
/// </summary>
public class SdlcPipelineRunner
{
    private readonly AgentSessionCoordinator _coordinator;
    private readonly RolePresetLoader _presetLoader;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<SdlcPipelineRunner> _logger;
    private readonly ICharacterRegistry? _characters;
    private readonly IAiProviderResolver? _providers;
    private readonly ModelCatalog? _catalog;
    private readonly Flows.FlowRunStore? _runStore;
    private readonly Flows.IExternalAgentRunner? _externalAgents;
    private readonly ISkillRegistry? _skills;
    private readonly ICheckpointManager? _checkpoints;

    public SdlcPipelineRunner(
        AgentSessionCoordinator coordinator,
        RolePresetLoader presetLoader,
        IAgentOrchestrator orchestrator,
        ILogger<SdlcPipelineRunner> logger,
        ICharacterRegistry? characters = null,
        IAiProviderResolver? providers = null,
        ModelCatalog? catalog = null,
        Flows.FlowRunStore? runStore = null,
        Flows.IExternalAgentRunner? externalAgents = null,
        ISkillRegistry? skills = null,
        ICheckpointManager? checkpoints = null)
    {
        _skills = skills;
        _checkpoints = checkpoints;
        _externalAgents = externalAgents;
        _runStore = runStore;
        _providers = providers;
        _catalog = catalog;
        _coordinator = coordinator;
        _presetLoader = presetLoader;
        _orchestrator = orchestrator;
        _logger = logger;
        _characters = characters;
    }

    /// <summary>
    /// Parse a cast string such as <c>planner=alex-architect,implementer=sam-dev</c>
    /// (role or stage name → character id). Throws <see cref="FormatException"/> on bad syntax.
    /// </summary>
    public static Dictionary<string, string> ParseCast(string? cast)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cast)) return map;
        foreach (var pair in cast.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new FormatException($"Invalid cast entry '{pair}'. Use role=character, e.g. planner=alex-architect.");
            map[parts[0]] = parts[1];
        }
        return map;
    }

    /// <summary>
    /// Character for a stage: run-time cast (by stage name, then role) &gt; the stage's
    /// <see cref="SdlcStageDefinition.Character"/> &gt; a user character whose id equals the role.
    /// Returns null to run the stage on its plain role preset.
    /// </summary>
    private CharacterInfo? ResolveStageCharacter(SdlcStageDefinition stage, IReadOnlyDictionary<string, string>? cast)
    {
        string? id = null;
        if (cast != null && (cast.TryGetValue(stage.Name, out var byName) || cast.TryGetValue(stage.Role, out byName)))
            id = byName;
        id ??= stage.Character;
        var explicitChoice = id != null;
        id ??= stage.Role;

        if (_characters == null)
        {
            if (explicitChoice)
                throw new InvalidOperationException($"Stage '{stage.Name}' asks for character '{id}', but no character registry is available.");
            return null;
        }

        var character = _characters.GetAsync(id).GetAwaiter().GetResult();
        if (character == null)
        {
            if (explicitChoice)
                throw new InvalidOperationException($"Unknown character '{id}' for stage '{stage.Name}'. Run 'characters list' to see available characters.");
            return null;
        }
        if (!character.IsValid)
            throw new InvalidOperationException($"Character '{id}' is invalid: {string.Join(" ", character.ValidationErrors)}");
        // Built-in characters are just the role presets; keep the plain path for them.
        return character.IsBuiltIn && !explicitChoice ? null : character;
    }

    /// <summary>Builds the multi-agent session plan for a linear pipeline run without executing it.</summary>
    public SessionPlan BuildPlan(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        string? model = null,
        int maxIterationsPerStage = 30,
        IReadOnlyDictionary<string, string>? cast = null)
    {
        var steps = new List<SessionStep>();

        foreach (var stage in pipeline.Stages)
        {
            if (!stage.Enabled) continue;
            if (string.IsNullOrWhiteSpace(stage.Role) && string.IsNullOrWhiteSpace(stage.Character))
            {
                _logger.LogWarning("Skipping pipeline stage '{Name}' with no role or character", stage.Name);
                continue;
            }

            var prompt = stage.PromptTemplate.Replace("{task}", task);
            steps.Add(BuildStageStep(stage, prompt, workingDirectory, model, maxIterationsPerStage, cast));
        }

        return new SessionPlan
        {
            SessionId = sessionId,
            UserGoal = task,
            Steps = steps
        };
    }

    /// <summary>
    /// The external agent that runs a stage, or null for a normal built-in step: either an <c>acp</c> stage with its own
    /// command, or a stage whose (cast or stage) character has <c>runtime: acp</c>. The character's persona and skills become
    /// the instructions sent with the prompt.
    /// </summary>
    public Flows.ExternalAgentTarget? ResolveExternalTarget(SdlcStageDefinition stage, IReadOnlyDictionary<string, string>? cast)
    {
        if (stage.IsAcp)
            return new Flows.ExternalAgentTarget(stage.AcpCommand ?? string.Empty, stage.AcpArgs ?? new List<string>(), null, null, string.Empty);

        var character = ResolveStageCharacter(stage, cast);
        if (character is not { IsExternal: true }) return null;

        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(character.Persona)) blocks.Add(character.Persona.Trim());
        if (_skills != null)
        {
            var names = (character.PinnedSkills).Concat(character.Skills ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var invocation = _skills.InvokeAsync(name).GetAwaiter().GetResult();
                if (invocation != null) blocks.Add(invocation.ToPromptBlock());
                else _logger.LogWarning("Skill {Skill} of character {Character} was not found", name, character.Id);
            }
        }
        var instructions = blocks.Count == 0 ? string.Empty : "Instructions for your role in this task:\n\n" + string.Join("\n\n", blocks);
        return new Flows.ExternalAgentTarget(character.Command ?? string.Empty, character.Args, character.Id, character.PermissionMode, instructions);
    }

    /// <summary>
    /// The session step for one stage: resolves its character (cast &gt; stage character &gt; a user
    /// character named like the role), builds its options and registers its agent. Flows pass
    /// <paramref name="agentId"/> (the node id) so the same character can appear in several nodes.
    /// </summary>
    public SessionStep BuildStageStep(
        SdlcStageDefinition stage,
        string prompt,
        string workingDirectory,
        string? model = null,
        int maxIterations = 30,
        IReadOnlyDictionary<string, string>? cast = null,
        string? agentId = null)
    {
        var character = ResolveStageCharacter(stage, cast);
        var role = !string.IsNullOrWhiteSpace(stage.Role) ? stage.Role : character?.BaseRole ?? string.Empty;
        agentId ??= character?.Id ?? role;

        // The orchestrator itself is stateless per-run (all role behavior comes from
        // AgentOptions), so the same instance can safely serve every role in the pipeline.
        if (!_coordinator.Agents.ContainsKey(agentId))
            _coordinator.RegisterAgent(agentId, _orchestrator);

        var baseOptions = new AgentOptions
        {
            Model = model,
            WorkingDirectory = workingDirectory,
            MaxIterations = maxIterations
        };

        AgentOptions options;
        if (character != null)
        {
            var basePreset = character.BaseRole == null ? null : _presetLoader.GetPreset(character.BaseRole);
            options = CharacterResolver.Apply(baseOptions, character, basePreset, overwrite: true, useBaseRolePermission: true);
            // A character's own model is more specific than the session-wide --model; Apply keeps --model otherwise.
            if (character.BaseRole == null && character.PermissionMode == null) options = options with { PermissionMode = PermissionMode.Ask };
        }
        else
        {
            var preset = _presetLoader.GetPreset(role);
            if (preset == null)
                _logger.LogWarning("No role preset found for '{Role}'; stage will run with generic instructions only", role);
            options = baseOptions with
            {
                PermissionMode = preset?.DefaultPermissionMode ?? PermissionMode.Ask,
                EnabledTools = preset?.AllowedTools ?? new List<string>(),
                RoleSystemPrompt = preset?.SystemPrompt
            };
        }

        options = ApplyStageModel(stage, options);

        return new SessionStep
        {
            AgentId = agentId,
            Role = role,
            CharacterId = character?.Id,
            Prompt = prompt,
            RequireConfirmation = stage.RequireConfirmation,
            Options = options
        };
    }

    /// <summary>
    /// Applies the stage's own provider and model on top of what the character and the session gave
    /// (stage &gt; character &gt; session default). A tier alias is resolved through the model catalog for the
    /// step's provider. A different provider without a model uses that provider's configured default model,
    /// because the inherited model id belongs to the session's provider.
    /// </summary>
    internal AgentOptions ApplyStageModel(SdlcStageDefinition stage, AgentOptions options)
    {
        var provider = string.IsNullOrWhiteSpace(stage.Provider) ? null : stage.Provider.Trim().ToLowerInvariant();
        var model = string.IsNullOrWhiteSpace(stage.Model) ? null : stage.Model.Trim();
        if (provider == null && model == null) return options;

        var defaultProvider = _providers?.DefaultProviderName?.ToLowerInvariant();
        var effectiveProvider = provider ?? defaultProvider;
        var differs = provider != null && !string.Equals(provider, defaultProvider, StringComparison.OrdinalIgnoreCase);

        if (provider != null) options = options with { Provider = differs ? provider : null };

        if (model != null)
        {
            var catalog = _catalog ?? ModelCatalogLoader.Default;
            var resolved = effectiveProvider == null ? (ModelCatalog.TryTierOfName(model, out _) ? null : model)
                : catalog.ResolveModelSetting(model, effectiveProvider);
            if (resolved != null) return options with { Model = resolved };
            _logger.LogWarning("Stage '{Stage}': model '{Model}' could not be resolved for provider '{Provider}'; using the inherited model",
                stage.Id ?? stage.Name, model, effectiveProvider ?? "(default)");
        }
        // Another provider cannot use the model chosen for the session's provider.
        return differs ? options with { Model = null } : options;
    }

    /// <summary>
    /// Builds and runs the pipeline, streaming tagged events. A linear pipeline runs its stages in
    /// order on one shared history; a flow (pipeline with edges) runs through <see cref="Flows.FlowRunner"/>.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        string? model = null,
        int maxIterationsPerStage = 30,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? cast = null,
        int? maxParallel = null,
        decimal? maxBudgetUsd = null,
        Flows.FlowReplayPlan? replay = null,
        PermissionMode? permissionMode = null)
    {
        if (pipeline.IsFlow)
        {
            var flow = new Flows.FlowRunner(_coordinator, this, _logger, _characters, _externalAgents, _checkpoints);
            return flow.RunAsync(pipeline, task, sessionId, workingDirectory, new Flows.FlowRunOptions
            {
                Model = model,
                MaxIterationsPerNode = maxIterationsPerStage,
                Cast = cast,
                MaxParallel = maxParallel ?? pipeline.MaxParallel,
                MaxBudgetUsd = maxBudgetUsd ?? pipeline.MaxBudgetUsd,
                Store = _runStore,
                Replay = replay,
                PermissionMode = permissionMode
            }, cancellationToken);
        }
        var plan = BuildPlan(pipeline, task, sessionId, workingDirectory, model, maxIterationsPerStage, cast);
        return _coordinator.RunAsync(plan, cancellationToken);
    }
}
