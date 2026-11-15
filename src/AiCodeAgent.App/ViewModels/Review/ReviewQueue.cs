using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>Somewhere things wait for a human: it reports what is pending right now and says when that changed.</summary>
public interface IReviewSource
{
    string Name { get; }

    /// <summary>Everything waiting now. Called often, so it must be cheap and must not change anything.</summary>
    IReadOnlyList<ReviewItem> Pending();

    event EventHandler? Changed;
}

/// <summary>
/// One list of everything waiting for a human (E10-S3): approvals, flow gates and decisions, finished tasks. The queue
/// owns no state of its own. Each source says what is pending and the queue merges those answers, so an item leaves the
/// list no matter how it was answered (here, in the approval dialog, in the Flows inspector, or by stopping the run).
/// </summary>
public sealed partial class ReviewQueue : ObservableObject
{
    private readonly List<IReviewSource> _sources = new();
    private readonly Action<Action> _marshal;

    /// <param name="marshal">Runs an action on the UI thread; sources may report changes from any thread.</param>
    public ReviewQueue(Action<Action>? marshal = null) => _marshal = marshal ?? (a => a());

    /// <summary>Blocking items first (an agent is waiting), then oldest first.</summary>
    public ObservableCollection<ReviewItem> Items { get; } = new();

    public int Count => Items.Count;
    public int BlockingCount => Items.Count(i => i.BlocksAgent);

    public void AddSource(IReviewSource source)
    {
        _sources.Add(source);
        source.Changed += (_, _) => _marshal(Refresh);
        Refresh();
    }

    /// <summary>Asks every source what is pending and updates the list in place, so a list control keeps its selection and scroll position.</summary>
    public void Refresh()
    {
        var wanted = _sources.SelectMany(s => s.Pending())
            .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(i => i.BlocksAgent ? 0 : 1).ThenBy(i => i.Since).ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        // An item that looks the same as before stays the very same object.
        var final = wanted.Select(w =>
            Items.FirstOrDefault(old => old.Id == w.Id && old.Signature == w.Signature) ?? w).ToList();

        for (var i = Items.Count - 1; i >= 0; i--)
            if (!final.Contains(Items[i])) Items.RemoveAt(i);
        for (var i = 0; i < final.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], final[i])) continue;
            var at = Items.IndexOf(final[i]);
            if (at >= 0) Items.Move(at, i);
            else Items.Insert(i, final[i]);
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(BlockingCount));
    }

    public ReviewResult Approve(ReviewItem item) => Run(item.Approve);

    public ReviewResult RequestChanges(ReviewItem item, string comment)
    {
        if (item.RequestChanges == null) return ReviewResult.Refused("This item has no way to send a comment. Answer it another way.");
        if (string.IsNullOrWhiteSpace(comment)) return ReviewResult.Refused("Write what should change.");
        return Run(() => item.RequestChanges(comment.Trim()));
    }

    public ReviewResult Decline(ReviewItem item) =>
        item.Decline == null ? ReviewResult.Refused("This item cannot be declined.") : Run(item.Decline);

    private ReviewResult Run(Func<ReviewResult> action)
    {
        var result = action();
        Refresh();
        return result;
    }
}
