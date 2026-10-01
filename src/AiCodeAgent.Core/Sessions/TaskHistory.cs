using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Sessions;

/// <summary>
/// A saved task with its conversation history and metadata.
/// </summary>
public class TaskHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? WorkingDirectory { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TaskMessageRecord> Messages { get; set; } = new();
    public List<TaskToolCallRecord> ToolCalls { get; set; } = new();
    public string? Status { get; set; } = "active";
}

public class TaskMessageRecord
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class TaskToolCallRecord
{
    public string ToolName { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? Output { get; set; }
    public bool IsError { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// JSON file-based store for task history. Persists tasks to a
/// `tasks/` directory under the application data folder.
/// </summary>
public class TaskHistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _storeDir;

    public TaskHistoryStore(string? baseDir = null)
    {
        _storeDir = baseDir ?? DefaultStoreDir();
        Directory.CreateDirectory(_storeDir);
    }

    public string StoreDirectory => _storeDir;

    /// <summary>Default storage location: ~/.aicodeagent/tasks</summary>
    public static string DefaultStoreDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aicodeagent", "tasks");
    }

    /// <summary>Save (create or update) a task entry.</summary>
    public async Task SaveAsync(TaskHistoryEntry entry)
    {
        entry.UpdatedAt = DateTime.UtcNow;
        var path = Path.Combine(_storeDir, $"{entry.Id}.json");
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>Load a single task by id.</summary>
    public async Task<TaskHistoryEntry?> LoadAsync(string taskId)
    {
        var path = Path.Combine(_storeDir, $"{taskId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<TaskHistoryEntry>(json, JsonOpts);
    }

    /// <summary>List all saved tasks, newest first.</summary>
    public Task<List<TaskHistoryEntry>> ListAsync()
    {
        var entries = new List<TaskHistoryEntry>();
        foreach (var file in Directory.GetFiles(_storeDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<TaskHistoryEntry>(json, JsonOpts);
                if (entry != null) entries.Add(entry);
            }
            catch { /* skip corrupt files */ }
        }
        entries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return Task.FromResult(entries);
    }

    /// <summary>Delete a task by id.</summary>
    public Task DeleteAsync(string taskId)
    {
        var path = Path.Combine(_storeDir, $"{taskId}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Append a message to an existing task (or create it).</summary>
    public async Task AddMessageAsync(string taskId, string title, TaskMessageRecord message)
    {
        var entry = await LoadAsync(taskId) ?? new TaskHistoryEntry
        {
            Id = taskId,
            Title = title,
            CreatedAt = DateTime.UtcNow
        };
        if (string.IsNullOrEmpty(entry.Title) && !string.IsNullOrEmpty(title))
            entry.Title = title;
        entry.Messages.Add(message);
        await SaveAsync(entry);
    }

    /// <summary>Append a tool call record to an existing task.</summary>
    public async Task AddToolCallAsync(string taskId, TaskToolCallRecord toolCall)
    {
        var entry = await LoadAsync(taskId);
        if (entry == null) return;
        entry.ToolCalls.Add(toolCall);
        await SaveAsync(entry);
    }
}