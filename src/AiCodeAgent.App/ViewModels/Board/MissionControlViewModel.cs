using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Board;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Mission Control (E11-S1): a board with the columns Backlog, Running, Needs review and Done. One card is one task, run by an
/// agent in its own git worktree and branch, so several cards can run side by side. Each project has its own board; cards keep
/// running when you switch to another project.
/// </summary>
public sealed partial class MissionControlViewModel : ObservableObject
{
    private readonly IWorkspaceIsolation? _isolation;
    private readonly IBoardAgentRunner? _agent;
    private readonly IBoardTargetRunner? _targets;
    private readonly BoardAgentCatalog? _catalog;
    private readonly Func<string, BoardStore> _storeFor;
    private readonly Action<Action> _marshal;
    private readonly Dictionary<string, ProjectBoard> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoardCardViewModel> _cards = new();
    private ProjectBoard? _current;

    private static readonly AgentChoice DefaultChoice =
        new(AgentTarget.Default, "Default agent", BoardAgentCatalog.BuiltInGroup, true, "The app's own agent with the model and settings you use in chat.");

    /// <param name="isolation">Gives each card its worktree; without it (or without <paramref name="agent"/>) cards can be planned but not started.</param>
    /// <param name="storeFor">Where a project's board is saved; defaults to the per-user folder.</param>
    /// <param name="marshal">Runs a UI update on the UI thread; board changes arrive from agent threads.</param>
    /// <param name="targets">Runs cards that are given a character, a flow or an external agent (E11-S2).</param>
    /// <param name="catalog">Lists the agents a card can be given; without it only the default agent is offered.</param>
    public MissionControlViewModel(
        IWorkspaceIsolation? isolation = null, IBoardAgentRunner? agent = null,
        Func<string, BoardStore>? storeFor = null, Action<Action>? marshal = null,
        IBoardTargetRunner? targets = null, BoardAgentCatalog? catalog = null)
    {
        _isolation = isolation;
        _agent = agent;
        _targets = targets;
        _catalog = catalog;
        _storeFor = storeFor ?? (dir => BoardStore.ForProject(dir));
        _marshal = marshal ?? (a => a());
    }

    public ObservableCollection<BoardCardViewModel> Backlog { get; } = new();
    public ObservableCollection<BoardCardViewModel> Running { get; } = new();
    public ObservableCollection<BoardCardViewModel> NeedsReview { get; } = new();
    public ObservableCollection<BoardCardViewModel> Done { get; } = new();

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Everything a card can be given: the default agent, characters, flows and external agents (E11-S2).</summary>
    public ObservableCollection<AgentChoice> AgentChoices { get; } = new() { DefaultChoice };

    /// <summary>The agent the next new card gets. It stays selected, so a batch of cards for one agent is quick to add.</summary>
    [ObservableProperty]
    private AgentChoice? _selectedAgent = DefaultChoice;

    /// <summary>There is a real choice to make: show the pickers.</summary>
    public bool HasAgentChoices => AgentChoices.Count > 1;

    partial void OnSelectedAgentChanged(AgentChoice? oldValue, AgentChoice? newValue)
    {
        // An agent that cannot start here (a CLI that is not installed) explains what to do and leaves the choice as it was.
        if (newValue is { IsAvailable: false })
        {
            Notice = Explain(newValue);
            SelectedAgent = oldValue is { IsAvailable: true } ? oldValue : DefaultChoice;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCardCommand))]
    private string _newTaskText = string.Empty;

    /// <summary>The folder name of the project whose board is shown.</summary>
    [ObservableProperty]
    private string _projectName = string.Empty;

    /// <summary>Something the user should know, for example why a card could not start. Empty when there is nothing.</summary>
    [ObservableProperty]
    private string _notice = string.Empty;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    /// <summary>Cards can be started: an agent and git worktrees are available.</summary>
    public bool CanRun => _isolation != null && _agent != null;

    public bool HasNoCards => Backlog.Count + Running.Count + NeedsReview.Count + Done.Count == 0;

    /// <summary>Cards running now in all projects: the badge on the activity bar.</summary>
    public int RunningCount => _projects.Values.Sum(p => p.Board.Count(BoardColumn.Running));

    public bool HasRunning => RunningCount > 0;

    public string StartAllText => Backlog.Count > 1 ? $"▶ Start all ({Backlog.Count})" : "▶ Start";

    /// <summary>Shows the board of <paramref name="directory"/>, creating it on first use. Boards of other projects keep their runs.</summary>
    public void SetProject(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var key = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (key.Length == 0) key = directory;
        if (_current != null && string.Equals(_current.Key, key, StringComparison.OrdinalIgnoreCase)) return;

        if (!_projects.TryGetValue(key, out var project))
        {
            project = CreateProject(key);
            _projects[key] = project;
        }
        _current = project;
        ProjectName = Path.GetFileName(key) is { Length: > 0 } name ? name : key;
        Notice = string.Empty;
        Rebuild();
        AddCardCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ agents (E11-S2)

    /// <summary>
    /// Reads the characters, flows and installed external agents again. Called when the board opens, because characters and
    /// flows change and a CLI may have been installed since last time. The list keeps its old content when reading fails.
    /// </summary>
    public async Task RefreshAgentsAsync()
    {
        if (_catalog == null) return;
        IReadOnlyList<AgentChoice> choices;
        try { choices = await _catalog.ListAsync().ConfigureAwait(false); }
        catch (Exception) { return; }
        _marshal(() => ApplyChoices(choices));
    }

    private void ApplyChoices(IReadOnlyList<AgentChoice> choices)
    {
        var selected = SelectedAgent?.Target ?? AgentTarget.Default;
        AgentChoices.Clear();
        foreach (var choice in choices) AgentChoices.Add(choice);
        if (AgentChoices.Count == 0) AgentChoices.Add(DefaultChoice);

        // A card may point at a character that was deleted or a flow that was renamed: keep it visible and say it is missing.
        foreach (var card in _current?.Board.Cards ?? Array.Empty<BoardCard>())
            EnsureChoice(card.Agent);

        SelectedAgent = ChoiceFor(selected) ?? AgentChoices[0];
        foreach (var card in _cards.Values) card.RefreshAgent();
        OnPropertyChanged(nameof(HasAgentChoices));
    }

    private void EnsureChoice(AgentTarget target)
    {
        if (ChoiceFor(target) != null) return;
        AgentChoices.Add(new AgentChoice(target, $"{target.DisplayName} (not found)", "Missing", false,
            "This agent is no longer available: a deleted character, a renamed flow or a removed program. Pick another one."));
    }

    internal AgentChoice? ChoiceFor(AgentTarget target) => AgentChoices.FirstOrDefault(c => c.Target.Equals(target));

    internal void SetAgent(BoardCardViewModel card, AgentChoice choice)
    {
        if (!choice.IsAvailable)
        {
            Notice = Explain(choice);
            card.Update(card.Card); // the picker goes back to the agent the card has
            return;
        }
        _current?.Board.SetAgent(card.Id, choice.Target);
    }

    private static string Explain(AgentChoice choice) =>
        string.IsNullOrWhiteSpace(choice.Detail) ? $"{choice.Label} is not available." : choice.Detail!;

    private ProjectBoard CreateProject(string key)
    {
        var board = new TaskBoard(_storeFor(key));
        // No agent survives a restart: cards the file still shows as running were cut off.
        board.RecoverInterrupted();
        var runner = CanRun ? new BoardRunner(board, _isolation!, _agent!, key, targets: _targets) : null;
        var project = new ProjectBoard(key, board, runner);
        board.Changed += (_, e) => _marshal(() => OnBoardChanged(project, e));
        return project;
    }

    // ------------------------------------------------------------------ actions

    private bool CanAdd() => _current != null && !string.IsNullOrWhiteSpace(NewTaskText);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddCard()
    {
        _current!.Board.Add(NewTaskText, agent: SelectedAgent?.Target);
        NewTaskText = string.Empty;
        Notice = string.Empty;
    }

    [RelayCommand]
    private void StartAll()
    {
        if (!EnsureCanRun()) return;
        _current!.Runner!.StartAll();
    }

    [RelayCommand]
    private void Close() => IsVisible = false;

    [RelayCommand]
    private void DismissNotice() => Notice = string.Empty;

    internal void Start(BoardCardViewModel card)
    {
        if (!EnsureCanRun()) return;
        _current!.Runner!.Start(card.Id);
    }

    internal void Stop(BoardCardViewModel card) => _current?.Runner?.Stop(card.Id);

    internal void MarkDone(BoardCardViewModel card) => _current?.Board.MarkDone(card.Id);

    internal void ReturnToBacklog(BoardCardViewModel card) => _current?.Board.ReturnToBacklog(card.Id);

    internal void Retry(BoardCardViewModel card)
    {
        if (!EnsureCanRun()) return;
        if (_current!.Board.ReturnToBacklog(card.Id) != null) _current.Runner!.Start(card.Id);
    }

    internal void Remove(BoardCardViewModel card) => _current?.Board.Remove(card.Id);

    private bool EnsureCanRun()
    {
        if (_current?.Runner != null) return true;
        Notice = "Cards cannot be started here: no agent or git support is available.";
        return false;
    }

    // ------------------------------------------------------------------ keeping the columns in step with the board

    private void Rebuild()
    {
        _cards.Clear();
        foreach (var column in new[] { Backlog, Running, NeedsReview, Done }) column.Clear();
        if (_current == null) return;
        foreach (var card in _current.Board.Cards)
        {
            EnsureChoice(card.Agent);
            Place(new BoardCardViewModel(card, this));
        }
        OnPropertyChanged(nameof(HasAgentChoices));
        NotifyCounts();
    }

    private void OnBoardChanged(ProjectBoard project, BoardChangedEventArgs e)
    {
        if (ReferenceEquals(project, _current))
        {
            switch (e.Kind)
            {
                case BoardChangeKind.Added:
                    Place(new BoardCardViewModel(e.Card, this));
                    break;

                case BoardChangeKind.Updated when _cards.TryGetValue(e.Card.Id, out var existing):
                    var moved = existing.Column != e.Card.Column;
                    if (moved) Collection(existing.Column).Remove(existing);
                    existing.Update(e.Card);
                    if (moved) Place(existing);
                    break;

                case BoardChangeKind.Updated:
                    Place(new BoardCardViewModel(e.Card, this));
                    break;

                case BoardChangeKind.Removed when _cards.Remove(e.Card.Id, out var gone):
                    Collection(gone.Column).Remove(gone);
                    break;
            }
        }
        NotifyCounts();
    }

    private void Place(BoardCardViewModel card)
    {
        _cards[card.Id] = card;
        var column = Collection(card.Column);
        var index = 0;
        while (index < column.Count && Compare(card.Card, column[index].Card) >= 0) index++;
        column.Insert(index, card);
    }

    private ObservableCollection<BoardCardViewModel> Collection(BoardColumn column) => column switch
    {
        BoardColumn.Backlog => Backlog,
        BoardColumn.Running => Running,
        BoardColumn.NeedsReview => NeedsReview,
        _ => Done
    };

    /// <summary>Backlog and Running: oldest first (the order to work through). Needs review and Done: the latest result on top.</summary>
    private static int Compare(BoardCard a, BoardCard b) => a.Column switch
    {
        BoardColumn.Backlog => a.CreatedAt.CompareTo(b.CreatedAt),
        BoardColumn.Running => (a.StartedAt ?? a.CreatedAt).CompareTo(b.StartedAt ?? b.CreatedAt),
        _ => (b.FinishedAt ?? b.CreatedAt).CompareTo(a.FinishedAt ?? a.CreatedAt)
    };

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasNoCards));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(StartAllText));
    }

    private sealed record ProjectBoard(string Key, TaskBoard Board, BoardRunner? Runner);
}
