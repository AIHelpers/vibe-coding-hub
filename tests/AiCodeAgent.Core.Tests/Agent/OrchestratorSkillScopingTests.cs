using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

/// <summary>
/// Verifies that an agent only sees (and can only be offered the tool to load)
/// the skills assigned to it, and that pinned skills are injected in full.
/// </summary>
public class OrchestratorSkillScopingTests : IDisposable
{
    private readonly string _root;
    private readonly SkillRegistry _skills;

    public OrchestratorSkillScopingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orch-skills-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_root, "skills");
        Write(dir, "alpha", "---\nname: alpha\ndescription: Alpha skill\n---\nALPHA-BODY");
        Write(dir, "beta", "---\nname: beta\ndescription: Beta skill\n---\nBETA-BODY");
        Write(dir, "conventions", "---\nname: conventions\ndescription: Team conventions\n---\nCONVENTIONS-BODY");
        _skills = new SkillRegistry(NullLogger<SkillRegistry>.Instance, globalSkillsDir: dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static void Write(string dir, string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(dir, name));
        File.WriteAllText(Path.Combine(dir, name, "SKILL.md"), content);
    }

    private sealed class NamedTool : ITool
    {
        public NamedTool(string name, RiskLevel risk = RiskLevel.Read) { Name = name; Risk = risk; }
        public string Name { get; }
        public string Description => Name;
        public RiskLevel Risk { get; }
        public ToolDefinition Definition => new() { Name = Name, Description = Name, Parameters = new JsonSchema() };
        public Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context) =>
            Task.FromResult(new ToolResult { ToolName = Name, Content = "ok" });
    }

    private static async IAsyncEnumerable<StreamChunk> Done()
    {
        await Task.Yield();
        yield return new StreamChunk { Delta = "done", IsFinished = true };
    }

    /// <summary>Runs one turn and returns the request sent to the provider.</summary>
    private async Task<CompletionRequest> RunAndCaptureAsync(AgentOptions options, bool registerSkillTool = true)
    {
        CompletionRequest? captured = null;
        var provider = Substitute.For<IAiProvider>();
        provider.StreamAsync(Arg.Do<CompletionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(_ => Done());
        var context = Substitute.For<IContextManager>();
        context.GetContextAsync(Arg.Any<string>()).Returns(new List<Message>());

        var tools = new ToolRegistry();
        tools.Register(new NamedTool("read_file"));
        tools.Register(new NamedTool("write_file", RiskLevel.Write));
        if (registerSkillTool) tools.Register(new NamedTool(AgentOrchestrator.UseSkillToolName));

        var orchestrator = new AgentOrchestrator(
            provider, context, tools, new AgentConfiguration(),
            Substitute.For<ILogger<AgentOrchestrator>>(),
            Substitute.For<IPermissionService>(),
            Substitute.For<ICheckpointManager>(),
            skillRegistry: _skills);

        await orchestrator.RunAsync("hi", "s1", options);
        return captured ?? throw new InvalidOperationException("Provider was not called");
    }

    private static string Section(string prompt, string tag)
    {
        var start = prompt.IndexOf($"<{tag}>", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = prompt.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return prompt[start..end];
    }

    [Fact]
    public async Task NoRestriction_ListsAllVisibleSkills_AndOffersUseSkill()
    {
        var request = await RunAndCaptureAsync(new AgentOptions());

        var skills = Section(request.SystemPrompt!, "available_skills");
        Assert.Contains("alpha", skills);
        Assert.Contains("beta", skills);
        Assert.Contains("use_skill", skills);
        Assert.Contains(request.Tools, t => t.Name == "use_skill");
    }

    [Fact]
    public async Task AllowedSkills_NarrowsTheList()
    {
        var request = await RunAndCaptureAsync(new AgentOptions { AllowedSkills = new() { "beta" } });

        var skills = Section(request.SystemPrompt!, "available_skills");
        Assert.Contains("beta", skills);
        Assert.DoesNotContain("alpha", skills);
    }

    [Fact]
    public async Task EmptyAllowedSkills_RemovesSectionAndTool()
    {
        var request = await RunAndCaptureAsync(new AgentOptions { AllowedSkills = new() });

        Assert.Equal(string.Empty, Section(request.SystemPrompt!, "available_skills"));
        Assert.DoesNotContain(request.Tools, t => t.Name == "use_skill");
    }

    [Fact]
    public async Task ToolWhitelist_StillGetsUseSkill_WhenAgentHasSkills()
    {
        var request = await RunAndCaptureAsync(new AgentOptions
        {
            EnabledTools = new() { "read_file" },
            AllowedSkills = new() { "alpha" }
        });

        Assert.Contains(request.Tools, t => t.Name == "use_skill");
        Assert.DoesNotContain(request.Tools, t => t.Name == "write_file");
    }

    [Fact]
    public async Task DisabledTools_BeatsAutomaticUseSkill()
    {
        var request = await RunAndCaptureAsync(new AgentOptions
        {
            DisabledTools = new() { "use_skill" },
            AllowedSkills = new() { "alpha" }
        });

        Assert.DoesNotContain(request.Tools, t => t.Name == "use_skill");
    }

    [Fact]
    public async Task PinnedSkills_AreInjectedInFull_AndNotListedTwice()
    {
        var request = await RunAndCaptureAsync(new AgentOptions
        {
            AllowedSkills = new() { "alpha" },
            PinnedSkills = new() { "conventions" }
        });

        var pinned = Section(request.SystemPrompt!, "pinned_skills");
        Assert.Contains("CONVENTIONS-BODY", pinned);
        var listed = Section(request.SystemPrompt!, "available_skills");
        Assert.Contains("alpha", listed);
        Assert.DoesNotContain("conventions", listed);
        Assert.DoesNotContain("ALPHA-BODY", request.SystemPrompt!);
    }

    [Fact]
    public async Task UnknownPinnedSkill_IsIgnored()
    {
        var request = await RunAndCaptureAsync(new AgentOptions { PinnedSkills = new() { "ghost" } });
        Assert.Equal(string.Empty, Section(request.SystemPrompt!, "pinned_skills"));
    }

    [Fact]
    public async Task WithoutSkillTool_NothingBreaks()
    {
        var request = await RunAndCaptureAsync(new AgentOptions(), registerSkillTool: false);
        Assert.DoesNotContain(request.Tools, t => t.Name == "use_skill");
        Assert.Contains("alpha", Section(request.SystemPrompt!, "available_skills"));
    }
}
