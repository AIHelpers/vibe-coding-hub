using System;
using System.Collections.Generic;
using System.Linq;

namespace AiCodeAgent.App.EditHistory;

/// <summary>
/// Maintains a per-file rolling buffer of the last N edits.
/// Used by the predictive next-edit autocomplete to provide recent-edit context.
/// </summary>
public sealed class EditHistoryTracker
{
    private const int DefaultCapacity = 20;

    private readonly int _capacity;
    private readonly Dictionary<string, LinkedList<EditRecord>> _historyByFile =
        new(StringComparer.OrdinalIgnoreCase);

    public EditHistoryTracker(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Records an edit for the given file, evicting the oldest when at capacity.</summary>
    public void Record(EditRecord record)
    {
        if (string.IsNullOrEmpty(record.FilePath))
            return;

        if (!_historyByFile.TryGetValue(record.FilePath, out var list))
        {
            list = new LinkedList<EditRecord>();
            _historyByFile[record.FilePath] = list;
        }

        list.AddLast(record);
        while (list.Count > _capacity)
        {
            list.RemoveFirst();
        }
    }

    /// <summary>Returns the most recent edits for a file, oldest-first.</summary>
    public IReadOnlyList<EditRecord> GetHistory(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !_historyByFile.TryGetValue(filePath, out var list))
            return Array.Empty<EditRecord>();
        return list.ToList();
    }

    /// <summary>Returns up to <paramref name="count"/> most-recent edits across all files.</summary>
    public IReadOnlyList<EditRecord> GetRecent(int count)
    {
        return _historyByFile.Values
            .SelectMany(l => l)
            .OrderByDescending(r => r.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>Clears the history for a file (e.g. on close or save-and-idle reset).</summary>
    public void Clear(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
            _historyByFile.Remove(filePath);
    }

    /// <summary>Clears all history.</summary>
    public void ClearAll() => _historyByFile.Clear();
}