using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.Core.Tests.Agent;

public class PermissionManagerTests
{
    private static PermissionManager CreateService()
    {
        var logger = Substitute.For<ILogger<PermissionManager>>();
        return new PermissionManager(logger);
    }

    private static ToolCall CreateToolCall(string name = "test_tool", string? command = null)
    {
        var args = new Dictionary<string, object?>();
        if (command != null)
            args["command"] = command;
        return new ToolCall { Name = name, Arguments = args };
    }

    private static AgentOptions Options(PermissionMode? mode = null, GranularRights? rights = null, bool isReadOnly = false) => new()
    {
        PermissionMode = mode ?? PermissionMode.Ask,
        Rights = rights,
        IsReadOnly = isReadOnly
    };

    // ---- Mode management ----

    [Fact]
    public async Task GetModeAsync_Default_IsAsk()
    {
        var svc = CreateService();
        Assert.Equal(PermissionMode.Ask, await svc.GetModeAsync());
    }

    [Fact]
    public async Task SetModeAsync_PersonalScope_SetsEffectiveMode()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto, PermissionScope.Personal);
        Assert.Equal(PermissionMode.FullAuto, await svc.GetModeAsync());
    }

    [Fact]
    public async Task SetModeAsync_ProjectScope_UsedWhenPersonalIsDefault()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.AutoEdit, PermissionScope.Project);
        Assert.Equal(PermissionMode.AutoEdit, await svc.GetModeAsync());
    }

    [Fact]
    public async Task SetModeAsync_OrganizationScope_UsedWhenProjectAndPersonalDefault()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Plan, PermissionScope.Organization);
        Assert.Equal(PermissionMode.Plan, await svc.GetModeAsync());
    }

    [Fact]
    public async Task SetModeAsync_PersonalOverridesProject()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Plan, PermissionScope.Project);
        await svc.SetModeAsync(PermissionMode.FullAuto, PermissionScope.Personal);
        Assert.Equal(PermissionMode.FullAuto, await svc.GetModeAsync());
    }

    [Fact]
    public async Task SetModeAsync_ProjectOverridesOrganization()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto, PermissionScope.Organization);
        await svc.SetModeAsync(PermissionMode.Plan, PermissionScope.Project);
        Assert.Equal(PermissionMode.Plan, await svc.GetModeAsync());
    }

    [Fact]
    public async Task SetModeAsync_AgentSpecific_DoesNotAffectGlobal()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto, PermissionScope.Personal, "impl-1");
        Assert.Equal(PermissionMode.FullAuto, await svc.GetModeAsync("impl-1"));
        Assert.Equal(PermissionMode.Ask, await svc.GetModeAsync());
    }

    [Fact]
    public async Task GetModeAsync_UnknownAgent_FallsBackToScoped()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.AutoEdit, PermissionScope.Project);
        Assert.Equal(PermissionMode.AutoEdit, await svc.GetModeAsync("unknown-agent"));
    }

    // ---- Shift+Tab cycling ----

    [Fact]
    public async Task CycleModeAsync_CyclesThroughAllModes()
    {
        var svc = CreateService();
        var modes = new List<PermissionMode>();
        for (int i = 0; i < 4; i++)
            modes.Add(await svc.CycleModeAsync());

        Assert.Equal(
            new[] { PermissionMode.AutoEdit, PermissionMode.FullAuto, PermissionMode.Plan, PermissionMode.Ask },
            modes.ToArray());
    }

    [Fact]
    public async Task CycleModeAsync_WrapsAround()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Plan);
        Assert.Equal(PermissionMode.Ask, await svc.CycleModeAsync());
    }

    [Fact]
    public async Task CycleModeAsync_AgentSpecific_OnlyAffectsAgent()
    {
        var svc = CreateService();
        await svc.CycleModeAsync("impl-1");
        Assert.Equal(PermissionMode.AutoEdit, await svc.GetModeAsync("impl-1"));
        Assert.Equal(PermissionMode.Ask, await svc.GetModeAsync());
    }

    // ---- Four modes behavior ----

    [Fact]
    public async Task CanExecute_AskMode_Read_Allows()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        var call = CreateToolCall("read_file");
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(call, RiskLevel.Read, Options()));
    }

    [Fact]
    public async Task CanExecute_AskMode_Write_Asks()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        var call = CreateToolCall("write_file");
        Assert.Equal(PermissionDecision.Ask, await svc.CanExecuteAsync(call, RiskLevel.Write, Options()));
    }

    [Fact]
    public async Task CanExecute_AskMode_Execute_Asks()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        var call = CreateToolCall("execute_command", "rm -rf /");
        Assert.Equal(PermissionDecision.Ask, await svc.CanExecuteAsync(call, RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_AutoEditMode_Write_Allows()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.AutoEdit);
        var call = CreateToolCall("write_file");
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(call, RiskLevel.Write, Options()));
    }

    [Fact]
    public async Task CanExecute_AutoEditMode_Execute_Asks()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.AutoEdit);
        var call = CreateToolCall("execute_command", "dotnet test");
        Assert.Equal(PermissionDecision.Ask, await svc.CanExecuteAsync(call, RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_FullAutoMode_SafeCommand_Allows()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto);
        var call = CreateToolCall("execute_command", "dotnet test");
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(call, RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_FullAutoMode_DangerousCommand_Denies()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto);
        var call = CreateToolCall("execute_command", "rm -rf /");
        Assert.Equal(PermissionDecision.Deny, await svc.CanExecuteAsync(call, RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_FullAutoMode_Write_Allows()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto);
        var call = CreateToolCall("write_file");
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(call, RiskLevel.Write, Options()));
    }

    [Fact]
    public async Task CanExecute_PlanMode_Read_Allows()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Plan);
        var call = CreateToolCall("read_file");
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(call, RiskLevel.Read, Options()));
    }

    [Fact]
    public async Task CanExecute_PlanMode_Write_Denies()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Plan);
        var call = CreateToolCall("write_file");
        Assert.Equal(PermissionDecision.Deny, await svc.CanExecuteAsync(call, RiskLevel.Write, Options()));
    }

    [Fact]
    public async Task CanExecute_PlanMode_Execute_Denies()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Plan);
        var call = CreateToolCall("execute_command", "dotnet test");
        Assert.Equal(PermissionDecision.Deny, await svc.CanExecuteAsync(call, RiskLevel.Execute, Options()));
    }

    // ---- Allow rules ----

    [Fact]
    public async Task CanExecute_AllowRule_ToolNameOnly_AllowsAnyCommand()
    {
        var svc = CreateService();
        await svc.AllowAsync(new PermissionRule { ToolName = "execute_command" });
        await svc.SetModeAsync(PermissionMode.Ask);
        var call = CreateToolCall("execute_command", "rm -rf /");
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(call, RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_AllowRule_PrefixPattern_AllowsMatchingCommand()
    {
        var svc = CreateService();
        await svc.AllowAsync(new PermissionRule { ToolName = "execute_command", CommandPattern = "npm test" });
        await svc.SetModeAsync(PermissionMode.Ask);
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "npm test -- --watch"), RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_AllowRule_PrefixPattern_DoesNotAllowNonMatching()
    {
        var svc = CreateService();
        await svc.AllowAsync(new PermissionRule { ToolName = "execute_command", CommandPattern = "npm test" });
        await svc.SetModeAsync(PermissionMode.Ask);
        Assert.Equal(PermissionDecision.Ask, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "npm install"), RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task CanExecute_AllowRule_OverridesPlanMode()
    {
        var svc = CreateService();
        await svc.AllowAsync(new PermissionRule { ToolName = "execute_command", CommandPattern = "git status" });
        await svc.SetModeAsync(PermissionMode.Plan);
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "git status"), RiskLevel.Execute, Options()));
    }

    [Fact]
    public async Task AllowAsync_DuplicateRule_NotDuplicated()
    {
        var svc = CreateService();
        await svc.AllowAsync(new PermissionRule { ToolName = "execute_command", CommandPattern = "npm test" });
        await svc.AllowAsync(new PermissionRule { ToolName = "execute_command", CommandPattern = "npm test" });
        // Only one rule needed; behavior should still allow.
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "npm test"), RiskLevel.Execute, Options()));
    }

    // ---- Granular rights ----

    [Fact]
    public async Task CanExecute_GranularRights_AllowEdit_OverridesAskMode()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        var rights = new GranularRights { AllowRead = true, AllowEdit = true, AllowExecute = false };
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(
            CreateToolCall("write_file"), RiskLevel.Write, Options(rights: rights)));
    }

    [Fact]
    public async Task CanExecute_GranularRights_DenyExecute_AsksEvenInFullAuto()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto);
        var rights = new GranularRights { AllowRead = true, AllowEdit = true, AllowExecute = false };
        Assert.Equal(PermissionDecision.Ask, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "dotnet test"), RiskLevel.Execute, Options(rights: rights)));
    }

    [Fact]
    public async Task CanExecute_GranularRights_AllowExecute_AllowsInAskMode()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        var rights = new GranularRights { AllowRead = true, AllowEdit = false, AllowExecute = true };
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "rm -rf /"), RiskLevel.Execute, Options(rights: rights)));
    }

    [Fact]
    public async Task CanExecute_GranularRights_DenyRead_AsksForReads()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        var rights = new GranularRights { AllowRead = false, AllowEdit = false, AllowExecute = false };
        Assert.Equal(PermissionDecision.Ask, await svc.CanExecuteAsync(
            CreateToolCall("read_file"), RiskLevel.Read, Options(rights: rights)));
    }

    // ---- Read-only session ----

    [Fact]
    public async Task CanExecute_ReadOnlySession_BlocksWrites()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto);
        Assert.Equal(PermissionDecision.Deny, await svc.CanExecuteAsync(
            CreateToolCall("write_file"), RiskLevel.Write, Options(isReadOnly: true)));
    }

    [Fact]
    public async Task CanExecute_ReadOnlySession_BlocksExecute()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.FullAuto);
        Assert.Equal(PermissionDecision.Deny, await svc.CanExecuteAsync(
            CreateToolCall("execute_command", "dotnet test"), RiskLevel.Execute, Options(isReadOnly: true)));
    }

    [Fact]
    public async Task CanExecute_ReadOnlySession_AllowsReads()
    {
        var svc = CreateService();
        await svc.SetModeAsync(PermissionMode.Ask);
        Assert.Equal(PermissionDecision.Allow, await svc.CanExecuteAsync(
            CreateToolCall("read_file"), RiskLevel.Read, Options(isReadOnly: true)));
    }

    // ---- Scoped settings loading ----

    [Fact]
    public async Task LoadScopedSettingsAsync_LoadsAllScopes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm-settings-{System.Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, @"{""permissions"":{""organization"":{""mode"":""Plan""},""project"":{""mode"":""AutoEdit""},""personal"":{""mode"":""FullAuto""}}}");
            var svc = CreateService();
            await svc.LoadScopedSettingsAsync(path);
            Assert.Equal(PermissionMode.FullAuto, await svc.GetModeAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadScopedSettingsAsync_PersonalMissing_FallsBackToProject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm-settings-{System.Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, @"{""permissions"":{""project"":{""mode"":""AutoEdit""}}}");
            var svc = CreateService();
            await svc.LoadScopedSettingsAsync(path);
            Assert.Equal(PermissionMode.AutoEdit, await svc.GetModeAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadScopedSettingsAsync_MissingFile_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.LoadScopedSettingsAsync("/nonexistent/path/settings.json");
        Assert.Equal(PermissionMode.Ask, await svc.GetModeAsync());
    }

    [Fact]
    public async Task LoadScopedSettingsAsync_MalformedJson_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm-settings-{System.Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{not valid json");
            var svc = CreateService();
            await svc.LoadScopedSettingsAsync(path);
            Assert.Equal(PermissionMode.Ask, await svc.GetModeAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}