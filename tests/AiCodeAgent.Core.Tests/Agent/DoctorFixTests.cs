using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

/// <summary><c>skills doctor --fix</c>: repairs preset files with unknown tool names.</summary>
public class DoctorFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "doctor-fix-" + Guid.NewGuid().ToString("N"));
    private string PresetFile => Path.Combine(_root, "presets", "tester.json");

    public DoctorFixTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "presets"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private RolePresetLoader Load() => new(NullLogger<RolePresetLoader>.Instance, _root);

    [Fact]
    public void Fix_RewritesMisspeltNames_AndASecondRunChangesNothing()
    {
        File.WriteAllText(PresetFile,
            """{"role":"tester","systemPrompt":"custom prompt","allowedTools":["ReadFile","read_file","ExecuteCommand"]}""");

        var action = Assert.Single(PresetRepair.Plan(Load()));
        Assert.Equal(PresetRepair.RepairKind.RewriteTools, action.Kind);

        PresetRepair.Apply(action);

        var after = Load();
        Assert.Empty(after.UnknownToolsFor("tester"));
        Assert.Contains("read_file", after.GetPreset("tester")!.AllowedTools);
        Assert.Contains("execute_command", after.GetPreset("tester")!.AllowedTools);
        Assert.DoesNotContain("ReadFile", after.GetPreset("tester")!.AllowedTools);
        Assert.Empty(PresetRepair.Plan(after));
    }

    [Fact]
    public void Fix_DeletesTheOldTestFixture()
    {
        File.WriteAllText(PresetFile,
            """{"role":"tester","systemPrompt":"You are the TESTER agent.","allowedTools":["ReadFile"]}""");

        var action = Assert.Single(PresetRepair.Plan(Load()));
        Assert.Equal(PresetRepair.RepairKind.DeleteFixture, action.Kind);

        PresetRepair.Apply(action);

        Assert.False(File.Exists(PresetFile));
        Assert.Empty(Load().UnknownToolsByRole);
        Assert.Empty(PresetRepair.Plan(Load()));
    }

    [Fact]
    public void Fix_LeavesNamesWithoutASuggestion_AsTheyAre()
    {
        File.WriteAllText(PresetFile,
            """{"role":"tester","systemPrompt":"custom","allowedTools":["teleport"]}""");

        Assert.Empty(PresetRepair.Plan(Load()));
        Assert.Single(Load().UnknownToolsFor("tester"));
    }
}
