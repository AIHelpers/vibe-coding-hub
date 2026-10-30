using System.Diagnostics;
using System.Windows.Input;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Samples;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.Screenshots;

/// <summary>
/// What a scene driver gets: the running main window and its view models, helpers to wait for a state and to
/// capture a frame, and the two shared "story" runs (the agent fixing the sample, the feature-team flow) that
/// several scenes look at from different angles.
/// </summary>
internal sealed class SceneContext
{
    private readonly Options _options;
    private bool _agentRunDone;
    private FlowWatcher? _flow;
    private Task? _flowTask;

    public SceneContext(Options options, Workspace workspace, string theme, string outDirectory, Window window, MainViewModel main)
    {
        _options = options;
        Workspace = workspace;
        Theme = theme;
        OutDirectory = outDirectory;
        Window = window;
        Main = main;
        Chat = main.ChatViewModel ?? throw new SceneException("The main view model has no chat view model.");
    }

    public Workspace Workspace { get; }
    public string Theme { get; }
    public string OutDirectory { get; }
    public Window Window { get; }
    public MainViewModel Main { get; }
    public ChatViewModel Chat { get; }

    public FlowEditorViewModel Flows => Main.Flows;

    public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(Math.Max(5, _options.SceneTimeoutSeconds - 10));

    // ------------------------------------------------------------------ waiting and rendering

    /// <summary>Lets pending UI work, bindings and layout finish, then renders a few frames.</summary>
    public async Task SettleAsync(int milliseconds = 120)
    {
        await Task.Delay(milliseconds).ConfigureAwait(true);
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20).ConfigureAwait(true);
        }
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return;
            if (clock.Elapsed > limit) throw new SceneException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
            await Task.Delay(25).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Like <see cref="WaitUntilAsync"/> but a timeout only returns false (for optional details).</summary>
    public async Task<bool> TryWaitUntilAsync(Func<bool> condition, string what, TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(condition, what, timeout).ConfigureAwait(true);
            return true;
        }
        catch (SceneException)
        {
            return false;
        }
    }

    /// <summary>Runs a command and, for async commands, waits for it to finish.</summary>
    public async Task ExecuteAsync(ICommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand asyncCommand)
            await asyncCommand.ExecuteAsync(parameter).ConfigureAwait(true);
        else
            command.Execute(parameter);
        await SettleAsync(60).ConfigureAwait(true);
    }

    /// <summary>Back to the plain conversation: closes panels, pages and overlays.</summary>
    public async Task ShowChatAsync()
    {
        if (Main.IsSlideMode) Main.IsSlideMode = false;
        if (Main.CommandPalette.IsOpen) Main.CommandPalette.Close();
        await ExecuteAsync(Main.ShowChatCommand).ConfigureAwait(true);
    }

    public async Task CaptureAsync(SceneInfo scene)
    {
        await SettleAsync(250).ConfigureAwait(true);
        var path = Path.Combine(OutDirectory, scene.FileName);
        SaveFrame(path);
    }

    /// <summary>Failure evidence: what the window looked like when a scene gave up.</summary>
    public void SaveFailureFrame(SceneInfo scene)
    {
        try
        {
            var directory = Path.Combine(Workspace.Root, "failures");
            Directory.CreateDirectory(directory);
            SaveFrame(Path.Combine(directory, scene.FileName));
        }
        catch
        {
            // best effort
        }
    }

    private void SaveFrame(string path)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame()
                    ?? throw new SceneException("The window produced no frame (is Skia drawing enabled?).");
        if (frame.PixelSize.Width != SceneCatalog.Width || frame.PixelSize.Height != SceneCatalog.Height)
            throw new SceneException($"The frame is {frame.PixelSize.Width}x{frame.PixelSize.Height}, expected {SceneCatalog.Width}x{SceneCatalog.Height}.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path);
    }

    // ------------------------------------------------------------------ the agent story

    /// <summary>
    /// The first task of the sample: the scripted agent reads, edits (approval), verifies and summarizes. Idempotent:
    /// the first caller runs it; <paramref name="atEditApproval"/> is invoked while the edit waits for approval.
    /// </summary>
    public async Task RunAgentAsync(Func<Task>? atEditApproval = null)
    {
        if (_agentRunDone) return;
        await ShowChatAsync().ConfigureAwait(true);

        Chat.InputText = SampleProjectInstaller.FirstTaskPrompt;
        var send = Chat.SendCommand.ExecuteAsync(null);
        var clock = Stopwatch.StartNew();
        var atApprovalDone = atEditApproval == null;

        while (true)
        {
            if (Chat.ShowApprovalDialog)
            {
                if (!atApprovalDone && Chat.IsEditApproval)
                {
                    // the diff preview of the edit is computed asynchronously
                    await TryWaitUntilAsync(() => Chat.ApprovalPreview != null, "the diff preview", TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                    await atEditApproval!().ConfigureAwait(true);
                    atApprovalDone = true;
                }
                Chat.ApproveToolCommand.Execute(null);
                await SettleAsync(60).ConfigureAwait(true);
                continue;
            }

            if (send.IsCompleted && !Chat.IsProcessing) break;
            if (clock.Elapsed > DefaultTimeout)
                throw new SceneException("The scripted agent run did not finish in time.");
            await Task.Delay(25).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        try { await send.ConfigureAwait(true); }
        catch (OperationCanceledException) { /* cancelled by shutdown */ }

        if (!atApprovalDone)
            throw new SceneException("The run finished without asking to approve the edit.");

        var summary = Chat.Timeline.Items.OfType<AssistantTextItem>().LastOrDefault()?.Content ?? string.Empty;
        if (!summary.Contains("all tests pass", StringComparison.OrdinalIgnoreCase))
            throw new SceneException(
                "The scripted run ended without a passing verification. Is the .NET SDK installed and NuGet reachable? " +
                "Re-run with --fast-checks to use instant stand-in commands. Last message: " + Shorten(summary));

        _agentRunDone = true;
    }

    // ------------------------------------------------------------------ the flow story

    /// <summary>Opens Flows with the feature-team flow loaded.</summary>
    public async Task OpenFeatureTeamAsync()
    {
        if (!Main.Flows.IsVisible)
        {
            await ShowChatAsync().ConfigureAwait(true);
            await ExecuteAsync(Main.ToggleFlowsCommand).ConfigureAwait(true);
        }

        await WaitUntilAsync(() => Flows.Pipelines.Contains(FeatureTeam), $"the '{FeatureTeam}' flow in the list").ConfigureAwait(true);
        if (Flows.SelectedPipeline != FeatureTeam)
            Flows.SelectedPipeline = FeatureTeam;
        await WaitUntilAsync(() => Flows.Nodes.Count == 5, "the five steps on the canvas").ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);
    }

    public const string FeatureTeam = "feature-team";
    public const string FlowTask = "Add an overdue summary to TodoApp and prepare its release notes";

    /// <summary>Starts the feature-team flow once; later calls just return the watcher.</summary>
    public async Task<FlowWatcher> StartFlowAsync()
    {
        if (_flow != null) return _flow;
        await OpenFeatureTeamAsync().ConfigureAwait(true);

        _flow = new FlowWatcher(Flows.Nodes);
        Flows.RunTask = FlowTask;
        _flowTask = Flows.RunAsync();
        await WaitUntilAsync(() => Flows.IsRunning, "the flow to start").ConfigureAwait(true);
        return _flow;
    }

    public async Task WaitForFlowToFinishAsync()
    {
        var watcher = await StartFlowAsync().ConfigureAwait(true);
        // gates ask the user before a step; the feature-team flow has none, but never hang on one
        await WaitUntilAsync(() =>
        {
            foreach (var node in Flows.Nodes.Where(n => n.NeedsAnswer).ToList())
                Flows.Approve(node);
            return !Flows.IsRunning && watcher.TimesRunning("release-notes") > 0;
        }, "the flow to finish").ConfigureAwait(true);

        if (_flowTask != null)
        {
            try { await _flowTask.ConfigureAwait(true); }
            catch (OperationCanceledException) { /* stopped */ }
        }
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "...";
}

/// <summary>Remembers how often each step of the running flow went to "Running" (a loop shows up as a second time).</summary>
internal sealed class FlowWatcher
{
    private readonly Dictionary<string, int> _timesRunning = new(StringComparer.OrdinalIgnoreCase);

    public FlowWatcher(IEnumerable<FlowNodeViewModel> nodes)
    {
        foreach (var node in nodes.ToList())
        {
            var id = node.Id;
            _timesRunning[id] = node.RunState == FlowNodeState.Running ? 1 : 0;
            node.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FlowNodeViewModel.RunState) && node.RunState == FlowNodeState.Running)
                    _timesRunning[id] = _timesRunning.GetValueOrDefault(id) + 1;
            };
        }
    }

    public int TimesRunning(string id) => _timesRunning.GetValueOrDefault(id);
}
