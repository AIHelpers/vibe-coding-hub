using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Screenshots;

/// <summary>
/// The drivers: how the app gets into the state each screenshot shows. They only use what a user can do (activity bar
/// entries, buttons, typing a task), so a change to the UI that breaks the story breaks the scene, loudly.
/// </summary>
internal static class Scenes
{
    /// <summary>
    /// Execution order. It differs from the file numbers because some images (Screens mode, 04) look better after the
    /// agent run, and the flow scenes must follow each other while the flow is still moving.
    /// </summary>
    private static readonly int[] RunOrder =
    {
        1, 2, 3, 5, 6, 4, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22
    };

    public static IReadOnlyList<(SceneInfo Info, Func<SceneContext, Task> Run)> Plan(IReadOnlyList<SceneInfo> selected)
    {
        var drivers = Drivers();
        return RunOrder
            .Select(number => selected.FirstOrDefault(s => s.Number == number))
            .Where(info => info != null)
            .Select(info => (info!, drivers[info!.Number]))
            .ToList();
    }

    private static Dictionary<int, Func<SceneContext, Task>> Drivers() => new()
    {
        [1] = MainWindowScene,
        [2] = SampleTaskScene,
        [3] = ExplorerScene,
        [4] = ScreensModeScene,
        [5] = ApprovalScene,
        [6] = ResultScene,
        [7] = DiffScene,
        [8] = CommitScene,
        [9] = ProjectKnowledgeScene,
        [10] = SkillsScene,
        [11] = CharacterScene,
        [12] = AddSkillScene,
        [13] = FlowCanvasScene,
        [14] = InspectorScene,
        [15] = FlowRunningScene,
        [16] = FlowLoopScene,
        [17] = FlowCompleteScene,
        [18] = SettingsScene,
        [19] = PaletteScene,
        [20] = CheckpointsScene,
        [21] = BackgroundTasksScene,
        [22] = LightThemeScene,
    };

    private static SceneInfo Info(int number) => SceneCatalog.All.First(s => s.Number == number);

    // ------------------------------------------------------------------ chat

    private static async Task MainWindowScene(SceneContext c)
    {
        await c.ShowChatAsync();
        await c.CaptureAsync(Info(1));
    }

    private static async Task SampleTaskScene(SceneContext c)
    {
        await c.ShowChatAsync();
        c.Chat.InputText = SampleProjectInstaller.FirstTaskPrompt;
        await c.CaptureAsync(Info(2));
        c.Chat.InputText = string.Empty;
    }

    private static async Task ExplorerScene(SceneContext c)
    {
        await c.ShowChatAsync();
        if (!c.Main.IsFileExplorerOpen)
            await c.ExecuteAsync(c.Main.ToggleFileExplorerCommand);
        await c.WaitUntilAsync(() => c.Main.IsFileExplorerOpen, "the file explorer");
        await c.SettleAsync(600);
        await c.CaptureAsync(Info(3));
        if (c.Main.IsFileExplorerOpen)
            await c.ExecuteAsync(c.Main.ToggleFileExplorerCommand);
    }

    private static async Task ScreensModeScene(SceneContext c)
    {
        await c.ShowChatAsync();
        c.Main.IsSlideMode = true;
        c.Main.ActiveScreen = AppScreen.Chat;
        await c.CaptureAsync(Info(4));
        c.Main.IsSlideMode = false;
    }

    private static async Task ApprovalScene(SceneContext c)
    {
        await c.RunAgentAsync(atEditApproval: () => c.CaptureAsync(Info(5)));
    }

    private static async Task ResultScene(SceneContext c)
    {
        await c.RunAgentAsync();
        await c.ShowChatAsync();
        await c.CaptureAsync(Info(6));
    }

    // ------------------------------------------------------------------ source control

    private static async Task OpenSourceControlAsync(SceneContext c)
    {
        await c.RunAgentAsync();
        await c.ShowChatAsync();
        await c.ExecuteAsync(c.Main.ToggleSourceControlCommand);
        await c.WaitUntilAsync(() => c.Main.SourceControl.IsVisible, "the source control panel");
        await c.Main.SourceControl.RefreshAsync();
        await c.WaitUntilAsync(() => c.Main.SourceControl.Unstaged.Count + c.Main.SourceControl.Staged.Count > 0, "the changed file in source control");
    }

    private static async Task DiffScene(SceneContext c)
    {
        await OpenSourceControlAsync(c);
        var entry = c.Main.SourceControl.Unstaged.FirstOrDefault() ?? c.Main.SourceControl.Staged.First();
        c.Main.SourceControl.OpenDiffCommand.Execute(entry);
        await c.WaitUntilAsync(() => c.Main.DiffViewer.IsVisible && c.Main.DiffViewer.Rows.Count > 0, "the diff viewer");
        await c.CaptureAsync(Info(7));
    }

    private static async Task CommitScene(SceneContext c)
    {
        await OpenSourceControlAsync(c);
        if (c.Main.DiffViewer.IsVisible) c.Main.DiffViewer.Close();

        var source = c.Main.SourceControl;
        if (source.Unstaged.Count > 0)
        {
            await c.ExecuteAsync(source.StageAllCommand);
            await c.WaitUntilAsync(() => source.Staged.Count > 0, "the staged change");
        }

        source.CommitMessage = "Fix Overdue: completed items are not overdue";
        await c.CaptureAsync(Info(8));
    }

    // ------------------------------------------------------------------ project knowledge

    private static async Task ProjectKnowledgeScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.ExecuteAsync(c.Main.ToggleAgentsMdCommand);
        await c.WaitUntilAsync(() => c.Main.ProjectKnowledge.IsVisible && c.Main.ProjectKnowledge.ActiveTabIndex == 0, "Project Knowledge (AGENTS.md)");
        await c.WaitUntilAsync(() => !string.IsNullOrEmpty(c.Main.ProjectKnowledge.MemoryContent), "the AGENTS.md text");
        await c.CaptureAsync(Info(9));
    }

    private static async Task SkillsScene(SceneContext c)
    {
        await OpenTabAsync(c, c.Main.ToggleSkillsCommand, 1);
        var knowledge = c.Main.ProjectKnowledge;
        await c.WaitUntilAsync(() => knowledge.Skills.Count > 0, "the skill list");
        var skill = knowledge.Skills.FirstOrDefault(s => s.Name == "csharp") ?? knowledge.Skills.First();
        await knowledge.SelectSkillAsync(skill);
        await c.WaitUntilAsync(() => !string.IsNullOrEmpty(knowledge.SelectedSkillContent), "the skill text");
        await c.CaptureAsync(Info(10));
    }

    private static async Task SelectCharacterAsync(SceneContext c, string id)
    {
        await OpenTabAsync(c, c.Main.ToggleCharactersCommand, 2);
        var characters = c.Main.ProjectKnowledge.Characters;
        await c.WaitUntilAsync(() => characters.Characters.Count > 0, "the character list");
        var item = characters.Characters.FirstOrDefault(x => x.Id == id) ?? characters.Characters.First();
        if (!ReferenceEquals(characters.SelectedCharacter, item))
            await characters.SelectCharacterAsync(item);
        await c.WaitUntilAsync(() => characters.SelectedCharacter != null && characters.SkillChoices.Count > 0, "the character's skills");
    }

    private static async Task CharacterScene(SceneContext c)
    {
        await SelectCharacterAsync(c, "dana-dotnet");
        await c.CaptureAsync(Info(11));
    }

    private static async Task AddSkillScene(SceneContext c)
    {
        await SelectCharacterAsync(c, "dana-dotnet");
        var characters = c.Main.ProjectKnowledge.Characters;
        await characters.BeginAddSkillAsync();
        characters.AddSkillCreatesNew = false;
        await c.WaitUntilAsync(() => characters.IsAddingSkill, "the Add skill panel");
        await c.TryWaitUntilAsync(() => characters.AvailableSkills.Count > 0, "skills to add", TimeSpan.FromSeconds(10));
        if (characters.AvailableSkills.Count > 0)
            characters.SelectedLibrarySkill = characters.AvailableSkills.First();
        await c.CaptureAsync(Info(12));
        characters.CancelAddSkill();
    }

    /// <summary>Closes source control, the diff and any other panel so the next page starts clean.</summary>
    private static async Task CloseWorkPanelsAsync(SceneContext c)
    {
        if (c.Main.DiffViewer.IsVisible) c.Main.DiffViewer.Close();
        await c.ShowChatAsync();
    }

    private static async Task OpenTabAsync(SceneContext c, System.Windows.Input.ICommand command, int tab)
    {
        await CloseWorkPanelsAsync(c);
        await c.ExecuteAsync(command);
        await c.WaitUntilAsync(() => c.Main.ProjectKnowledge.IsVisible && c.Main.ProjectKnowledge.ActiveTabIndex == tab,
            "the Project Knowledge tab");
    }

    // ------------------------------------------------------------------ flows

    private static async Task FlowCanvasScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.OpenFeatureTeamAsync();
        c.Flows.ClearSelection();
        await c.CaptureAsync(Info(13));
    }

    private static async Task InspectorScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.OpenFeatureTeamAsync();
        var review = c.Flows.Nodes.FirstOrDefault(n => n.Id == "review") ?? c.Flows.Nodes.First();
        c.Flows.SelectedNode = review;
        await c.CaptureAsync(Info(14));
    }

    private static async Task FlowRunningScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.StartFlowAsync();
        await c.WaitUntilAsync(() => NodeRunning(c, "backend") && NodeRunning(c, "model"), "Backend and Model running in parallel");
        await c.CaptureAsync(Info(15));
    }

    private static async Task FlowLoopScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        var watcher = await c.StartFlowAsync();
        await c.WaitUntilAsync(() => watcher.TimesRunning("backend") >= 2 && NodeRunning(c, "backend"),
            "the review to send the work back to Backend");
        await c.CaptureAsync(Info(16));
    }

    private static async Task FlowCompleteScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.WaitForFlowToFinishAsync();
        c.Flows.SelectedNode = c.Flows.Nodes.FirstOrDefault(n => n.Id == "release-notes") ?? c.Flows.Nodes.Last();
        await c.CaptureAsync(Info(17));
    }

    private static bool NodeRunning(SceneContext c, string id) => c.Flows.Nodes.Any(n => n.Id == id && n.IsRunning);

    // ------------------------------------------------------------------ pages and panels

    private static async Task SettingsScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.ExecuteAsync(c.Main.NavigateToSettingsCommand);
        await c.WaitUntilAsync(() => c.Main.IsSettingsMode, "the settings page");
        await c.SettleAsync(500);
        await c.CaptureAsync(Info(18));
        await c.ShowChatAsync();
    }

    private static async Task PaletteScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.ExecuteAsync(c.Main.ToggleCommandPaletteCommand);
        await c.WaitUntilAsync(() => c.Main.CommandPalette.IsOpen, "the command palette");
        c.Main.CommandPalette.SearchText = "flow";
        await c.CaptureAsync(Info(19));
        c.Main.CommandPalette.Close();
    }

    private static async Task CheckpointsScene(SceneContext c)
    {
        await c.RunAgentAsync();
        await CloseWorkPanelsAsync(c);
        await c.ExecuteAsync(c.Main.ToggleCheckpointBrowserCommand);
        await c.WaitUntilAsync(() => c.Main.IsCheckpointBrowserOpen, "the checkpoint browser");
        await c.SettleAsync(800);
        await c.CaptureAsync(Info(20));
    }

    private static async Task BackgroundTasksScene(SceneContext c)
    {
        await CloseWorkPanelsAsync(c);
        await c.ExecuteAsync(c.Main.ToggleBackgroundTasksCommand);
        await c.WaitUntilAsync(() => c.Main.BackgroundTasks.IsVisible, "the background tasks panel");
        var tasks = c.Main.BackgroundTasks;
        tasks.NewTaskPrompt = "Explain this project";
        tasks.StartNewTaskCommand.Execute(null);
        await c.WaitUntilAsync(() => tasks.Tasks.Count > 0, "the new background task");
        await c.WaitUntilAsync(() => tasks.Tasks.All(t => t.Status.ToString() != "Running"), "the background task to finish");
        await c.CaptureAsync(Info(21));
    }

    private static async Task LightThemeScene(SceneContext c)
    {
        await c.ShowChatAsync();
        var original = c.Theme;
        ThemeService.Apply(ThemeService.Light);
        try
        {
            await c.SettleAsync(400);
            await c.CaptureAsync(Info(22));
        }
        finally
        {
            ThemeService.Apply(original);
        }
    }
}
