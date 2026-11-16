using AiCodeAgent.Core.Board;

namespace AiCodeAgent.Core.Tests.Board;

/// <summary>E11-S1: the cards, the rules for moving them between columns, and how the board is saved.</summary>
public sealed class TaskBoardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "board-" + Guid.NewGuid().ToString("N"));

    public TaskBoardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private BoardStore Store() => new(Path.Combine(_dir, "board.json"));

    // ------------------------------------------------------------------ cards

    [Fact]
    public void Add_PutsTheCardInTheBacklog_AndTakesTheTitleFromTheFirstLine()
    {
        var board = new TaskBoard();

        var card = board.Add("  Fix   the login\ttimeout \nand add a test");

        Assert.Equal(BoardColumn.Backlog, card.Column);
        Assert.Equal("Fix the login timeout", card.Title);
        Assert.Equal("Fix   the login\ttimeout \nand add a test", card.Prompt);
        Assert.Same(card, board.Get(card.Id));
        Assert.Throws<ArgumentException>(() => board.Add("   "));
    }

    [Fact]
    public void Title_IsCutAtSeventyCharacters_AndAnExplicitTitleWins()
    {
        Assert.Equal(70, BoardCard.TitleFrom(new string('x', 200)).Length);
        Assert.EndsWith("…", BoardCard.TitleFrom(new string('x', 200)));
        Assert.Equal("Mine", new TaskBoard().Add("do something", "  Mine ").Title);
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public void Cards_MoveBacklogRunningNeedsReviewDone_AndBack()
    {
        var board = new TaskBoard();
        var id = board.Add("task").Id;

        Assert.Null(board.EndRun(id, CardOutcome.Completed));   // not running yet
        Assert.Null(board.MarkDone(id));                          // nothing to accept yet

        var running = board.BeginRun(id)!;
        Assert.Equal(BoardColumn.Running, running.Column);
        Assert.Equal(1, running.Runs);
        Assert.NotNull(running.StartedAt);
        Assert.Null(board.BeginRun(id));                          // already running

        var review = board.EndRun(id, CardOutcome.Completed)!;
        Assert.Equal(BoardColumn.NeedsReview, review.Column);
        Assert.Equal(CardOutcome.Completed, review.Outcome);
        Assert.NotNull(review.FinishedAt);

        Assert.Equal(BoardColumn.Done, board.MarkDone(id)!.Column);
        Assert.Null(board.MarkDone(id));
        Assert.Equal(BoardColumn.Backlog, board.ReturnToBacklog(id)!.Column);
        Assert.Null(board.ReturnToBacklog(id));                   // already in the backlog
    }

    [Fact]
    public void ARunningCard_CannotBeRemoved_Others_Can()
    {
        var board = new TaskBoard();
        var running = board.Add("a").Id;
        var idle = board.Add("b").Id;
        board.BeginRun(running);

        Assert.False(board.Remove(running));
        Assert.True(board.Remove(idle));
        Assert.False(board.Remove(idle));
        Assert.Single(board.Cards);
    }

    [Fact]
    public void ANewRun_ClearsTheLastResult_ButKeepsTheCost()
    {
        var board = new TaskBoard();
        var id = board.Add("task").Id;
        board.BeginRun(id);
        board.EndRun(id, CardOutcome.Failed, c => c with { Branch = "agent/x", Summary = "boom", CostUsd = 0.5m, Tokens = 100 });
        board.ReturnToBacklog(id);

        var again = board.BeginRun(id)!;

        Assert.Equal(2, again.Runs);
        Assert.Null(again.Branch);
        Assert.Null(again.Summary);
        Assert.Equal(CardOutcome.None, again.Outcome);
        Assert.Equal(0.5m, again.CostUsd);
        Assert.Equal(100, again.Tokens);
    }

    [Fact]
    public void Report_OnlyAppliesToARunningCard_AndIsNotSaved()
    {
        var store = Store();
        var board = new TaskBoard(store);
        var id = board.Add("task").Id;

        Assert.Null(board.Report(id, c => c with { LastAction = "x" })); // still in the backlog
        board.BeginRun(id);
        Assert.Equal("edit a.cs", board.Report(id, c => c with { LastAction = "edit a.cs" })!.LastAction);

        Assert.Null(store.Load().Single().LastAction);                    // progress stays in memory
        board.EndRun(id, CardOutcome.Completed);
        Assert.Equal("edit a.cs", store.Load().Single().LastAction);      // the next state change saves it
    }

    [Fact]
    public void Changed_ReportsEveryAddUpdateAndRemove()
    {
        var board = new TaskBoard();
        var kinds = new List<BoardChangeKind>();
        board.Changed += (_, e) => kinds.Add(e.Kind);

        var id = board.Add("task").Id;
        board.BeginRun(id);
        board.EndRun(id, CardOutcome.Completed);
        board.Remove(id);

        Assert.Equal(new[] { BoardChangeKind.Added, BoardChangeKind.Updated, BoardChangeKind.Updated, BoardChangeKind.Removed }, kinds);
    }

    // ------------------------------------------------------------------ saving

    [Fact]
    public void TheBoard_IsSaved_AndComesBackAfterARestart()
    {
        var first = new TaskBoard(Store());
        var id = first.Add("Fix it").Id;
        first.BeginRun(id);
        first.EndRun(id, CardOutcome.Completed, c => c with { Branch = "agent/x/fix-it", Summary = "Done.", CostUsd = 0.25m });
        first.Add("Second task");

        var reloaded = new TaskBoard(Store());

        Assert.Equal(2, reloaded.Cards.Count);
        var card = reloaded.Get(id)!;
        Assert.Equal(BoardColumn.NeedsReview, card.Column);
        Assert.Equal(CardOutcome.Completed, card.Outcome);
        Assert.Equal("agent/x/fix-it", card.Branch);
        Assert.Equal(0.25m, card.CostUsd);
        Assert.Contains("\"needsReview\"", File.ReadAllText(Store().Path)); // readable file, not numbers
    }

    [Fact]
    public void ADamagedFile_ReadsAsAnEmptyBoard_InsteadOfBreakingTheApp()
    {
        File.WriteAllText(Store().Path, "{ not json");

        var board = new TaskBoard(Store());

        Assert.Empty(board.Cards);
        board.Add("still works");
        Assert.Single(new TaskBoard(Store()).Cards);
    }

    [Fact]
    public void CardsThatWereRunningWhenTheAppClosed_BecomeInterrupted()
    {
        var first = new TaskBoard(Store());
        var cut = first.Add("long task").Id;
        var waiting = first.Add("other").Id;
        first.BeginRun(cut);

        var after = new TaskBoard(Store());
        Assert.Equal(1, after.RecoverInterrupted());

        var card = after.Get(cut)!;
        Assert.Equal(BoardColumn.NeedsReview, card.Column);
        Assert.Equal(CardOutcome.Interrupted, card.Outcome);
        Assert.Contains("worktree", card.Summary);
        Assert.Equal(BoardColumn.Backlog, after.Get(waiting)!.Column);
        Assert.Equal(0, after.RecoverInterrupted());
    }

    [Fact]
    public void TheStoreFolder_IsStablePerProject_AndKeepsAlikeNamesApart()
    {
        var a = BoardStore.FolderFor(Path.Combine(_dir, "one", "app"));
        var b = BoardStore.FolderFor(Path.Combine(_dir, "two", "app"));

        Assert.Equal(a, BoardStore.FolderFor(Path.Combine(_dir, "one", "app") + Path.DirectorySeparatorChar));
        Assert.NotEqual(a, b);
        Assert.StartsWith("app-", a);
    }
}
