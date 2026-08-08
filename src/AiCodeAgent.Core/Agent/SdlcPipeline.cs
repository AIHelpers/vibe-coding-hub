using System.Text.Json;
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
    /// <summary>Human-friendly stage name for display (e.g. "Analyze", "Implement").</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Prompt sent to the agent for this stage. "{task}" is replaced with the user's task text.</summary>
    public string PromptTemplate { get; init; } = string.Empty;
    /// <summary>Whether this stage runs. Set false to skip a stage without deleting it from the pipeline.</summary>
    public bool Enabled { get; init; } = true;
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

    public SdlcPipelineRunner(
        AgentSessionCoordinator coordinator,
        RolePresetLoader presetLoader,
        IAgentOrchestrator orchestrator,
        ILogger<SdlcPipelineRunner> logger)
    {
        _coordinator = coordinator;
        _presetLoader = presetLoader;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>Builds the multi-agent session plan for a pipeline run without executing it.</summary>
    public SessionPlan BuildPlan(
        SdlcPipelineDefinition pipeline,
        string task,
        string sessionId,
        string workingDirectory,
        string? model = null,
        int maxIterationsPerStage = 30)
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

            // The orchestrator itself is stateless per-run (all role behavior comes from
            // AgentOptions), so the same instance can safely serve every role in the pipeline.
            if (!_coordinator.Agents.ContainsKey(stage.Role))
                _coordinator.RegisterAgent(stage.Role, _orchestrator);

            var preset = _presetLoader.GetPreset(stage.Role);
            if (preset == null)
                _logger.LogWarning("No role preset found for '{Role}'; stage will run with generic instructions only", stage.Role);

            var prompt = stage.PromptTemplate.Replace("{task}", task);

            steps.Add(new SessionStep
            {
                AgentId = stage.Role,
                Role = stage.Role,
                Prompt = prompt,
                Options = new AgentOptions
                {
                    Model = model,
                    WorkingDirectory = workingDirectory,
                    MaxIterations = maxIterationsPerStage,
                    PermissionMode = preset?.DefaultPermissionMode ?? PermissionMode.Ask,
                    EnabledTools = preset?.AllowedTools ?? new List<string>(),
                    RoleSystemPrompt = preset?.SystemPrompt
                }
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
        CancellationToken cancellationToken = default)
    {
        var plan = BuildPlan(pipeline, task, sessionId, workingDirectory, model, maxIterationsPerStage);
        return _coordinator.RunAsync(plan, cancellationToken);
    }
}
