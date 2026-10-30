using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCodeAgent.Core.Audit;

public enum AuditExportFormat { Markdown, Json, Zip }

/// <summary>One line of an audit file, parsed.</summary>
public sealed record AuditLine(int Seq, string Hash, JsonObject Entry)
{
    public string Kind => Entry["kind"]?.GetValue<string>() ?? "";
    public string? Agent => Entry["agent"]?.GetValue<string>();
    public JsonObject Data => Entry["data"] as JsonObject ?? new JsonObject();
    public string At => Entry["at"]?.GetValue<string>() ?? "";
    public string? Str(string field) => Data[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : Data[field]?.ToJsonString();
}

/// <summary>
/// Renders a run's audit file as Markdown, JSON or a zip (E7-S2). The zip holds <c>audit.jsonl</c> exactly as written (so
/// <c>aiagent audit verify</c> works on the export), the Markdown report, the verification result and one <c>.diff</c> per file change.
/// </summary>
public static class AuditExporter
{
    public static IReadOnlyList<AuditLine> Read(string path)
    {
        var lines = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
            ? AuditFiles.ReadZipLines(path)
            : AuditFiles.ReadLines(path);
        return Parse(lines);
    }

    public static IReadOnlyList<AuditLine> Parse(IEnumerable<string> lines)
    {
        var result = new List<AuditLine>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            try
            {
                var node = JsonNode.Parse(line)?.AsObject();
                if (node?["entry"] is JsonObject entry)
                    result.Add(new AuditLine(node["seq"]?.GetValue<int>() ?? result.Count + 1, node["hash"]?.GetValue<string>() ?? "", entry));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* verify reports damaged lines */ }
        }
        return result;
    }

    public static string DefaultExtension(AuditExportFormat format) => format switch
    {
        AuditExportFormat.Markdown => ".md",
        AuditExportFormat.Json => ".json",
        _ => ".zip"
    };

    public static bool TryParseFormat(string? text, out AuditExportFormat format)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "md": case "markdown": format = AuditExportFormat.Markdown; return true;
            case "json": format = AuditExportFormat.Json; return true;
            case "zip": format = AuditExportFormat.Zip; return true;
            default: format = default; return false;
        }
    }

    /// <summary>Writes the export of <paramref name="auditPath"/> to <paramref name="outputPath"/> and returns the verification result.</summary>
    public static AuditVerification Export(string auditPath, string outputPath, AuditExportFormat format)
    {
        var verification = AuditChainVerifier.Verify(auditPath);
        var entries = Read(auditPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        switch (format)
        {
            case AuditExportFormat.Markdown:
                File.WriteAllText(outputPath, ToMarkdown(entries, verification), new UTF8Encoding(false));
                break;
            case AuditExportFormat.Json:
                File.WriteAllText(outputPath, ToJson(entries, verification), new UTF8Encoding(false));
                break;
            default:
                WriteZip(auditPath, outputPath, entries, verification);
                break;
        }
        return verification;
    }

    /// <summary>
    /// Exports several runs (e.g. every task of one chat session) into one file. Zip: one folder per run with its own
    /// <c>audit.jsonl</c> (verifiable on its own), report and diffs. Markdown: the reports one after the other.
    /// JSON: one export object per run.
    /// </summary>
    public static IReadOnlyList<AuditVerification> ExportMany(IReadOnlyList<string> auditPaths, string outputPath, AuditExportFormat format)
    {
        var results = new List<AuditVerification>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        switch (format)
        {
            case AuditExportFormat.Zip:
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
                foreach (var path in auditPaths)
                {
                    var v = AuditChainVerifier.Verify(path);
                    results.Add(v);
                    AddRun(zip, AuditLocator.Id(path) + "/", path, Read(path), v);
                }
                break;
            }
            case AuditExportFormat.Json:
            {
                var array = new JsonArray();
                foreach (var path in auditPaths)
                {
                    var v = AuditChainVerifier.Verify(path);
                    results.Add(v);
                    var one = JsonNode.Parse(ToJson(Read(path), v))!.AsObject();
                    one["run"] = AuditLocator.Id(path);
                    array.Add(one);
                }
                File.WriteAllText(outputPath, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                break;
            }
            default:
            {
                var sb = new StringBuilder();
                foreach (var path in auditPaths)
                {
                    var v = AuditChainVerifier.Verify(path);
                    results.Add(v);
                    sb.AppendLine(ToMarkdown(Read(path), v));
                    sb.AppendLine("\n---\n");
                }
                File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
                break;
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ JSON

    public static string ToJson(IReadOnlyList<AuditLine> entries, AuditVerification verification)
    {
        var root = new JsonObject
        {
            ["format"] = "vch-audit-export/1",
            ["verification"] = VerificationNode(verification),
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject
            {
                ["seq"] = e.Seq,
                ["hash"] = e.Hash,
                ["entry"] = e.Entry.DeepClone()
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject VerificationNode(AuditVerification v) => new()
    {
        ["ok"] = v.Ok,
        ["lines"] = v.Lines,
        ["headHash"] = v.HeadHash,
        ["closed"] = v.HasEndRecord,
        ["failedLine"] = v.Failure?.Line,
        ["problem"] = v.Failure?.Problem,
        ["warnings"] = new JsonArray(v.Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray())
    };

    // ------------------------------------------------------------------ Markdown

    public static string ToMarkdown(IReadOnlyList<AuditLine> entries, AuditVerification verification)
    {
        var sb = new StringBuilder();
        var start = entries.FirstOrDefault(e => e.Kind == "run.start");
        var end = entries.LastOrDefault(e => e.Kind == "run.end");
        var title = start?.Str("name") ?? "run";
        sb.AppendLine($"# Audit report — {title}");
        sb.AppendLine();
        sb.AppendLine($"- Run: `{start?.Str("runId") ?? "?"}` ({start?.Str("runKind") ?? "?"})");
        sb.AppendLine($"- User: {start?.Entry["user"]?.GetValue<string>() ?? "?"}" + (start?.Str("machine") is { } m ? $" on {m}" : ""));
        sb.AppendLine($"- Started (UTC): {start?.At ?? "?"}");
        if (end != null)
        {
            sb.AppendLine($"- Finished (UTC): {end.At} — {end.Str("status")}" + (string.IsNullOrEmpty(end.Str("message")) ? "" : $": {end.Str("message")}"));
            sb.AppendLine($"- Tokens: {end.Str("tokens")} · Cost: {FormatCost(end.Data["costUsd"])}");
        }
        else
        {
            sb.AppendLine("- Finished: no closing entry (still running, interrupted, or cut off)");
        }
        var models = entries.Select(e => e.Entry["model"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        if (models.Count > 0) sb.AppendLine($"- Models: {string.Join(", ", models.Select(x => $"`{x}`"))}");
        var characters = entries.Select(e => e.Entry["character"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        if (characters.Count > 0) sb.AppendLine($"- Characters: {string.Join(", ", characters.Select(x => $"`{x}`"))}");
        if (start?.Str("permissionMode") is { Length: > 0 } pm) sb.AppendLine($"- Permission mode: {pm}");
        sb.AppendLine($"- Entries: {verification.Lines} · Head hash: `{verification.HeadHash}`");
        sb.AppendLine(verification.Ok
            ? "- Hash chain: **verified**"
            : $"- Hash chain: **BROKEN at line {verification.Failure!.Line}** — {verification.Failure.Problem}");
        foreach (var w in verification.Warnings) sb.AppendLine($"- Note: {w}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(start?.Str("task")))
        {
            sb.AppendLine("## Task");
            sb.AppendLine();
            sb.AppendLine(start!.Str("task"));
            sb.AppendLine();
        }

        var steps = entries.Where(e => e.Kind == "step.end").ToList();
        if (steps.Count > 0)
        {
            sb.AppendLine($"## Steps ({steps.Count})");
            sb.AppendLine();
            sb.AppendLine("| Step | Round | Character | Model | Result | Tokens | Cost | Duration |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var s in steps)
                sb.AppendLine($"| {Cell(s.Str("step"))} | {Cell(s.Str("round"))} | {Cell(s.Entry["character"]?.GetValue<string>())} | {Cell(s.Entry["model"]?.GetValue<string>())} | {Cell(s.Str("state") + (s.Str("outcome") is { Length: > 0 } o ? " → " + o : ""))} | {Cell(s.Str("tokens"))} | {FormatCost(s.Data["costUsd"])} | {Duration(s.Data["durationMs"])} |");
            sb.AppendLine();
        }

        var requests = entries.Where(e => e.Kind == "approval.request").ToList();
        var decisions = entries.Where(e => e.Kind == "approval.decision").ToList();
        sb.AppendLine($"## Approvals ({requests.Count})");
        sb.AppendLine();
        if (requests.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Time | Agent | Tool | Risk | Decision | Decided by | Note |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in requests)
            {
                var d = decisions.FirstOrDefault(x => x.Str("id") == r.Str("id") && x.Seq > r.Seq);
                sb.AppendLine($"| {Cell(r.At)} | {Cell(r.Agent)} | `{Cell(r.Str("tool"))}` | {Cell(r.Str("risk"))} | {Cell(d?.Str("decision") ?? "unanswered")} | {Cell(d?.Str("decidedBy"))} | {Cell(d?.Str("note"))} |");
            }
        }
        sb.AppendLine();

        var changes = entries.Where(e => e.Kind == "file.change").ToList();
        sb.AppendLine($"## Files changed ({changes.Count})");
        sb.AppendLine();
        if (changes.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Time | Agent | File | Before (SHA-256) | After (SHA-256) |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in changes)
                sb.AppendLine($"| {Cell(c.At)} | {Cell(c.Agent)} | {Cell(c.Str("path"))} | `{Short(c.Str("beforeSha256"))}` | `{Short(c.Str("afterSha256"))}` |");
        }
        sb.AppendLine();

        var calls = entries.Where(e => e.Kind == "tool.call").ToList();
        var results = entries.Where(e => e.Kind == "tool.result").ToList();
        sb.AppendLine($"## Tool calls ({calls.Count})");
        sb.AppendLine();
        if (calls.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| # | Time | Agent | Tool | Arguments | Result | Duration |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            var i = 0;
            foreach (var c in calls)
            {
                i++;
                var r = results.FirstOrDefault(x => x.Str("id") == c.Str("id") && x.Seq > c.Seq);
                var args = c.Data["arguments"]?.ToJsonString() ?? "";
                sb.AppendLine($"| {i} | {Cell(c.At)} | {Cell(c.Agent)} | `{Cell(c.Str("tool"))}` | {Cell(Shorten(args, 120))} | {(r == null ? "no result" : r.Data["isError"]?.GetValue<bool>() == true ? "error" : "ok")} | {Duration(r?.Data["durationMs"])} |");
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ zip

    private static void WriteZip(string auditPath, string outputPath, IReadOnlyList<AuditLine> entries, AuditVerification verification)
    {
        if (File.Exists(outputPath)) File.Delete(outputPath);
        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        AddRun(zip, "", auditPath, entries, verification);
    }

    private static void AddRun(ZipArchive zip, string prefix, string auditPath, IReadOnlyList<AuditLine> entries, AuditVerification verification)
    {
        if (string.Equals(Path.GetExtension(auditPath), ".zip", StringComparison.OrdinalIgnoreCase))
            AddText(zip, prefix + AuditChain.FileName, string.Join("\n", AuditFiles.ReadZipLines(auditPath)) + "\n");
        else
            AddText(zip, prefix + AuditChain.FileName, AuditFiles.ReadAllText(auditPath));
        AddText(zip, prefix + "audit.md", ToMarkdown(entries, verification));
        AddText(zip, prefix + "verification.json", VerificationNode(verification).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var change in entries.Where(e => e.Kind == "file.change" && !string.IsNullOrEmpty(e.Str("diff"))))
        {
            var name = $"{prefix}diffs/{change.Seq:D4}-{SafeFileName(change.Str("path") ?? "file")}.diff";
            AddText(zip, name, change.Str("diff")! + (change.Data["diffTruncated"]?.GetValue<bool>() == true ? "\n\\ diff truncated in the audit file\n" : ""));
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string SafeFileName(string path)
    {
        var chars = path.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "file" : s.Length > 80 ? s[^80..] : s;
    }

    // ------------------------------------------------------------------ small helpers

    private static string Cell(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "" : hash.Length > 12 ? hash[..12] : hash;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string FormatCost(JsonNode? node) =>
        node != null && decimal.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? "$" + d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : "n/a";

    private static string Duration(JsonNode? ms) =>
        ms != null && long.TryParse(ms.ToJsonString(), out var v) ? (v / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" : "";
}

/// <summary>Finds the audit file of a run from what the user types: a path, a run id, or part of a chat run's file name.</summary>
public sealed class AuditLocator
{
    private readonly string _runsRoot;
    private readonly string _flowRoot;

    public AuditLocator(string? runsRoot = null, string? flowRoot = null)
    {
        _runsRoot = runsRoot ?? AiCodeAgent.Core.Configuration.AppPaths.Runs;
        _flowRoot = flowRoot ?? (runsRoot == null ? AiCodeAgent.Core.Configuration.AppPaths.FlowRuns : Path.Combine(runsRoot, "flows"));
    }

    public const string ChatSuffix = ".audit.jsonl";

    public static string ChatFilePath(string runsRoot, DateTime startedUtc, string sessionId, int attempt = 1)
    {
        var safe = string.Concat(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(runsRoot, $"{startedUtc:yyyyMMdd-HHmmss}-{safe}{(attempt > 1 ? "-" + attempt : "")}{ChatSuffix}");
    }

    /// <summary>Every audit file under the runs folder, newest first.</summary>
    public IReadOnlyList<string> List()
    {
        var found = new List<string>();
        if (Directory.Exists(_runsRoot))
            found.AddRange(Directory.EnumerateFiles(_runsRoot, "*" + ChatSuffix));
        if (Directory.Exists(_flowRoot))
            found.AddRange(Directory.EnumerateFiles(_flowRoot, AuditChain.FileName, SearchOption.AllDirectories));
        return found.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
    }

    /// <summary>Every audit file of one chat session (one per task), oldest first.</summary>
    public IReadOnlyList<string> ForSession(string sessionId)
    {
        var safe = string.Concat(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var pattern = new System.Text.RegularExpressions.Regex($@"^\d{{8}}-\d{{6}}-{System.Text.RegularExpressions.Regex.Escape(safe)}(-\d+)?{System.Text.RegularExpressions.Regex.Escape(ChatSuffix)}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return Directory.Exists(_runsRoot)
            ? Directory.EnumerateFiles(_runsRoot, "*" + ChatSuffix).Where(f => pattern.IsMatch(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    /// <summary>The audit file for <paramref name="run"/>, or the candidates when it is ambiguous / missing.</summary>
    public (string? Path, IReadOnlyList<string> Candidates) Resolve(string run)
    {
        if (string.IsNullOrWhiteSpace(run)) return (null, Array.Empty<string>());
        run = run.Trim();
        if (File.Exists(run)) return (Path.GetFullPath(run), Array.Empty<string>());
        if (Directory.Exists(run))
        {
            var inside = Path.Combine(run, AuditChain.FileName);
            if (File.Exists(inside)) return (Path.GetFullPath(inside), Array.Empty<string>());
        }
        var matches = List().Where(f => Id(f).Contains(run, StringComparison.OrdinalIgnoreCase)).ToList();
        // An exact run id / file stem beats a partial match.
        var exact = matches.Where(f => string.Equals(Id(f), run, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0], Array.Empty<string>());
        return matches.Count == 1 ? (matches[0], Array.Empty<string>()) : (null, matches);
    }

    /// <summary>The id of a run as shown in <c>audit list</c>: the run folder name for flows, the file stem for chat runs.</summary>
    public static string Id(string auditFile) =>
        string.Equals(Path.GetFileName(auditFile), AuditChain.FileName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(auditFile)) ?? auditFile
            : Path.GetFileName(auditFile).Replace(ChatSuffix, "", StringComparison.OrdinalIgnoreCase);

}
