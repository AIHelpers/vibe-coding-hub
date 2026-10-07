using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.Core.Tests.Characters;

public class SkillDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-doctor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private SkillRegistry Skills() => new(NullLogger<SkillRegistry>.Instance, Path.Combine(_root, "skills"));
    private CharacterRegistry Characters() => new(NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"),
        presets: () => CharacterRegistryTests.Presets);

    [Fact]
    public async Task EmptyRegistry_WarnsNoSkills()
    {
        var checks = await SkillDoctor.DiagnoseAsync(Skills());
        var check = Assert.Single(checks);
        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.NotNull(check.FixHint);
    }

    [Fact]
    public async Task ReportsInvalidSkills_InvalidCharacters_DanglingRefs_AndLargePins()
    {
        var skills = Skills();
        await skills.CreateAsync(new SkillDraft { Name = "ok", Description = "fine" }, SkillScope.Global);
        await skills.CreateAsync(new SkillDraft { Name = "big", Description = "huge", Body = new string('x', 40_000) }, SkillScope.Global);
        Directory.CreateDirectory(Path.Combine(_root, "skills", "broken"));
        File.WriteAllText(Path.Combine(_root, "skills", "broken", "SKILL.md"), "no frontmatter");

        Directory.CreateDirectory(Path.Combine(_root, "characters"));
        File.WriteAllText(Path.Combine(_root, "characters", "alex.md"), "---\nid: alex\ndescription: d\nskills: [ok, ghost]\npinned-skills: [big]\n---\nP");
        File.WriteAllText(Path.Combine(_root, "characters", "bad.md"), "---\nid: bad\ndescription: d\npermission-mode: nope\n---\nP");

        var checks = await SkillDoctor.DiagnoseAsync(skills, Characters());

        Assert.Contains(checks, c => c.Name == "Skill 'broken'" && c.Status == DoctorStatus.Error);
        Assert.Contains(checks, c => c.Name == "Skill 'big'" && c.Status == DoctorStatus.Warning);
        Assert.Contains(checks, c => c.Name == "Character 'bad'" && c.Status == DoctorStatus.Error);
        Assert.Contains(checks, c => c.Name == "Character 'alex'" && c.Detail.Contains("ghost"));
        Assert.Contains(checks, c => c.Name == "Character 'alex'" && c.Detail.Contains("Pins large skill 'big'"));
        Assert.Contains(checks, c => c.Name == "Characters" && c.Detail.StartsWith("2 custom"));
        Assert.DoesNotContain(checks, c => c.Name == "Skill 'ok'");
    }

    [Fact]
    public void RunReport_ListsSkillsLoaded()
    {
        var report = new RunReportBuilder("s", "task");
        report.Add(End("use_skill", new() { ["name"] = "release-notes" }, error: false));
        report.Add(End("use_skill", new() { ["name"] = "release-notes" }, error: false));
        report.Add(End("use_skill", new() { ["name"] = "ghost" }, error: true));
        report.Add(End("read_file", new() { ["path"] = "a" }, error: false));

        Assert.Equal(new[] { "release-notes" }, report.SkillsLoaded());
        Assert.Contains("## Skills loaded (1)", report.Build());
    }

    [Fact]
    public void RunReport_NoSkills_OmitsSection()
    {
        var report = new RunReportBuilder("s", "task");
        report.Add(End("read_file", new() { ["path"] = "a" }, error: false));
        Assert.DoesNotContain("Skills loaded", report.Build());
    }

    private static ToolCallEndEvent End(string tool, Dictionary<string, object?> args, bool error) =>
        new(new ToolCall { Id = "1", Name = tool, Arguments = args },
            new ToolResult { ToolName = tool, Content = "x", IsError = error },
            TimeSpan.FromMilliseconds(5));
}
