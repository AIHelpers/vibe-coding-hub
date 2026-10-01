using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiCodeAgent.App.Tests.BackgroundTasks;

public class BackgroundTaskProjectsTests
{
    /// <summary>Orchestrator stub that records the options of every run and finishes immediately.</summary>
    private sealed class RecordingOrchestrator : IAgentOrchestrator
    {
        public ConcurrentBag<AgentOptions> Runs { get; } = new();

        public Task<AgentResponse> RunAsync(string userMessage, string sessionId, AgentOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Content = string.Empty });

        public async IAsyncEnumerable<AgentEvent> StreamRunAsync(
            string userMessage, string sessionId, AgentOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Runs.Add(options);
            await Task.Yield();
            yield break;
        }
    }

    private static (BackgroundTaskManagerViewModel Vm, RecordingOrchestrator Orchestrator) CreateVm(IProjectMemoryLoader? loader = null)
    {
        var bus = new AgentEventBus();
        var orchestrator = new RecordingOrchestrator();
        var service = new AgentService(
            orchestrator,
            Substitute.For<IToolRegistry>(),
            Substitute.For<IContextManager>(),
            NullLogger<AgentService>.Instance,
            bus,
            new SessionRecorder(bus, NullLogger<SessionRecorder>.Instance),
            loader);
        return (new BackgroundTaskManagerViewModel(service, bus), orchestrator);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), "condition not met in time");
    }

    [Fact]
    public void SetProjects_SelectsMainDirectoryOnly_AndDeduplicates()
    {
        var (vm, _) = CreateVm();

        vm.SetProjects("/work/app", new[] { "/work/api", "/work/APP/", "/work/api" });

        Assert.Equal(new[] { "/work/app", "/work/api" }, vm.Projects.Select(p => p.Path));
        Assert.Equal(new[] { "/work/app" }, vm.GetSelectedProjectPaths());
        Assert.True(vm.HasMultipleProjects);
        Assert.Equal("▶ Start", vm.StartButtonText);
    }

    [Fact]
    public void TickingSeveralProjects_UpdatesCountAndButtonText()
    {
        var (vm, _) = CreateVm();
        vm.SetProjects("/work/app", new[] { "/work/api" });

        vm.Projects[1].IsSelected = true;

        Assert.Equal(2, vm.SelectedProjectCount);
        Assert.Equal("▶ Start in 2 projects", vm.StartButtonText);
        Assert.Equal(new[] { "/work/app", "/work/api" }, vm.GetSelectedProjectPaths());
    }

    [Fact]
    public void SetProjects_KeepsExistingSelection_WhenRefreshed()
    {
        var (vm, _) = CreateVm();
        vm.SetProjects("/work/app", new[] { "/work/api" });
        vm.Projects[0].IsSelected = false;
        vm.Projects[1].IsSelected = true;

        vm.SetProjects("/work/app", new[] { "/work/api", "/work/web" });

        Assert.Equal(new[] { "/work/api" }, vm.GetSelectedProjectPaths());
    }

    [Fact]
    public void GetSelectedProjectPaths_FallsBackToWorkingDirectory_WhenPickerEmpty()
    {
        var (vm, _) = CreateVm();
        vm.WorkingDirectory = "/work/app";

        Assert.Equal(new[] { "/work/app" }, vm.GetSelectedProjectPaths());
    }

    [Fact]
    public async Task StartTaskInProjects_CreatesOneConfinedTaskPerProject()
    {
        var (vm, orchestrator) = CreateVm();

        var started = vm.StartTaskInProjects("add tests", new[] { "/work/app", "/work/api", "/work/app" });

        Assert.Equal(2, started.Count);
        Assert.Equal(new[] { "/work/app", "/work/api" }, started.Select(t => t.ProjectPath));
        Assert.Equal(2, started.Select(t => t.SessionId).Distinct().Count());
        Assert.All(started, t => Assert.False(string.IsNullOrEmpty(t.BatchId)));
        Assert.Single(started.Select(t => t.BatchId).Distinct());

        await WaitForAsync(() => orchestrator.Runs.Count == 2);
        foreach (var run in orchestrator.Runs)
        {
            Assert.Equal(new[] { run.WorkingDirectory }, run.AllowedPaths);
            Assert.Equal(PermissionMode.AutoEdit, run.PermissionMode);
        }
        Assert.Equal(new[] { "/work/api", "/work/app" }, orchestrator.Runs.Select(r => r.WorkingDirectory).OrderBy(x => x));
    }

    [Fact]
    public void StartTaskInProjects_SingleProject_HasNoBatchId()
    {
        var (vm, _) = CreateVm();

        var started = vm.StartTaskInProjects("fix bug", new[] { "/work/app" });

        var task = Assert.Single(started);
        Assert.Equal(string.Empty, task.BatchId);
        Assert.Equal("app", task.ProjectName);
    }

    [Fact]
    public async Task ProjectMemory_IsCachedPerDirectory_NotSingleSlot()
    {
        var loader = Substitute.For<IProjectMemoryLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ProjectMemory?>(new ProjectMemory { FilePath = ci.Arg<string>() + "/AGENTS.md", RawContent = "x" }));
        var (vm, orchestrator) = CreateVm(loader);

        vm.StartTaskInProjects("a", new[] { "/work/app", "/work/api" });
        await WaitForAsync(() => orchestrator.Runs.Count == 2);

        // Each task got the memory of its own project.
        Assert.All(orchestrator.Runs, r => Assert.Equal(r.WorkingDirectory + "/AGENTS.md", r.ProjectMemory?.FilePath));

        // A second round reuses the cache: still only one load per directory.
        vm.StartTaskInProjects("b", new[] { "/work/app", "/work/api" });
        await WaitForAsync(() => orchestrator.Runs.Count == 4);
        await loader.Received(1).LoadAsync("/work/app", Arg.Any<CancellationToken>());
        await loader.Received(1).LoadAsync("/work/api", Arg.Any<CancellationToken>());
    }
}
