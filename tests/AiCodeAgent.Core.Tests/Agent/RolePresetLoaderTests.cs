using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class RolePresetLoaderTests
{
    private static RolePresetLoader CreateLoader()
    {
        var logger = Substitute.For<ILogger<RolePresetLoader>>();
        return new RolePresetLoader(logger);
    }

    [Fact]
    public void Loader_ContainsBuiltInPresets()
    {
        var loader = CreateLoader();

        Assert.NotNull(loader.GetPreset("planner"));
        Assert.NotNull(loader.GetPreset("implementer"));
        Assert.NotNull(loader.GetPreset("reviewer"));
    }

    [Fact]
    public void PlannerPreset_IsReadOnly_ByDefault()
    {
        var loader = CreateLoader();
        var planner = loader.GetPreset("planner");

        Assert.NotNull(planner);
        Assert.Equal(PermissionMode.Plan, planner!.DefaultPermissionMode);
        Assert.DoesNotContain("write_file", planner.AllowedTools);
        Assert.DoesNotContain("execute_command", planner.AllowedTools);
    }

    [Fact]
    public void ImplementerPreset_HasWriteAndExecuteTools()
    {
        var loader = CreateLoader();
        var implementer = loader.GetPreset("implementer");

        Assert.NotNull(implementer);
        Assert.Equal(PermissionMode.AutoEdit, implementer!.DefaultPermissionMode);
        Assert.Contains("write_file", implementer.AllowedTools);
        Assert.Contains("edit_file", implementer.AllowedTools);
        Assert.Contains("execute_command", implementer.AllowedTools);
    }

    [Fact]
    public void ReviewerPreset_IsReadOnly_ByDefault()
    {
        var loader = CreateLoader();
        var reviewer = loader.GetPreset("reviewer");

        Assert.NotNull(reviewer);
        Assert.Equal(PermissionMode.Plan, reviewer!.DefaultPermissionMode);
        Assert.DoesNotContain("write_file", reviewer.AllowedTools);
        Assert.DoesNotContain("execute_command", reviewer.AllowedTools);
    }

    [Fact]
    public void GetPreset_UnknownRole_ReturnsNull()
    {
        var loader = CreateLoader();

        Assert.Null(loader.GetPreset("unknown-role"));
    }

    [Fact]
    public void GetAllPresets_ReturnsAllBuiltIns()
    {
        var loader = CreateLoader();

        var presets = loader.GetAllPresets().ToList();

        // At minimum the 3 built-ins must be present (user presets may add more)
        Assert.True(presets.Count >= 3);
        Assert.Contains(presets, p => p.Role == "planner");
        Assert.Contains(presets, p => p.Role == "implementer");
        Assert.Contains(presets, p => p.Role == "reviewer");
    }

    [Fact]
    public async Task SavePresetAsync_PersistsCustomPreset()
    {
        var loader = CreateLoader();
        var custom = new AgentRolePreset
        {
            Role = "tester",
            Description = "Runs tests and reports results",
            SystemPrompt = "You are the TESTER agent.",
            DefaultPermissionMode = PermissionMode.AutoEdit,
            AllowedTools = new List<string> { "ReadFile", "ExecuteCommand", "RunDiagnostics" }
        };

        await loader.SavePresetAsync(custom);

        var loaded = loader.GetPreset("tester");
        Assert.NotNull(loaded);
        Assert.Equal("tester", loaded!.Role);
        Assert.Equal(PermissionMode.AutoEdit, loaded.DefaultPermissionMode);
        Assert.Contains("ExecuteCommand", loaded.AllowedTools);
    }
}