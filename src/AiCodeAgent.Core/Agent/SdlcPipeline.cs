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
    /// <summary>Role name, resolved against <see cref="RolePresetLoader"/> (e.g. "planner", "implementer").</summary>
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

    public SdlcPipelineLoader(ILogger<SdlcPipelineLoader> logger)
    {
        _logger = logger;
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent");
        _pipelinesDirectory = Path.Combine(configDir, "pipelines");
        Directory.CreateDirectory(_pipelinesDirectory);

        LoadBuiltInPipelines();
        LoadUserPipelines();
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
                    var pipeline = JsonSerializer.Deserialize<SdlcPipelineDefinition>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

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
        var filePath = Path.Combine(_pipelinesDirectory, $"{pipeline.Name}.json");
        var json = JsonSerializer.Serialize(pipeline, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        });
        await File.WriteAllTextAsync(filePath, json);
        _pipelines[pipeline.Name] = pipeline;
        _logger.LogInformation("Saved pipeline '{Name}' to {File}", pipeline.Name, filePath);
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

    /// <summary>Builds the multi-agent session plan for a pipeline run without executing it.</summary>
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
            if (string.IsNullOrWhiteSpace(stage.Role))
            {
                _logger.LogWarning("Skipping pipeline stage '{Name}' with no role", stage.Name);
                continue;
            }

            var character = ResolveStageCharacter(stage, cast);
            var agentId = character?.Id ?? stage.Role;

            // The orchestrator itself is stateless per-run (all role behavior comes from
            // AgentOptions), so the same instance can safely serve every role in the pipeline.
            if (!_coordinator.Agents.ContainsKey(agentId))
                _coordinator.RegisterAgent(agentId, _orchestrator);

            var prompt = stage.PromptTemplate.Replace("{task}", task);
            var baseOptions = new AgentOptions
            {
                Model = model,
                WorkingDirectory = workingDirectory,
                MaxIterations = maxIterationsPerStage
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
                var preset = _presetLoader.GetPreset(stage.Role);
                if (preset == null)
                    _logger.LogWarning("No role preset found for '{Role}'; stage will run with generic instructions only", stage.Role);
                options = baseOptions with
                {
                    PermissionMode = preset?.DefaultPermissionMode ?? PermissionMode.Ask,
                    EnabledTools = preset?.AllowedTools ?? new List<string>(),
                    RoleSystemPrompt = preset?.SystemPrompt
                };
            }

            steps.Add(new SessionStep
            {
                AgentId = agentId,
                Role = stage.Role,
                CharacterId = character?.Id,
                Prompt = prompt,
                RequireConfirmation = stage.RequireConfirmation,
                Options = options
            });
        }

        return new SessionPlan
        {
            SessionId = sessionId,
            UserGoal = task,
            Steps = steps
        };
    }

    /// <summary>Builds and runs the pipeline, streaming tagged events from every stage in order.</summary>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        string? model = null,
        int maxIterationsPerStage = 30,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? cast = null)
    {
        var plan = BuildPlan(pipeline, task, sessionId, workingDirectory, model, maxIterationsPerStage, cast);
        return _coordinator.RunAsync(plan, cancellationToken);
    }
}
