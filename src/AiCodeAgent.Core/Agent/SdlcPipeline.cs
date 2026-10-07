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
    /// Verdicts this stage must choose from in a flow (e.g. ["approved", "rejected"]). The agent
    /// ends its answer with <c>OUTCOME: &lt;one of them&gt;</c> and edges route on it.
    /// </summary>
    public List<string>? Outcomes { get; init; }
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

    /// <summary>True when this pipeline is a flow (has edges).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFlow => Edges is { Count: > 0 };
}

/// <summary>
/// Loads SDLC pipeline definitions from JSON files, the same pattern as <see cref="RolePresetLoader"/>.
/// Ships with a built-in "full-sdlc" pipeline (analyze -> implement -> review -> test -> deploy) and a
/// lightweight "quick-fix" pipeline. Users/teams can add or override pipelines by dropping JSON files
/// in ~/.aiagent/pipelines, so the whole lifecycle is configurable without recompiling the app.
/// </summary>
public class SdlcPipelineLoader
{
    private readonly ILogger<SdlcPipelineLoader> _logger;
    private readonly string _pipelinesDirectory;
    private readonly Dictionary<string, SdlcPipelineDefinition> _pipelines = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SdlcPipelineDefinition> Pipelines => _pipelines;

    private readonly HashSet<string> _builtInNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON used for pipeline files: camelCase, indented, nulls left out (so linear pipelines stay short).</summary>
    public static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="pipelinesDirectory">Folder with user pipelines; defaults to <c>~/.aiagent/pipelines</c>.</param>
    public SdlcPipelineLoader(ILogger<SdlcPipelineLoader> logger, string? pipelinesDirectory = null)
    {
        _logger = logger;
        _pipelinesDirectory = pipelinesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent", "pipelines");
        Directory.CreateDirectory(_pipelinesDirectory);

        LoadBuiltInPipelines();
        _builtInNames.UnionWith(_pipelines.Keys);
        LoadUserPipelines();
    }

    /// <summary>Raised after a pipeline was saved or deleted (pipeline pickers refresh on it).</summary>
    public event EventHandler? Changed;

    /// <summary>Folder that holds user pipeline files.</summary>
    public string PipelinesDirectory => _pipelinesDirectory;

    /// <summary>True for a pipeline that ships with the app and has no user file overriding it.</summary>
    public bool IsBuiltIn(string name) => _builtInNames.Contains(name) && !File.Exists(GetPipelinePath(name));

    /// <summary>Path of the user file for <paramref name="name"/> (whether or not it exists).</summary>
    public string GetPipelinePath(string name) => Path.Combine(_pipelinesDirectory, $"{name}.json");

    /// <summary>Re-read the user pipelines folder (after files changed on disk).</summary>
    public void Reload()
    {
        _pipelines.Clear();
        LoadBuiltInPipelines();
        LoadUserPipelines();
    }

    /// <summary>Delete a user pipeline file. A built-in pipeline it overrode becomes visible again.</summary>
    public bool DeletePipeline(string name)
    {
        var path = GetPipelinePath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public SdlcPipelineDefinition? GetPipeline(string name) =>
        _pipelines.TryGetValue(name, out var pipeline) ? pipeline : null;

    public IEnumerable<SdlcPipelineDefinition> GetAllPipelines() => _pipelines.Values;

    private void LoadBuiltInPipelines()
    {
        _pipelines["full-sdlc"] = new SdlcPipelineDefinition
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

        _pipelines["quick-fix"] = new SdlcPipelineDefinition
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
    }

    private void LoadUserPipelines()
    {
        try
        {
            if (!Directory.Exists(_pipelinesDirectory)) return;

            foreach (var file in Directory.GetFiles(_pipelinesDirectory, "*.json"))
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

                    _pipelines[pipeline.Name] = pipeline;
                    _logger.LogInformation("Loaded pipeline '{Name}' from {File}", pipeline.Name, file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load pipeline from {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load user pipelines");
        }
    }

    /// <summary>Save a custom pipeline definition to the user pipelines directory.</summary>
    public async Task SavePipelineAsync(SdlcPipelineDefinition pipeline)
    {
        if (string.IsNullOrWhiteSpace(pipeline.Name) || !Context.LocalFileStore.IsValidName(pipeline.Name))
            throw new ArgumentException($"Pipeline name '{pipeline.Name}' must be kebab-case (letters, numbers, hyphens).", nameof(pipeline));
        var filePath = GetPipelinePath(pipeline.Name);
        var json = JsonSerializer.Serialize(pipeline, FileJsonOptions);
        await Context.LocalFileStore.WriteAllTextAtomicAsync(filePath, json + "\n");
        _pipelines[pipeline.Name] = pipeline;
        _logger.LogInformation("Saved pipeline '{Name}' to {File}", pipeline.Name, filePath);
        Changed?.Invoke(this, EventArgs.Empty);
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

    public SdlcPipelineRunner(
        AgentSessionCoordinator coordinator,
        RolePresetLoader presetLoader,
        IAgentOrchestrator orchestrator,
        ILogger<SdlcPipelineRunner> logger,
        ICharacterRegistry? characters = null)
    {
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
        int? maxParallel = null)
    {
        if (pipeline.IsFlow)
        {
            var flow = new Flows.FlowRunner(_coordinator, this, _logger, _characters);
            return flow.RunAsync(pipeline, task, sessionId, workingDirectory, new Flows.FlowRunOptions
            {
                Model = model,
                MaxIterationsPerNode = maxIterationsPerStage,
                Cast = cast,
                MaxParallel = maxParallel ?? pipeline.MaxParallel
            }, cancellationToken);
        }
        var plan = BuildPlan(pipeline, task, sessionId, workingDirectory, model, maxIterationsPerStage, cast);
        return _coordinator.RunAsync(plan, cancellationToken);
    }
}
