using System.Collections.Generic;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class PermissionServiceTests
{
    private static PermissionService CreateService()
    {
        var logger = Substitute.For<ILogger<PermissionService>>();
        return new PermissionService(logger);
    }

    private static ToolCall CreateToolCall(string name = "test_tool", string? command = null)
    {
        var args = new Dictionary<string, object?>();
        if (command != null)
            args["command"] = command;
        return new ToolCall { Name = name, Arguments = args };
    }

    [Fact]
    public void SetMode_WithAgentId_SetsPerAgentMode()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.Plan, "planner-1");

        Assert.Equal(PermissionMode.Plan, service.GetMode("planner-1"));
        Assert.Equal(PermissionMode.Ask, service.GetMode()); // global unchanged
    }

    [Fact]
    public void SetMode_WithoutAgentId_SetsGlobalMode()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.FullAuto);

        Assert.Equal(PermissionMode.FullAuto, service.GetMode());
        Assert.Equal(PermissionMode.FullAuto, service.GetMode("any-agent")); // falls back to global
    }

    [Fact]
    public void GetMode_UnknownAgent_FallsBackToGlobal()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.AutoEdit);

        Assert.Equal(PermissionMode.AutoEdit, service.GetMode("unknown-agent"));
    }

    [Fact]
    public async Task RequestApprovalAsync_PlanMode_AgentSpecific_BlocksWrites()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.Plan, "planner-1");

        var readCall = CreateToolCall("read_file");
        var writeCall = CreateToolCall("write_file");

        var options = new AgentOptions();

        Assert.True(await service.RequestApprovalAsync(readCall, RiskLevel.Read, options, "planner-1"));
        Assert.False(await service.RequestApprovalAsync(writeCall, RiskLevel.Write, options, "planner-1"));
    }

    [Fact]
    public async Task RequestApprovalAsync_PlanMode_Global_DoesNotAffectOtherAgents()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.Plan); // global plan mode

        var writeCall = CreateToolCall("write_file");
        var options = new AgentOptions();

        // Global mode is Plan, so writes blocked
        Assert.False(await service.RequestApprovalAsync(writeCall, RiskLevel.Write, options));

        // But an agent with FullAuto mode is not affected
        service.SetMode(PermissionMode.FullAuto, "implementer-1");
        Assert.True(await service.RequestApprovalAsync(writeCall, RiskLevel.Write, options, "implementer-1"));
    }

    [Fact]
    public async Task RequestApprovalAsync_AutoEditMode_AgentSpecific_ApprovesWrites()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.AutoEdit, "implementer-1");

        var writeCall = CreateToolCall("write_file");
        var options = new AgentOptions();

        Assert.True(await service.RequestApprovalAsync(writeCall, RiskLevel.Write, options, "implementer-1"));
    }

    [Fact]
    public async Task RequestApprovalAsync_AutoEditMode_AgentSpecific_AsksForExecute()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.AutoEdit, "implementer-1");

        var execCall = CreateToolCall("execute_command", "dotnet test");
        var options = new AgentOptions();

        Assert.False(await service.RequestApprovalAsync(execCall, RiskLevel.Execute, options, "implementer-1"));
    }

    [Fact]
    public async Task RequestApprovalAsync_FullAutoMode_AgentSpecific_ApprovesEverything()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.FullAuto, "implementer-1");

        var execCall = CreateToolCall("execute_command", "rm -rf /");
        var options = new AgentOptions();

        Assert.True(await service.RequestApprovalAsync(execCall, RiskLevel.Execute, options, "implementer-1"));
    }

    [Fact]
    public async Task RequestApprovalAsync_ReadOperations_AlwaysAllowed()
    {
        var service = CreateService();
        service.SetMode(PermissionMode.Ask, "reviewer-1");

        var readCall = CreateToolCall("read_file");
        var options = new AgentOptions();

        Assert.True(await service.RequestApprovalAsync(readCall, RiskLevel.Read, options, "reviewer-1"));
    }
}