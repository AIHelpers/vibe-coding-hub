using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private readonly Func<string, BoardStore> _storeFor;
    private readonly Action<Action> _marshal;
    private readonly Dictionary<string, ProjectBoard> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoardCardViewModel> _cards = new();
    private ProjectBoard? _current;

    /// <param name="isolation">Gives each card its worktree; without it (or without <paramref name="agent"/>) cards can be planned but not started.</param>
    /// <param name="storeFor">Where a project's board is saved; defaults to the per-user folder.</param>
    /// <param name="marshal">Runs a UI update on the UI thread; board changes arrive from agent threads.</param>
    public MissionControlViewModel(
        IWorkspaceIsolation? isolation = null, IBoardAgentRunner? agent = null,
        Func<string, BoardStore>? storeFor = null, Action<Action>? marshal = null)
    {
        _isolation = isolation;
        _agent = agent;
        _storeFor = storeFor ?? (dir => BoardStore.ForProject(dir));
        _marshal = marshal ?? (a => a());
    }

    public ObservableCollection<BoardCardViewModel> Backlog { get; } = new();
    public ObservableCollection<BoardCardViewModel> Running { get; } = new();
    public ObservableCollection<BoardCardViewModel> NeedsReview { get; } = new();
    public ObservableCollection<BoardCardViewModel> Done { get; } = new();

    [ObservableProperty]
    private bool _isVisible;

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

    private ProjectBoard CreateProject(string key)
    {
        var board = new TaskBoard(_storeFor(key));
        // No agent survives a restart: cards the file still shows as running were cut off.
        board.RecoverInterrupted();
        var runner = CanRun ? new BoardRunner(board, _isolation!, _agent!, key) : null;
        var project = new ProjectBoard(key, board, runner);
        board.Changed += (_, e) => _marshal(() => OnBoardChanged(project, e));
        return project;
    }

    // ------------------------------------------------------------------ actions

    private bool CanAdd() => _current != null && !string.IsNullOrWhiteSpace(NewTaskText);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddCard()
    {
        _current!.Board.Add(NewTaskText);
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
        foreach (var card in _current.Board.Cards) Place(new BoardCardViewModel(card, this));
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
