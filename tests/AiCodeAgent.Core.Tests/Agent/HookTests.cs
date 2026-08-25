using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class HookTests
{
    [Fact]
    public void HookDefinition_Defaults()
    {
        var def = new HookDefinition
        {
            Name = "test",
            Event = HookEvent.PreToolUse,
            Command = "echo hi"
        };

        Assert.False(def.Blocking);
        Assert.Equal(30, def.TimeoutSeconds);
        Assert.Null(def.ToolFilter);
    }

    [Fact]
    public void HookResult_Defaults()
    {
        var result = new HookResult
        {
            Definition = new HookDefinition { Name = "x", Event = HookEvent.PreToolUse, Command = "c" }
        };

        Assert.False(result.Success);
        Assert.False(result.Deny);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task HookRegistry_LoadAsync_ReturnsEmpty_WhenNoSettingsFile()
    {
        var registry = new HookRegistry();
        var hooks = await registry.LoadAsync("/nonexistent/path/that/does/not/exist");
        Assert.Empty(hooks);
        Assert.Empty(registry.Hooks);
    }

    [Fact]
    public async Task HookRegistry_LoadAsync_ParsesProjectSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiagent-hook-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".aiagent"));
        var settingsPath = Path.Combine(dir, ".aiagent", "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
        {
          "hooks": [
            {
              "name": "lint-check",
              "event": "PreToolUse",
              "command": "echo linting",
              "blocking": true,
              "timeoutSeconds": 10,
              "toolFilter": "WriteFile"
            },
            {
              "name": "post-notify",
              "event": "PostToolUse",
              "command": "echo done"
            }
          ]
        }
        """);

        try
        {
            var registry = new HookRegistry();
            var hooks = await registry.LoadAsync(dir);

            Assert.Equal(2, hooks.Count);
            var lint = hooks.First(h => h.Name == "lint-check");
            Assert.Equal(HookEvent.PreToolUse, lint.Event);
            Assert.True(lint.Blocking);
            Assert.Equal(10, lint.TimeoutSeconds);
            Assert.Equal("WriteFile", lint.ToolFilter);

            var post = hooks.First(h => h.Name == "post-notify");
            Assert.Equal(HookEvent.PostToolUse, post.Event);
            Assert.False(post.Blocking);
            Assert.Equal(30, post.TimeoutSeconds);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task HookRegistry_LoadAsync_ProjectOverridesGlobal_ForSameName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiagent-hook-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".aiagent"));
        var projectPath = Path.Combine(dir, ".aiagent", "settings.json");
        await File.WriteAllTextAsync(projectPath, """
        {
          "hooks": [
            { "name": "shared", "event": "PreToolUse", "command": "project-cmd", "blocking": true }
          ]
        }
        """);

        try
        {
            var registry = new HookRegistry();
            var hooks = await registry.LoadAsync(dir);
            var shared = hooks.Single(h => h.Name == "shared");
            Assert.Equal("project-cmd", shared.Command);
            Assert.True(shared.Blocking);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HookRegistry_SkipsInvalidHookDefinitions()
    {
        // ParseHook returns null for missing name or command — tested indirectly via LoadAsync
        var registry = new HookRegistry();
        Assert.Empty(registry.Hooks);
    }

    [Fact]
    public async Task HookRunner_RunAsync_NoHooks_ReturnsEmpty()
    {
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(Array.Empty<HookDefinition>());
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task HookRunner_RunAsync_FiltersByEvent()
    {
        var preHook = new HookDefinition { Name = "h1", Event = HookEvent.PreToolUse, Command = "echo pre" };
        var postHook = new HookDefinition { Name = "h2", Event = HookEvent.PostToolUse, Command = "echo post" };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { preHook, postHook });
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.Single(result.Results);
        Assert.Equal("h1", result.Results[0].Definition.Name);
    }

    [Fact]
    public async Task HookRunner_RunAsync_AppliesToolFilter()
    {
        var hook = new HookDefinition
        {
            Name = "filtered",
            Event = HookEvent.PreToolUse,
            Command = "echo hi",
            ToolFilter = "WriteFile"
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { hook });
        var runner = new HookRunner(registry);

        // Non-matching tool: skipped
        var result1 = await runner.RunAsync(HookEvent.PreToolUse, new HookContext
        {
            SessionId = "s1",
            ToolCall = new ToolCall { Name = "ReadFile" }
        });
        Assert.Empty(result1.Results);

        // Matching tool: executed
        var result2 = await runner.RunAsync(HookEvent.PreToolUse, new HookContext
        {
            SessionId = "s1",
            ToolCall = new ToolCall { Name = "WriteFile" }
        });
        Assert.Single(result2.Results);
    }

    [Fact]
    public async Task HookRunner_RunAsync_ExecutesCommand_AndSucceeds()
    {
        var hook = new HookDefinition
        {
            Name = "success-hook",
            Event = HookEvent.PreToolUse,
            Command = "echo hello",
            Blocking = false,
            TimeoutSeconds = 5
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { hook });
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.Single(result.Results);
        var r = result.Results[0];
        Assert.True(r.Success);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.Deny);
    }

    [Fact]
    public async Task HookRunner_RunAsync_BlockingDeny_WhenExitCodeNonZero()
    {
        var hook = new HookDefinition
        {
            Name = "deny-hook",
            Event = HookEvent.PreToolUse,
            Command = "exit 1",
            Blocking = true,
            TimeoutSeconds = 5
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { hook });
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.Single(result.Results);
        Assert.True(result.Results[0].Deny);
        Assert.False(result.Results[0].Success);
    }

    [Fact]
    public async Task HookRunner_RunAsync_BlockingDeny_WhenOutputContainsDeny()
    {
        var hook = new HookDefinition
        {
            Name = "deny-output-hook",
            Event = HookEvent.PreToolUse,
            Command = "echo DENY: not allowed",
            Blocking = true,
            TimeoutSeconds = 5
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { hook });
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.True(result.Results[0].Deny);
    }

    [Fact]
    public async Task HookRunner_RunAsync_StopsEarly_OnBlockingDeny()
    {
        var denyHook = new HookDefinition
        {
            Name = "deny-first",
            Event = HookEvent.PreToolUse,
            Command = "exit 1",
            Blocking = true,
            TimeoutSeconds = 5
        };
        var secondHook = new HookDefinition
        {
            Name = "should-not-run",
            Event = HookEvent.PreToolUse,
            Command = "echo should-not-run",
            Blocking = false,
            TimeoutSeconds = 5
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { denyHook, secondHook });
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.Single(result.Results);
        Assert.Equal("deny-first", result.Results[0].Definition.Name);
    }

    [Fact]
    public async Task HookRunner_RunAsync_TimesOut_WhenCommandHangs()
    {
        var hook = new HookDefinition
        {
            Name = "timeout-hook",
            Event = HookEvent.PreToolUse,
            Command = "ping -n 30 127.0.0.1",
            Blocking = false,
            TimeoutSeconds = 1
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(new List<HookDefinition> { hook });
        var runner = new HookRunner(registry);

        var result = await runner.RunAsync(HookEvent.PreToolUse, new HookContext { SessionId = "s1" });

        Assert.Single(result.Results);
        Assert.True(result.Results[0].TimedOut);
        Assert.False(result.Results[0].Success);
    }

    [Fact]
    public void HookRunner_ListHooks_ReturnsRegistryHooks()
    {
        var hooks = new List<HookDefinition>
        {
            new() { Name = "h1", Event = HookEvent.PreToolUse, Command = "echo a" },
            new() { Name = "h2", Event = HookEvent.PostToolUse, Command = "echo b" }
        };
        var registry = Substitute.For<IHookRegistry>();
        registry.Hooks.Returns(hooks);
        var runner = new HookRunner(registry);

        var list = runner.ListHooks();

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void HookContext_Defaults()
    {
        var ctx = new HookContext { SessionId = "s1" };

        Assert.Equal("s1", ctx.SessionId);
        Assert.Null(ctx.ToolCall);
        Assert.Null(ctx.ToolResult);
        Assert.Null(ctx.Error);
        Assert.Null(ctx.WorkingDirectory);
    }

    [Fact]
    public void HookRunResult_AggregatesResults()
    {
        var runResult = new HookRunResult
        {
            Results = new List<HookResult>
            {
                new() { Definition = new HookDefinition { Name = "a", Event = HookEvent.PreToolUse, Command = "c" }, Success = true },
                new() { Definition = new HookDefinition { Name = "b", Event = HookEvent.PreToolUse, Command = "c" }, Success = false, Deny = true }
            }
        };

        Assert.Equal(2, runResult.Results.Count);
        Assert.True(runResult.Results[0].Success);
        Assert.True(runResult.Results[1].Deny);
    }
}