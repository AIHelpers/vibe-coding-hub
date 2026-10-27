namespace TodoApp;

/// <summary>One entry on the to-do list.</summary>
/// <param name="Id">Unique, assigned by <see cref="TodoService"/>.</param>
/// <param name="Title">What has to be done.</param>
/// <param name="Due">The day it has to be done by.</param>
/// <param name="Done">True once the item has been completed.</param>
public sealed record TodoItem(int Id, string Title, DateOnly Due, bool Done = false);
