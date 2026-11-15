using System.Text.Json.Nodes;

namespace AiCodeAgent.Core.Evidence;

/// <summary>
/// Replays the successful <c>acceptance_criteria</c> calls of a run, in order, into the criteria that were declared and the last
/// mark on each. The tool refuses bad calls, so only calls that succeeded are replayed; the rules here must match the tool's.
/// </summary>
internal static class AcceptanceReplay
{
    public sealed record Call(int Seq, JsonNode? Arguments);

    public static (List<DeclaredCriterion> Declared, Dictionary<string, CriterionMark> Marks, int? FirstDeclareSeq) Replay(IEnumerable<Call> calls)
    {
        var declared = new List<DeclaredCriterion>();
        var marks = new Dictionary<string, CriterionMark>(StringComparer.OrdinalIgnoreCase);
        int? firstDeclare = null;

        foreach (var call in calls.OrderBy(c => c.Seq))
        {
            if (call.Arguments is not JsonObject args) continue;
            switch (Str(args, "action")?.Trim().ToLowerInvariant())
            {
                case "declare":
                    var texts = Strings(args["criteria"]);
                    if (texts.Count == 0) break;
                    firstDeclare ??= call.Seq;
                    declared.Clear();
                    marks.Clear();
                    for (var i = 0; i < texts.Count; i++) declared.Add(new DeclaredCriterion(IdOf(i), texts[i]));
                    break;

                case "mark":
                    if (args["marks"] is not JsonArray array) break;
                    foreach (var node in array)
                    {
                        if (node is not JsonObject m || Str(m, "id") is not { } id) continue;
                        var known = declared.FirstOrDefault(d => string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (known == null) continue;
                        var status = Str(m, "status")?.Trim().ToLowerInvariant() == "not_met" ? "not_met" : "met";
                        marks[known.Id] = new CriterionMark(known.Id, status, Strings(m["tests"]), Strings(m["files"]), Strings(m["commands"]), Str(m, "note"));
                    }
                    break;
            }
        }
        return (declared, marks, firstDeclare);
    }

    /// <summary>The id the tool gives the criterion at this position (AC1, AC2…).</summary>
    public static string IdOf(int index) => "AC" + (index + 1);

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static List<string> Strings(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray a)
            foreach (var item in a)
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        return list;
    }
}
