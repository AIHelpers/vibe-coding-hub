namespace TodoApp;

/// <summary>An in-memory to-do list.</summary>
public sealed class TodoService
{
    private readonly List<TodoItem> _items = new();
    private int _nextId = 1;

    /// <summary>Every item, in the order it was added.</summary>
    public IReadOnlyList<TodoItem> All => _items;

    /// <summary>Adds an open item and returns it.</summary>
    public TodoItem Add(string title, DateOnly due)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A to-do needs a title.", nameof(title));

        var item = new TodoItem(_nextId++, title.Trim(), due);
        _items.Add(item);
        return item;
    }

    /// <summary>Marks an item as done. Returns false when there is no item with that id.</summary>
    public bool Complete(int id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return false;

        _items[index] = _items[index] with { Done = true };
        return true;
    }

    /// <summary>The items that are late as of <paramref name="today"/>, oldest first.</summary>
    public IReadOnlyList<TodoItem> Overdue(DateOnly today) =>
        _items.Where(x => x.Due <= today).OrderBy(x => x.Due).ToList();
}
