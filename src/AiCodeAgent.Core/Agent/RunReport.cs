using System.Text;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Collects what an agent run did — every tool call (with outcome and duration), every approval
/// requested, every file changed — and renders it as a Markdown report the user can audit afterwards.
/// </summary>
public sealed class RunReportBuilder
{
    private readonly string _sessionId;
    private readonly string _prompt;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly List<(ToolCall Call, ToolResult Result, TimeSpan Duration)> _tools = new();
    private readonly List<(string Tool, string Risk)> _approvals = new();
    private readonly SortedSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private AgentResponse? _response;

    public RunReportBuilder(string sessionId, string prompt)
    {
        _sessionId = sessionId;
        _prompt = prompt;
    }

    public bool HasContent => _tools.Count > 0 || _response != null;

    public void Add(AgentEvent evt)
    {
        switch (evt)
        {
            case SessionScopedEvent scoped:
                Add(scoped.Inner);
                break;
            case AgentTaggedEvent tagged:
                Add(tagged.Inner);
                break;
            case ToolCallEndEvent end:
                _tools.Add((end.Call, end.Result, end.Duration));
                break;
            case ApprovalRequestEvent approval:
                _approvals.Add((approval.Call.Name, approval.Risk.ToString()));
                break;
            case DiffProducedEvent diff:
                _changedFiles.Add(diff.Diff.FilePath);
                break;
            case AgentFinishedEvent finished:
                _response = finished.Response;
                break;
        }
    }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Agent run report");
        sb.AppendLine();
        sb.AppendLine($"- Session: `{_sessionId}`");
        sb.AppendLine($"- Started (UTC): {_started:yyyy-MM-dd HH:mm:ss}");
        if (_response != null)
        {
            sb.AppendLine($"- Duration: {_response.Duration.TotalSeconds:F1}s");
            sb.AppendLine($"- Tokens: {_response.TotalUsage.PromptTokens} prompt / {_response.TotalUsage.CompletionTokens} completion");
            sb.AppendLine($"- Outcome: {(_response.WasCancelled ? "cancelled" : _response.StopReason ?? "completed")}");
        }
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine(_prompt);
        sb.AppendLine();

        sb.AppendLine($"## Tool calls ({_tools.Count})");
        sb.AppendLine();
        var i = 0;
        foreach (var (call, result, duration) in _tools)
        {
            i++;
            var args = string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={Shorten(kv.Value?.ToString(), 80)}"));
            sb.AppendLine($"{i}. `{call.Name}` ({args}) — {(result.IsError ? "ERROR" : "ok")}, {duration.TotalSeconds:F1}s");
            if (result.IsError)
                sb.AppendLine($"    - {Shorten(result.Content, 200).Replace("\n", " ")}");
        }
        sb.AppendLine();

        sb.AppendLine($"## Approvals requested ({_approvals.Count})");
        sb.AppendLine();
        foreach (var (tool, risk) in _approvals)
            sb.AppendLine($"- `{tool}` ({risk})");
        if (_approvals.Count == 0) sb.AppendLine("None.");
        sb.AppendLine();

        sb.AppendLine($"## Files changed ({_changedFiles.Count})");
        sb.AppendLine();
        foreach (var f in _changedFiles)
            sb.AppendLine($"- {f}");
        if (_changedFiles.Count == 0) sb.AppendLine("None.");

        return sb.ToString();
    }

    /// <summary>Writes the report under <c>~/.aiagent/runs/</c> (outside the project, so it never lands in version control).</summary>
    public async Task<string?> WriteAsync()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aiagent", "runs");
            Directory.CreateDirectory(dir);
            var safeSession = string.Concat(_sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var path = Path.Combine(dir, $"{_started:yyyyMMdd-HHmmss}-{safeSession}.md");
            await File.WriteAllTextAsync(path, Build()).ConfigureAwait(false);

            // Keep the folder bounded: newest 200 reports.
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.md").OrderByDescending(f => f.CreationTimeUtc).Skip(200))
            {
                try { old.Delete(); } catch { /* best effort */ }
            }
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string Shorten(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
