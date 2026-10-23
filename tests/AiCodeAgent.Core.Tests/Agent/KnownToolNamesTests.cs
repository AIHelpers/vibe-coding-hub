using AiCodeAgent.Core.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

public class KnownToolNamesTests
{
    [Theory]
    [InlineData("ReadFile", "read_file")]
    [InlineData("ExecuteCommand", "execute_command")]
    [InlineData("run-diagnostics", "run_diagnostics")]
    public void Suggest_FindsTheCanonicalName(string typed, string expected)
    {
        Assert.Equal(expected, KnownToolNames.Suggest(typed));
    }

    [Fact]
    public void Suggest_ReturnsNull_ForAnUnrelatedName()
    {
        Assert.Null(KnownToolNames.Suggest("teleport"));
    }

    [Fact]
    public void Unknown_FlagsMisspellings_ButNotPatterns()
    {
        var unknown = KnownToolNames.Unknown(new[] { "read_file", "ReadFile", "mcp:github/*", "skill:*" });

        Assert.Equal("ReadFile", Assert.Single(unknown));
    }

    [Fact]
    public void BuiltInPresets_OnlyUseKnownTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "known-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loader = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance, root);

            foreach (var preset in loader.GetAllPresets())
                Assert.Empty(KnownToolNames.Unknown(preset.AllowedTools));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
