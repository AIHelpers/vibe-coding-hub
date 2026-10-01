using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Live "what is the agent doing right now" state for the chat: a human-readable current activity,
/// total elapsed time for the turn, elapsed time of the current step, and a short list of finished steps.
/// Pure state + a clock: the owner calls <see cref="Tick"/> once per second (a DispatcherTimer in the chat view-model).
/// </summary>
public sealed partial class AgentActivityTracker : ObservableObject
{
    public const int MaxRecentSteps = 6;
    public const string ThinkingText = "Thinking…";

    private readonly Func<DateTime> _clock;
    private DateTime _turnStart;
    private DateTime _stepStart;
    private string _phase = ThinkingText;
    private string? _blocking;
    // Insertion-ordered list of tool calls currently running.
    private readonly List<RunningTool> _running = new();

    private sealed record RunningTool(string Id, string Text, DateTime Start);

    public AgentActivityTracker(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _currentActivity = string.Empty;
    [ObservableProperty] private string _elapsedText = "0s";
    [ObservableProperty] private string _stepElapsedText = string.Empty;

    /// <summary>Most recent finished steps, newest first (e.g. "✓ Reading Foo.cs · 0.4s").</summary>
    public ObservableCollection<string> RecentSteps { get; } = new();

    public bool HasRecentSteps => RecentSteps.Count > 0;

    public void Begin()
    {
        _turnStart = _stepStart = _clock();
        _phase = ThinkingText;
        _blocking = null;
        _running.Clear();
        RecentSteps.Clear();
        OnPropertyChanged(nameof(HasRecentSteps));
        IsActive = true;
        Refresh();
    }

    public void End()
    {
        _running.Clear();
        _blocking = null;
        IsActive = false;
        CurrentActivity = string.Empty;
        StepElapsedText = string.Empty;
    }

    /// <summary>A general phase such as "Thinking…" or "Writing response…". Ignored while a tool is running or blocking.</summary>
    public void SetPhase(string text)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(text)) return;
        if (_blocking != null || _running.Count > 0) return;
        if (_phase == text) return;
        _phase = text;
        _stepStart = _clock();
        Refresh();
    }

    /// <summary>Shows a "waiting for you" line (approval, question) until the next tool/phase change.</summary>
    public void SetBlocking(string text)
    {
        if (!IsActive) return;
        _blocking = text;
        _stepStart = _clock();
        Refresh();
    }

    public void ClearBlocking()
    {
        if (_blocking == null) return;
        _blocking = null;
        _stepStart = _clock();
        Refresh();
    }

    public void ToolStarted(string id, string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        if (!IsActive) return;
        _blocking = null;
        _running.Add(new RunningTool(id, DescribeTool(toolName, args), _clock()));
        _stepStart = _clock();
        Refresh();
    }

    public void ToolFinished(string id, string toolName, bool isError)
    {
        if (!IsActive) return;
        // Match by id; fall back to the oldest running call.
        var running = _running.FirstOrDefault(r => r.Id == id) ?? _running.FirstOrDefault();
        if (running != null)
        {
            _running.Remove(running);
            var took = FormatShort(_clock() - running.Start);
            RecentSteps.Insert(0, $"{(isError ? "✗" : "✓")} {running.Text} · {took}");
            while (RecentSteps.Count > MaxRecentSteps) RecentSteps.RemoveAt(RecentSteps.Count - 1);
            OnPropertyChanged(nameof(HasRecentSteps));
        }
        _blocking = null;
        _phase = ThinkingText;
        _stepStart = _clock();
        Refresh();
    }

    /// <summary>Call about once a second while active.</summary>
    public void Tick()
    {
        if (!IsActive) return;
        Refresh();
    }

    private void Refresh()
    {
        var now = _clock();
        ElapsedText = FormatElapsed(now - _turnStart);

        string activity;
        if (_blocking != null) activity = _blocking;
        else if (_running.Count > 0)
        {
            var last = _running[^1];
            activity = _running.Count > 1 ? $"{last.Text} (+{_running.Count - 1} more)" : last.Text;
        }
        else activity = _phase;

        CurrentActivity = activity;
        StepElapsedText = FormatElapsed(now - _stepStart);
    }

    // ---- formatting helpers (public/static for tests) ----

    /// <summary>"7s", "1m 05s", "1h 02m".</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalSeconds}s";
    }

    /// <summary>Sub-second precision for finished steps: "0.4s", "12s", "1m 05s".</summary>
    public static string FormatShort(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 10) return $"{t.TotalSeconds:0.0}s";
        return FormatElapsed(t);
    }

    /// <summary>Plain-language description of a tool call: "Reading Foo.cs", "Running: dotnet build".</summary>
    public static string DescribeTool(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        string? Arg(params string[] keys)
        {
            if (args == null) return null;
            foreach (var k in keys)
            {
                if (args.TryGetValue(k, out var v) && v != null)
                {
                    var s = v.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                }
            }
            return null;
        }

        static string Short(string s, int max = 60)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s[..(max - 1)] + "…";
        }

        static string FileName(string p)
        {
            var t = p.TrimEnd('/', '\\');
            var i = Math.Max(t.LastIndexOf('/'), t.LastIndexOf('\\'));
            return i >= 0 && i < t.Length - 1 ? t[(i + 1)..] : t;
        }

        var path = Arg("path", "file_path", "filePath", "file", "target_file");
        var shown = path != null ? FileName(path) : null;

        return toolName switch
        {
            "read_file" => shown != null ? $"Reading {shown}" : "Reading a file",
            "write_file" => shown != null ? $"Writing {shown}" : "Writing a file",
            "edit_file" or "multi_edit" => shown != null ? $"Editing {shown}" : "Editing a file",
            "apply_patch" => "Applying a patch",
            "list_directory" => shown != null ? $"Listing {shown}" : "Listing a folder",
            "glob" => Arg("pattern") is { } g ? $"Finding files: {Short(g)}" : "Finding files",
            "grep" => Arg("pattern", "query") is { } q ? $"Searching for “{Short(q, 40)}”" : "Searching the code",
            "execute_command" => Arg("command", "cmd") is { } c ? $"Running: {Short(c)}" : "Running a command",
            "git" => Arg("command", "args", "action", "operation") is { } gc ? $"Running git {Short(gc, 40)}" : "Running git",
            "web_fetch" => Arg("url") is { } u ? $"Fetching {Short(u)}" : "Fetching a web page",
            "ask_user" => "Waiting for your answer",
            "spawn_subagent" => "Running a sub-agent",
            "verify_changes" => "Verifying changes",
            "run_diagnostics" or "get_diagnostics" => "Checking for errors",
            "find_references" => "Finding references",
            "go_to_definition" => "Finding a definition",
            "todo_write" => "Updating the to-do list",
            "browser_check" => "Checking the page in a browser",
            "scaffold_backend" => "Scaffolding a backend",
            _ => $"Running {toolName}"
        };
    }
}
