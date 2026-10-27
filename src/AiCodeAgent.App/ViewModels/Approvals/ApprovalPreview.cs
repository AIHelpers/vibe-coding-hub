using System;
using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

public enum UnifiedDiffLineKind { Context, Removed, Added, HunkHeader, Gap }

/// <summary>One row of the compact unified diff in the approval dialog (old and new numbers in the gutter).</summary>
public sealed class UnifiedDiffLine
{
    // Same translucent colours as DiffRowViewModel, so the preview matches the full diff viewer.
    private const string RemovedBackground = "#33E05555";
    private const string AddedBackground = "#334CAF50";

    public UnifiedDiffLineKind Kind { get; init; }
    public int? OldNumber { get; init; }
    public int? NewNumber { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<DiffSegment>? Segments { get; init; }

    public bool IsLine => Kind is UnifiedDiffLineKind.Context or UnifiedDiffLineKind.Removed or UnifiedDiffLineKind.Added;
    public bool IsHunkHeader => Kind == UnifiedDiffLineKind.HunkHeader;
    public bool IsGap => Kind == UnifiedDiffLineKind.Gap;

    public string OldNumberText => OldNumber?.ToString() ?? string.Empty;
    public string NewNumberText => NewNumber?.ToString() ?? string.Empty;

    public string Marker => Kind switch
    {
        UnifiedDiffLineKind.Removed => "−",
        UnifiedDiffLineKind.Added => "+",
        _ => string.Empty
    };

    public string MarkerColor => Kind == UnifiedDiffLineKind.Removed ? "#E05555" : "#4CAF50";

    public string Background => Kind switch
    {
        UnifiedDiffLineKind.Removed => RemovedBackground,
        UnifiedDiffLineKind.Added => AddedBackground,
        UnifiedDiffLineKind.HunkHeader => "#204A9EFF",
        _ => "Transparent"
    };

    /// <summary>Colour behind the changed words of a modified line.</summary>
    public string HighlightBrush => Kind == UnifiedDiffLineKind.Removed ? "#80E05555" : "#804CAF50";

    public override string ToString() => Kind switch
    {
        UnifiedDiffLineKind.HunkHeader or UnifiedDiffLineKind.Gap => Text,
        _ => $"{OldNumberText,4} {NewNumberText,4} {(Marker.Length == 0 ? " " : Marker)} {Text}"
    };
}

public enum ApprovalPreviewKind { FileDiff, Command, Arguments }

/// <summary>What the approval dialog shows: a diff for file edits, the command for shell tools, raw arguments otherwise.</summary>
public sealed partial class ApprovalPreviewViewModel : ObservableObject
{
    public ApprovalPreviewKind Kind { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string Title => ToolCallCardFormatting.GetFriendlyName(ToolName);
    public string Subtitle { get; init; } = string.Empty;
    public string RawArguments { get; init; } = string.Empty;

    /// <summary>Set when the call would fail as it stands (e.g. old_string not found).</summary>
    public string? Error { get; init; }
    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsFileDiff => Kind == ApprovalPreviewKind.FileDiff;
    public bool IsCommand => Kind == ApprovalPreviewKind.Command;
    public bool IsArguments => Kind == ApprovalPreviewKind.Arguments;

    // ── file diff ──
    public string FilePath { get; init; } = string.Empty;
    public bool IsNewFile { get; init; }
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }
    public IReadOnlyList<UnifiedDiffLine> Lines { get; init; } = Array.Empty<UnifiedDiffLine>();
    public bool HasNoChanges => IsFileDiff && !HasError && AddedCount == 0 && RemovedCount == 0;

    public string ChangeSummary => IsNewFile
        ? $"New file · {AddedCount} line{(AddedCount == 1 ? "" : "s")}"
        : $"+{AddedCount} −{RemovedCount}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleLines))]
    [NotifyPropertyChangedFor(nameof(ShowAllText))]
    private bool _isShowingAll;

    /// <summary>The first <see cref="ApprovalPreviewBuilder.MaxCollapsedLines"/> lines, or all of them after "Show all".</summary>
    public IReadOnlyList<UnifiedDiffLine> VisibleLines =>
        IsShowingAll || Lines.Count <= ApprovalPreviewBuilder.MaxCollapsedLines
            ? Lines
            : Lines.Take(ApprovalPreviewBuilder.MaxCollapsedLines).ToList();

    public bool HasMoreLines => Lines.Count > ApprovalPreviewBuilder.MaxCollapsedLines;

    public string ShowAllText => IsShowingAll ? "Show less" : $"Show all {Lines.Count} lines";

    [RelayCommand]
    private void ToggleShowAll() => IsShowingAll = !IsShowingAll;

    // ── command ──
    public IReadOnlyList<string> Commands { get; init; } = Array.Empty<string>();
    public string CommandText => string.Join(Environment.NewLine, Commands);
    public string WorkingDirectory { get; init; } = string.Empty;
    public bool IsSandboxed { get; init; }
    public string SandboxText { get; init; } = string.Empty;
}

/// <summary>Turns a tool call and its dry-run <see cref="ToolPreview"/> into what the approval dialog shows.</summary>
public static class ApprovalPreviewBuilder
{
    /// <summary>Diff lines shown before "Show all".</summary>
    public const int MaxCollapsedLines = 30;

    public static ApprovalPreviewViewModel Build(ToolCall call, ToolPreview? preview)
    {
        var raw = FormatArguments(call.Arguments);
        var subtitle = ToolCallCardFormatting.ExtractSubtitle(call.Arguments);

        if (preview?.FileChange is { } change)
        {
            var diff = SideBySideDiff.Build(change.OldContent, change.NewContent, change.Path);
            return new ApprovalPreviewViewModel
            {
                Kind = ApprovalPreviewKind.FileDiff,
                ToolName = call.Name,
                Subtitle = change.Path,
                RawArguments = raw,
                Error = preview.Error,
                FilePath = change.Path,
                IsNewFile = change.IsNewFile,
                AddedCount = diff.AddedLines,
                RemovedCount = diff.RemovedLines,
                Lines = ToUnifiedLines(diff)
            };
        }

        if (preview?.Command is { } command)
        {
            return new ApprovalPreviewViewModel
            {
                Kind = ApprovalPreviewKind.Command,
                ToolName = call.Name,
                Subtitle = subtitle,
                RawArguments = raw,
                Error = preview.Error,
                Commands = command.Commands,
                WorkingDirectory = command.WorkingDirectory,
                IsSandboxed = command.SandboxEnabled,
                SandboxText = command.SandboxEnabled
                    ? $"Docker sandbox: on{(string.IsNullOrEmpty(command.SandboxDescription) ? "" : $" ({command.SandboxDescription})")}"
                    : "Docker sandbox: off · runs directly on this machine"
            };
        }

        return new ApprovalPreviewViewModel
        {
            Kind = ApprovalPreviewKind.Arguments,
            ToolName = call.Name,
            Subtitle = subtitle,
            RawArguments = raw,
            Error = preview?.Error
        };
    }

    /// <summary>
    /// Re-flows the split diff into a unified one: in each change block all removed lines come first, then the added
    /// ones, each with its own line number; context lines carry both numbers.
    /// </summary>
    public static IReadOnlyList<UnifiedDiffLine> ToUnifiedLines(SideBySideDiffResult diff)
    {
        var lines = new List<UnifiedDiffLine>();
        var removed = new List<UnifiedDiffLine>();
        var added = new List<UnifiedDiffLine>();

        void Flush()
        {
            lines.AddRange(removed);
            lines.AddRange(added);
            removed.Clear();
            added.Clear();
        }

        foreach (var row in diff.Rows)
        {
            switch (row.Kind)
            {
                case SideBySideRowKind.HunkHeader:
                    Flush();
                    lines.Add(new UnifiedDiffLine { Kind = UnifiedDiffLineKind.HunkHeader, Text = row.Header });
                    break;

                case SideBySideRowKind.Gap:
                    Flush();
                    lines.Add(new UnifiedDiffLine
                    {
                        Kind = UnifiedDiffLineKind.Gap,
                        Text = row.GapCount == 1 ? "⋯ 1 unchanged line" : $"⋯ {row.GapCount} unchanged lines"
                    });
                    break;

                default:
                    var isChange = row.OldKind == SideBySideCellKind.Removed || row.NewKind == SideBySideCellKind.Added;
                    if (!isChange)
                    {
                        Flush();
                        lines.Add(new UnifiedDiffLine
                        {
                            Kind = UnifiedDiffLineKind.Context,
                            OldNumber = row.OldNumber,
                            NewNumber = row.NewNumber,
                            Text = row.NewText
                        });
                        break;
                    }

                    if (row.OldKind == SideBySideCellKind.Removed)
                        removed.Add(new UnifiedDiffLine
                        {
                            Kind = UnifiedDiffLineKind.Removed,
                            OldNumber = row.OldNumber,
                            Text = row.OldText,
                            Segments = row.OldSegments
                        });
                    if (row.NewKind == SideBySideCellKind.Added)
                        added.Add(new UnifiedDiffLine
                        {
                            Kind = UnifiedDiffLineKind.Added,
                            NewNumber = row.NewNumber,
                            Text = row.NewText,
                            Segments = row.NewSegments
                        });
                    break;
            }
        }

        Flush();
        return lines;
    }

    /// <summary>One argument per line; long values are cut so the dialog stays readable.</summary>
    public static string FormatArguments(IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return "(no arguments)";
        return string.Join(Environment.NewLine, args.Select(kv =>
        {
            var value = kv.Value?.ToString() ?? "null";
            if (value.Length > 2000) value = value[..2000] + " …";
            return $"{kv.Key}: {value}";
        }));
    }
}

/// <summary>
/// The user's standing approval decisions: "approve all edits this task" (reset when the task ends; commands still
/// ask) and "always allow &lt;tool&gt; this session".
/// </summary>
public sealed class ApprovalPolicy
{
    /// <summary>Tools that only change files in the workspace; "approve all edits" covers exactly these.</summary>
    public static readonly IReadOnlySet<string> EditTools =
        new HashSet<string>(StringComparer.Ordinal) { "edit_file", "multi_edit", "apply_patch", "write_file" };

    private readonly HashSet<string> _sessionTools = new(StringComparer.Ordinal);

    public bool EditsAllowedThisTask { get; private set; }

    public IReadOnlyCollection<string> SessionAllowedTools => _sessionTools;

    public static bool IsEditTool(string? toolName) => toolName != null && EditTools.Contains(toolName);

    public void AllowEditsForTask() => EditsAllowedThisTask = true;

    public void AllowToolForSession(string toolName)
    {
        if (!string.IsNullOrWhiteSpace(toolName)) _sessionTools.Add(toolName);
    }

    /// <summary>The task finished (or was cancelled): edits ask again; per-tool session rules stay.</summary>
    public void EndTask() => EditsAllowedThisTask = false;

    /// <summary>A new chat: forget everything.</summary>
    public void ResetSession()
    {
        EditsAllowedThisTask = false;
        _sessionTools.Clear();
    }

    /// <summary>True when <paramref name="toolName"/> needs no dialog; <paramref name="reason"/> says which rule applied.</summary>
    public bool TryAutoApprove(string toolName, out string reason)
    {
        if (_sessionTools.Contains(toolName))
        {
            reason = "always allowed this session";
            return true;
        }
        if (EditsAllowedThisTask && IsEditTool(toolName))
        {
            reason = "edits allowed for this task";
            return true;
        }
        reason = string.Empty;
        return false;
    }
}
