using AiCodeAgent.Core.Sessions;
using System.CommandLine;

namespace AiCodeAgent.CLI;

/// <summary>
/// CLI commands for task history: list saved tasks and view a task's dialog.
/// </summary>
public static class TaskHistoryCommands
{
    public static void AddCommands(Command rootCommand)
    {
        // tasks list
        var tasksCommand = new Command("tasks", "Manage saved task history");
        var listCommand = new Command("list", "List all saved tasks (newest first)");
        var limitOption = new Option<int>("--limit", () => 20, "Maximum number of tasks to show");
        listCommand.AddOption(limitOption);
        listCommand.SetHandler(async (limit) =>
        {
            var store = new TaskHistoryStore();
            var tasks = await store.ListAsync();
            if (tasks.Count == 0)
            {
                Console.WriteLine("No saved tasks found.");
                Console.WriteLine($"Tasks are stored in: {store.StoreDirectory}");
                return;
            }

            Console.WriteLine($"Saved tasks ({tasks.Count} total, showing up to {limit}):");
            Console.WriteLine($"  {"ID",-12} {"Title",-40} {"Messages",-8} {"Updated",-20} Status");
            Console.WriteLine(new string('-', 90));
            foreach (var t in tasks.Take(limit))
            {
                var title = t.Title.Length > 38 ? t.Title[..35] + "..." : t.Title;
                Console.WriteLine($"  {t.Id,-12} {title,-40} {t.Messages.Count,-8} {t.UpdatedAt:yyyy-MM-dd HH:mm}    {t.Status ?? "active"}");
            }
            Console.WriteLine();
            Console.WriteLine($"Store: {store.StoreDirectory}");
        }, limitOption);

        // tasks show <id>
        var showCommand = new Command("show", "Show the full dialog for a task");
        var idArg = new Argument<string>("taskId", "The task ID to display");
        showCommand.AddArgument(idArg);
        showCommand.SetHandler(async (taskId) =>
        {
            var store = new TaskHistoryStore();
            var entry = await store.LoadAsync(taskId);
            if (entry == null)
            {
                Console.Error.WriteLine($"Task '{taskId}' not found.");
                Console.Error.WriteLine($"Tasks are stored in: {store.StoreDirectory}");
                return;
            }

            Console.WriteLine($"Task:    {entry.Title}");
            Console.WriteLine($"ID:      {entry.Id}");
            Console.WriteLine($"Created: {entry.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Updated: {entry.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Status:  {entry.Status ?? "active"}");
            if (!string.IsNullOrEmpty(entry.Provider))
                Console.WriteLine($"Provider: {entry.Provider}");
            if (!string.IsNullOrEmpty(entry.Model))
                Console.WriteLine($"Model:    {entry.Model}");
            if (!string.IsNullOrEmpty(entry.WorkingDirectory))
                Console.WriteLine($"Dir:      {entry.WorkingDirectory}");
            Console.WriteLine($"Messages: {entry.Messages.Count}, Tool calls: {entry.ToolCalls.Count}");
            Console.WriteLine();
            Console.WriteLine(new string('=', 80));
            Console.WriteLine("Dialog:");
            Console.WriteLine(new string('=', 80));

            foreach (var msg in entry.Messages)
            {
                var roleLabel = msg.Role switch
                {
                    "System" => "[SYSTEM]",
                    "User" => "[USER]",
                    "Assistant" => "[ASSISTANT]",
                    "Tool" => "[TOOL]",
                    _ => $"[{msg.Role}]"
                };
                Console.WriteLine($"\n{roleLabel} ({msg.Timestamp:HH:mm:ss}):");
                Console.WriteLine(msg.Content);
            }

            if (entry.ToolCalls.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(new string('=', 80));
                Console.WriteLine($"Tool Calls ({entry.ToolCalls.Count}):");
                Console.WriteLine(new string('=', 80));
                foreach (var tc in entry.ToolCalls)
                {
                    Console.WriteLine($"\n  [{tc.Timestamp:HH:mm:ss}] {tc.ToolName}" +
                        (tc.IsError ? " (ERROR)" : ""));
                    if (!string.IsNullOrEmpty(tc.Arguments))
                        Console.WriteLine($"    Args:   {tc.Arguments}");
                    if (!string.IsNullOrEmpty(tc.Output))
                    {
                        var output = tc.Output.Length > 500
                            ? tc.Output[..500] + "\n    ...[truncated]"
                            : tc.Output;
                        Console.WriteLine($"    Output: {output}");
                    }
                }
            }
        }, idArg);

        // tasks delete <id>
        var deleteCommand = new Command("delete", "Delete a saved task");
        var deleteIdArg = new Argument<string>("taskId", "The task ID to delete");
        deleteCommand.AddArgument(deleteIdArg);
        deleteCommand.SetHandler(async (taskId) =>
        {
            var store = new TaskHistoryStore();
            var entry = await store.LoadAsync(taskId);
            if (entry == null)
            {
                Console.Error.WriteLine($"Task '{taskId}' not found.");
                return;
            }
            await store.DeleteAsync(taskId);
            Console.WriteLine($"Deleted task '{entry.Title}' ({taskId})");
        }, deleteIdArg);

        tasksCommand.AddCommand(listCommand);
        tasksCommand.AddCommand(showCommand);
        tasksCommand.AddCommand(deleteCommand);
        rootCommand.AddCommand(tasksCommand);
    }
}