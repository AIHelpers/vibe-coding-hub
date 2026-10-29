using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>E5-S8 in the editor: Export ▾ — report, Mermaid and the area a PNG covers.</summary>
public class FlowExportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-export-vm-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 18, 2, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static SdlcStageDefinition Stage(string id, string role) => new() { Id = id, Role = role };

    private static SdlcPipelineDefinition Definition() => new()
    {
        Name = "diamond",
        Stages = new() { Stage("design", "planner"), Stage("build", "implementer"), Stage("review", "reviewer") },
        Edges = new()
        {
            new FlowEdge { From = "design", To = "build" },
            new FlowEdge { From = "build", To = "review" },
            new FlowEdge { From = "review", To = "build", When = "rejected", MaxLoops = 2 }
        }
    };

    private FlowRunStore SaveRun()
    {
        var store = new FlowRunStore(Path.Combine(_root, "runs"));
        store.Save(new FlowRunRecord
        {
            Id = FlowRunStore.NewRunId(T0),
            FlowName = "diamond",
            Task = "ship it",
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(42),
            Status = FlowRunStatus.Succeeded,
            Message = "done",
            Definition = Definition(),
            Steps = new List<FlowStepRecord>
            {
                new() { NodeId = "design", State = FlowNodeState.Done, OutputFile = "design.md" },
                new() { NodeId = "build", State = FlowNodeState.Done, OutputFile = "build.md" },
                new() { NodeId = "review", State = FlowNodeState.Done, OutputFile = "review.md" }
            }
        }, new Dictionary<string, string> { ["design.md"] = "the design", ["build.md"] = "the build", ["review.md"] = "the review" });
        return store;
    }

    private async Task<FlowEditorViewModel> OpenAsync(FlowRunStore? store)
    {
        Directory.CreateDirectory(_root);
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters, null, null, store);
        await vm.OpenAsync(_root);
        vm.NewFlow();
        vm.FlowName = "diamond";
        vm.RefreshHistory();
        return vm;
    }

    [Fact]
    public async Task Report_WithoutARun_SaysSoInsteadOfCopyingNothing()
    {
        var vm = await OpenAsync(new FlowRunStore(Path.Combine(_root, "runs")));

        Assert.Null(vm.BuildMarkdownReport());
        Assert.Contains("No run to report on", vm.StatusText);
    }

    [Fact]
    public async Task Report_IsTheNewestRunOfTheFlow()
    {
        var vm = await OpenAsync(SaveRun());

        var report = vm.BuildMarkdownReport();

        Assert.NotNull(report);
        Assert.StartsWith("# Flow run: diamond", report);
        Assert.Contains("ship it", report);
        Assert.Contains("the review", report);
        Assert.Contains("```mermaid", report);
    }

    [Fact]
    public async Task Mermaid_InReplay_ShowsTheRunStatus_AndOtherwiseTheCanvas()
    {
        var vm = await OpenAsync(SaveRun());
        vm.AddNode("planner", 40, 40);
        Assert.DoesNotContain("classDef", vm.BuildMermaid());

        vm.OpenRun(vm.History[0].RunId);

        Assert.Contains("class n_design,n_build,n_review done", vm.BuildMermaid());
        Assert.Contains("n_review -.->|rejected ↺ 0/2| n_build", vm.BuildMermaid());
    }

    [Fact]
    public async Task PngBounds_HoldEveryCard_EvenFarFromTheOrigin()
    {
        var vm = await OpenAsync(null);
        vm.AddNode("planner", 60, 80);
        vm.AddNode("planner", 3000, 2100);

        var b = vm.ExportBounds();

        foreach (var n in vm.Nodes)
        {
            Assert.True(n.X >= b.X + FlowEditorViewModel.ExportPadding - 0.001, $"{n.Id} clipped on the left");
            Assert.True(n.Y >= b.Y + FlowEditorViewModel.ExportPadding - 0.001, $"{n.Id} clipped at the top");
            Assert.True(n.X + FlowNodeViewModel.Width <= b.X + b.Width - FlowEditorViewModel.ExportPadding + 0.001, $"{n.Id} clipped on the right");
            Assert.True(n.Y + FlowNodeViewModel.Height <= b.Y + b.Height - FlowEditorViewModel.ExportPadding + 0.001, $"{n.Id} clipped at the bottom");
        }
    }

    [Fact]
    public async Task PngBounds_IncludeALoopArcAboveTheCards()
    {
        var vm = await OpenAsync(SaveRun());
        vm.OpenRun(vm.History[0].RunId);
        var highest = vm.Nodes.Min(n => n.Y);

        var b = vm.ExportBounds();

        // The loop review → build arcs 70 units above the higher of the two cards.
        Assert.True(b.Y <= Math.Max(0, highest - 70 - FlowEditorViewModel.ExportPadding) + 0.001 || b.Y == 0);
        Assert.True(b.Height > FlowNodeViewModel.Height);
    }

    [Fact]
    public void PngSize_IsTheBoundsTimesTheScale()
    {
        var size = FlowCanvasImage.PixelSizeOf(new FlowExportBounds(10, 20, 400.2, 300));

        Assert.Equal((int)Math.Ceiling(400.2 * FlowCanvasImage.Scale), size.Width);
        Assert.Equal((int)(300 * FlowCanvasImage.Scale), size.Height);
    }

    [Fact]
    public async Task AnEmptyFlow_ExportsASmallBlankBox()
    {
        var vm = await OpenAsync(null);
        var b = vm.ExportBounds();

        Assert.True(b.Width > 0 && b.Height > 0);
    }
}
