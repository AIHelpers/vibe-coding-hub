using System;
using System.Linq;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Flows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeAgent.App.ViewModels;

/// <summary>Mission Control (E11-S1): the task board, shown over the main area like the Flows editor.</summary>
public partial class MainViewModel
{
    /// <summary>The board: Backlog, Running, Needs review and Done; one card per task, each in its own worktree.</summary>
    public MissionControlViewModel MissionControl { get; private set; } = null!;

    private void InitializeMissionControl()
    {
        var agent = _serviceProvider.GetService<AgentService>();
        var defaultRunner = agent == null ? null : new AgentServiceBoardRunner(agent);
        var (targets, catalog) = CreateBoardAgents(defaultRunner);
        MissionControl = new MissionControlViewModel(
            _serviceProvider.GetService<IWorkspaceIsolation>(), defaultRunner,
            marshal: RunOnUi, targets: targets, catalog: catalog);
        MissionControl.SetProject(WorkingDirectory);
        _ = MissionControl.RefreshAgentsAsync();
    }

    /// <summary>
    /// What a card can be given besides the default agent (E11-S2): the characters of the character registry, the flows of the
    /// pipeline loader and the coding agents found on this computer, each with the runner that drives it. A kind whose services
    /// are not registered is left out, so the picker never offers what cannot run.
    /// </summary>
    private (IBoardTargetRunner? Targets, BoardAgentCatalog Catalog) CreateBoardAgents(IBoardAgentRunner? defaultRunner)
    {
        var characters = _serviceProvider.GetService<ICharacterRegistry>();
        var loader = _serviceProvider.GetService<SdlcPipelineLoader>();
        var pipelines = _serviceProvider.GetService<SdlcPipelineRunner>();
        var externalAgents = _serviceProvider.GetService<IExternalAgentRunner>();
        var presets = _serviceProvider.GetService<RolePresetLoader>();
        var detector = new ExternalAgentDetector();

        var runners = new System.Collections.Generic.List<IBoardTargetRunner>();
        ExternalBoardRunner? external = externalAgents == null ? null : new ExternalBoardRunner(externalAgents, detector);
        if (external != null) runners.Add(external);
        if (characters != null && defaultRunner != null)
        {
            runners.Add(new CharacterBoardRunner(
                characters, defaultRunner, external,
                presetOf: role => role == null ? null : presets?.GetPreset(role),
                externalTargetOf: id => pipelines?.ResolveExternalTarget(new SdlcStageDefinition { Character = id }, null)));
        }
        if (loader != null && pipelines != null)
        {
            var headless = new FlowHeadlessRunner(pipelines);
            runners.Add(new FlowBoardRunner(
                name => loader.GetPipeline(name),
                (pipeline, task, directory, options, token) => headless.RunAsync(pipeline, task, directory, options, token),
                characters));
        }

        var catalog = new BoardAgentCatalog(
            characters,
            loader == null ? null : () => loader.GetAllPipelines().Where(p => p.IsFlow).Select(p => (p.Name, p.Description)),
            external == null ? null : detector);
        return (runners.Count == 0 ? null : new BoardTargetRouter(runners.ToArray()), catalog);
    }

    /// <summary>Opens or closes Mission Control.</summary>
    [RelayCommand]
    private void ToggleMissionControl()
    {
        if (MissionControl.IsVisible)
        {
            MissionControl.IsVisible = false;
            return;
        }

        CloseOverlays();
        IsSettingsMode = false;
        MissionControl.SetProject(WorkingDirectory);
        MissionControl.IsVisible = true;
        _ = MissionControl.RefreshAgentsAsync();
    }
}
