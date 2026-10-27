namespace TodoApp.Tests;

public class TodoServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    [Fact]
    public void Add_AssignsIncreasingIds()
    {
        var service = new TodoService();

        var first = service.Add("Buy milk", Today);
        var second = service.Add("Call the bank", Today);

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
    }

    [Fact]
    public void Add_RejectsBlankTitle()
    {
        var service = new TodoService();

        Assert.Throws<ArgumentException>(() => service.Add("   ", Today));
    }

    [Fact]
    public void Complete_MarksTheItemDone()
    {
        var service = new TodoService();
        var item = service.Add("Buy milk", Today);

        var found = service.Complete(item.Id);

        Assert.True(found);
        Assert.True(service.All.Single().Done);
    }

    [Fact]
    public void Complete_UnknownId_ReturnsFalse()
    {
        var service = new TodoService();

        Assert.False(service.Complete(42));
    }

    [Fact]
    public void Overdue_IncludesOpenItemsThatAreLate()
    {
        var service = new TodoService();
        service.Add("Pay rent", Today.AddDays(-3));

        var overdue = service.Overdue(Today);

        Assert.Equal("Pay rent", Assert.Single(overdue).Title);
    }

    [Fact]
    public void Overdue_ExcludesCompletedItems()
    {
        var service = new TodoService();
        var paid = service.Add("Pay rent", Today.AddDays(-3));
        service.Add("Renew passport", Today.AddDays(-1));
        service.Complete(paid.Id);

        var overdue = service.Overdue(Today);

        Assert.Equal("Renew passport", Assert.Single(overdue).Title);
    }

    [Fact]
    public void Overdue_ExcludesItemsDueLater()
    {
        var service = new TodoService();
        service.Add("Book flights", Today.AddDays(10));

        Assert.Empty(service.Overdue(Today));
    }

    [Fact]
    public void Overdue_ListsTheOldestFirst()
    {
        var service = new TodoService();
        service.Add("Newer", Today.AddDays(-1));
        service.Add("Older", Today.AddDays(-9));

        var titles = service.Overdue(Today).Select(x => x.Title).ToArray();

        Assert.Equal(new[] { "Older", "Newer" }, titles);
    }
}
