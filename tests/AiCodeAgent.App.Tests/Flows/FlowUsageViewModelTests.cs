using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S4 in the editor: the node badge, the run total, the usage table and the budget field.</summary>
public class FlowUsageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-usage-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private async Task<FlowEditorViewModel> OpenAsync()
    {
        Directory.CreateDirectory(_root);
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters);
        await vm.OpenAsync(_root);
        vm.NewFlow();
        return vm;
    }

    private static FlowStepRunUsage Run(string node, int round, decimal cost, string? model = "claude-sonnet-x") =>
        new(node, round, model, null, 1000, 200, 0, 0, cost, false, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task UsageEvent_FillsTheNodeBadge_AndTheRunHeader()
    {
        var vm = await OpenAsync();
        var a = vm.AddNode("planner", 40, 40);
        var first = Run(a.Id, 1, 0.004m);

        vm.ApplyRunEvent(new FlowUsageEvent(a.Id, first, FlowUsageTotals.Sum(new[] { first }), FlowUsageTotals.Sum(new[] { first }), 1m));

        Assert.Equal("1.2k tok · $0.004", a.UsageText);
        Assert.True(a.HasUsage);
        Assert.Equal("Run: 1.2k tok · $0.004 of $1.00", vm.RunUsageText);
        Assert.True(vm.HasRunUsage);
    }

    [Fact]
    public async Task FinishedFlow_ShowsOneRowPerStepRun_AndATotal()
    {
        var vm = await OpenAsync();
        var a = vm.AddNode("planner", 40, 40);
        var b = vm.AddNode("implementer", 300, 40);
        var rows = new[] { Run(a.Id, 1, 0.004m), Run(b.Id, 1, 0.01m, model: null), Run(b.Id, 2, 0.01m) };
        var result = new FlowRunResult(FlowRunStatus.BudgetExceeded, "Budget exceeded: stopped.", Array.Empty<FlowNodeRunSummary>())
        {
            StepRuns = rows,
            BudgetUsd = 0.02m
        };

        vm.ApplyRunEvent(new FlowFinishedEvent(result));

        Assert.True(vm.HasUsageRows);
        Assert.Equal(4, vm.UsageRows.Count);
        Assert.Equal(new[] { a.Id, b.Id, b.Id, "total" }, vm.UsageRows.Select(r => r.Step).ToArray());
        Assert.Equal("(default)", vm.UsageRows[1].Model);
        Assert.Equal("2", vm.UsageRows[2].Round);
        Assert.True(vm.UsageRows[3].IsTotal);
        Assert.Equal("$0.024", vm.UsageRows[3].Cost);
        Assert.StartsWith("Flow budget_exceeded:", vm.StatusText);
    }

    [Fact]
    public async Task ResetRunState_ClearsBadgesHeaderAndTable()
    {
        var vm = await OpenAsync();
        var a = vm.AddNode("planner", 40, 40);
        var u = Run(a.Id, 1, 0.004m);
        vm.ApplyRunEvent(new FlowUsageEvent(a.Id, u, FlowUsageTotals.Sum(new[] { u }), FlowUsageTotals.Sum(new[] { u }), null));
        vm.ApplyRunEvent(new FlowFinishedEvent(new FlowRunResult(FlowRunStatus.Succeeded, "ok", Array.Empty<FlowNodeRunSummary>()) { StepRuns = new[] { u } }));

        vm.ResetRunState();

        Assert.Equal(string.Empty, a.UsageText);
        Assert.Equal(string.Empty, vm.RunUsageText);
        Assert.Empty(vm.UsageRows);
        Assert.False(vm.HasUsageRows);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("$2", 2)]
    [InlineData("0,25", 0.25)]
    [InlineData(" 3 ", 3)]
    public async Task BudgetText_ParsesToTheCap(string text, double expected)
    {
        var vm = await OpenAsync();
        vm.BudgetText = text;
        Assert.Equal((decimal)expected, vm.BudgetUsd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task BudgetText_WithoutAPositiveNumber_MeansNoCap(string text)
    {
        var vm = await OpenAsync();
        vm.BudgetText = text;
        Assert.Null(vm.BudgetUsd);
        Assert.Null(vm.ToDefinition().MaxBudgetUsd);
    }

    [Fact]
    public async Task Budget_RoundTripsThroughTheDefinition_AndMarksTheFlowDirty()
    {
        var vm = await OpenAsync();
        vm.AddNode("planner", 40, 40);
        vm.IsDirty = false;

        vm.BudgetText = "0.5";

        Assert.True(vm.IsDirty);
        var def = vm.ToDefinition();
        Assert.Equal(0.5m, def.MaxBudgetUsd);

        vm.BudgetText = string.Empty;
        vm.LoadDefinition(def);
        Assert.Equal("0.5", vm.BudgetText);
    }
}
