using System.Text.Json;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Loads agent role presets from JSON files.
/// Ships with built-in defaults for planner, implementer, and reviewer.
/// Users/teams can add custom presets by dropping JSON files in the presets directory.
/// </summary>
public class RolePresetLoader
{
    private readonly ILogger<RolePresetLoader> _logger;
    private readonly string _presetsDirectory;
    private readonly Dictionary<string, AgentRolePreset> _presets = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, AgentRolePreset> Presets => _presets;

    public RolePresetLoader(ILogger<RolePresetLoader> logger)
    {
        _logger = logger;
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent");
        _presetsDirectory = Path.Combine(configDir, "presets");
        Directory.CreateDirectory(_presetsDirectory);

        LoadBuiltInPresets();
        LoadUserPresets();
    }

    public AgentRolePreset? GetPreset(string role) =>
        _presets.TryGetValue(role, out var preset) ? preset : null;

    public IEnumerable<AgentRolePreset> GetAllPresets() => _presets.Values;

    private void LoadBuiltInPresets()
    {
        _presets["planner"] = new AgentRolePreset
        {
            Role = "planner",
            Description = "Analyzes the task and produces a plan. Read-only by default.",
            SystemPrompt = """
                You are the PLANNER agent in a multi-agent coding session.
                Your job is to analyze the user's request and produce a clear, actionable plan.
                
                Responsibilities:
                - Understand the user's goal and break it into concrete steps
                - Identify files that need to be read or modified
                - Identify risks, dependencies, and edge cases
                - Produce a structured plan that the implementer can follow
                
                You are read-only: do NOT edit files or execute commands.
                Focus on analysis and planning only.
                """,
            DefaultPermissionMode = PermissionMode.Plan,
            AllowedTools = new List<string> { "ReadFile", "SearchCode", "ListDirectory", "Grep" }
        };

        _presets["implementer"] = new AgentRolePreset
        {
            Role = "implementer",
            Description = "Executes the plan by editing files and running commands.",
            SystemPrompt = """
                You are the IMPLEMENTER agent in a multi-agent coding session.
                Your job is to execute the plan produced by the planner.
                
                Responsibilities:
                - Read the plan and understand the required changes
                - Read relevant files before editing them
                - Make minimal, targeted changes
                - Run diagnostics after making changes to verify correctness
                - Report what you changed and why
                
                Follow the plan strictly. If you discover the plan is wrong, note it
                in your response rather than improvising major deviations.
                """,
            DefaultPermissionMode = PermissionMode.AutoEdit,
            AllowedTools = new List<string>
            {
                "ReadFile", "WriteFile", "EditFile", "ListDirectory", "Grep",
                "ExecuteCommand", "RunDiagnostics", "Git"
            }
        };

        _presets["reviewer"] = new AgentRolePreset
        {
            Role = "reviewer",
            Description = "Reviews the implementer's changes for correctness and quality.",
            SystemPrompt = """
                You are the REVIEWER agent in a multi-agent coding session.
                Your job is to review the changes made by the implementer.
                
                Responsibilities:
                - Read the changes made by the implementer
                - Check for correctness, edge cases, and potential bugs
                - Verify the changes match the original plan
                - Check for style consistency and maintainability
                - Report issues with specific line references
                
                You are read-only: do NOT edit files or execute commands.
                Your output is a review report with actionable feedback.
                """,
            DefaultPermissionMode = PermissionMode.Plan,
            AllowedTools = new List<string> { "ReadFile", "SearchCode", "ListDirectory", "Grep", "RunDiagnostics" }
        };
    }

    private void LoadUserPresets()
    {
        try
        {
            if (!Directory.Exists(_presetsDirectory)) return;

            foreach (var file in Directory.GetFiles(_presetsDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var preset = JsonSerializer.Deserialize<AgentRolePreset>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (preset == null || string.IsNullOrEmpty(preset.Role))
                    {
                        _logger.LogWarning("Skipping invalid role preset file: {File}", file);
                        continue;
                    }

                    _presets[preset.Role] = preset;
                    _logger.LogInformation("Loaded role preset '{Role}' from {File}", preset.Role, file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load role preset from {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load user role presets");
        }
    }

    /// <summary>Save a custom role preset to the user presets directory.</summary>
    public async Task SavePresetAsync(AgentRolePreset preset)
    {
        var filePath = Path.Combine(_presetsDirectory, $"{preset.Role}.json");
        var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        });
        await File.WriteAllTextAsync(filePath, json);
        _presets[preset.Role] = preset;
        _logger.LogInformation("Saved role preset '{Role}' to {File}", preset.Role, filePath);
    }
}