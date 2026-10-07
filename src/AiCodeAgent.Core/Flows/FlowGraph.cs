using System.Text;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;

namespace AiCodeAgent.Core.Flows;

/// <summary>A stage of a flow, with its resolved id.</summary>
public sealed record FlowNode(string Id, SdlcStageDefinition Stage, int Index)
{
    /// <summary>Display name: the stage name, else the id.</summary>
    public string Name => string.IsNullOrWhiteSpace(Stage.Name) ? Id : Stage.Name;

    /// <summary>Declared outcomes (empty when the stage routes unconditionally).</summary>
    public IReadOnlyList<string> Outcomes => Stage.Outcomes ?? (IReadOnlyList<string>)Array.Empty<string>();
}

/// <summary>An edge of a flow. <see cref="IsLoop"/> edges go back to an earlier node and are bounded by MaxLoops.</summary>
public sealed record FlowGraphEdge(int Index, string From, string To, string? When, int MaxLoops, bool IsLoop)
{
    public bool IsConditional => !string.IsNullOrEmpty(When);
    public string Label => IsLoop ? $"{When ?? "always"} ↺{MaxLoops}" : When ?? string.Empty;
}

public enum FlowProblemSeverity { Warning, Error }

/// <summary>A validation problem, attached to a node and/or an edge so the canvas can highlight it.</summary>
public sealed record FlowProblem(FlowProblemSeverity Severity, string Message, string? NodeId = null, int? EdgeIndex = null)
{
    public bool IsError => Severity == FlowProblemSeverity.Error;
    public override string ToString() => (IsError ? "error: " : "warning: ") + Message;
}

/// <summary>
/// The dependency graph of a pipeline. Linear pipelines (no edges) become a chain. Forward edges
/// must form a DAG; an edge that closes a cycle must declare <c>maxLoops</c> and is treated as a
/// loop edge (not a dependency). <see cref="Waves"/> lists which nodes can run at the same time.
/// </summary>
public sealed class FlowGraph
{
    /// <summary>Edge condition used when a loop edge has run out of loops.</summary>
    public const string LoopExhausted = "loop-exhausted";

    private readonly Dictionary<string, FlowNode> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FlowProblem> _problems = new();

    public SdlcPipelineDefinition Definition { get; }
    public IReadOnlyList<FlowNode> Nodes { get; }
    public IReadOnlyList<FlowGraphEdge> Edges { get; private set; } = Array.Empty<FlowGraphEdge>();
    public IReadOnlyList<FlowProblem> Problems => _problems;
    public bool IsValid => _problems.All(p => !p.IsError);

    /// <summary>True when the edges were implied by stage order (a linear pipeline).</summary>
    public bool IsImplicitChain { get; }

    /// <summary>Nodes grouped by execution wave: wave N runs after every node in waves &lt; N it depends on.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Waves { get; private set; } = Array.Empty<IReadOnlyList<string>>();

    private FlowGraph(SdlcPipelineDefinition definition)
    {
        Definition = definition;
        var nodes = new List<FlowNode>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < definition.Stages.Count; i++)
        {
            var stage = definition.Stages[i];
            var id = stage.Id?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                id = DeriveId(stage, used);
            }
            else if (!used.Add(id))
            {
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"Duplicate node id '{id}'.", id));
                id = DeriveId(stage, used);
            }
            if (stage.Id != null && !LocalFileStore.IsValidName(stage.Id.Trim()))
                _problems.Add(new FlowProblem(FlowProblemSeverity.Warning, $"Node id '{stage.Id}' is not kebab-case.", id));
            var node = new FlowNode(id, stage, i);
            nodes.Add(node);
            _byId[id] = node;
        }
        Nodes = nodes;
        IsImplicitChain = !definition.IsFlow;
    }

    private static string DeriveId(SdlcStageDefinition stage, HashSet<string> used)
    {
        var baseId = LocalFileStore.ToKebab(string.IsNullOrWhiteSpace(stage.Name) ? stage.Character ?? stage.Role : stage.Name);
        if (string.IsNullOrEmpty(baseId)) baseId = "stage";
        var id = baseId;
        for (var n = 2; !used.Add(id); n++) id = $"{baseId}-{n}";
        return id;
    }

    public FlowNode? GetNode(string id) => _byId.TryGetValue(id, out var n) ? n : null;

    /// <summary>Forward (dependency) edges into <paramref name="nodeId"/>.</summary>
    public IEnumerable<FlowGraphEdge> Incoming(string nodeId) =>
        Edges.Where(e => !e.IsLoop && string.Equals(e.To, nodeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>All edges (forward and loop) out of <paramref name="nodeId"/>.</summary>
    public IEnumerable<FlowGraphEdge> Outgoing(string nodeId) =>
        Edges.Where(e => string.Equals(e.From, nodeId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<FlowProblem> ProblemsFor(string nodeId) =>
        _problems.Where(p => string.Equals(p.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Build and validate the graph. <paramref name="characterLookup"/> (optional) checks that
    /// referenced characters exist and are not templates; <paramref name="cast"/> overrides
    /// characters by stage name or role, as <c>pipeline run --cast</c> does.
    /// </summary>
    public static FlowGraph Build(
        SdlcPipelineDefinition definition,
        Func<string, CharacterInfo?>? characterLookup = null,
        IReadOnlyDictionary<string, string>? cast = null)
    {
        var graph = new FlowGraph(definition);
        graph.BuildEdges();
        graph.ValidateNodes(characterLookup, cast);
        graph.ComputeWaves();
        return graph;
    }

    /// <summary>Character id a node runs as: cast (by stage name, then role) &gt; the stage's character.</summary>
    public static string? EffectiveCharacter(SdlcStageDefinition stage, IReadOnlyDictionary<string, string>? cast)
    {
        if (cast != null && (cast.TryGetValue(stage.Name, out var c) || (!string.IsNullOrEmpty(stage.Role) && cast.TryGetValue(stage.Role, out c))))
            return c;
        return string.IsNullOrWhiteSpace(stage.Character) ? null : stage.Character.Trim();
    }

    private void BuildEdges()
    {
        var raw = new List<(int Index, string From, string To, string? When, int? MaxLoops)>();
        if (IsImplicitChain)
        {
            var enabled = Nodes.ToList();
            for (var i = 1; i < enabled.Count; i++)
                raw.Add((i - 1, enabled[i - 1].Id, enabled[i].Id, null, null));
        }
        else
        {
            var seen = new HashSet<(string, string, string)>();
            for (var i = 0; i < Definition.Edges!.Count; i++)
            {
                var e = Definition.Edges[i];
                var from = e.From?.Trim() ?? string.Empty;
                var to = e.To?.Trim() ?? string.Empty;
                var when = string.IsNullOrWhiteSpace(e.When) ? null : e.When.Trim();
                var ok = true;
                if (!_byId.ContainsKey(from))
                {
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"Edge {i + 1} starts at unknown node '{from}'.", EdgeIndex: i));
                    ok = false;
                }
                if (!_byId.ContainsKey(to))
                {
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"Edge {i + 1} points to unknown node '{to}'.", EdgeIndex: i));
                    ok = false;
                }
                if (!ok) continue;
                from = _byId[from].Id;
                to = _byId[to].Id;
                if (!seen.Add((from.ToLowerInvariant(), to.ToLowerInvariant(), (when ?? string.Empty).ToLowerInvariant())))
                {
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"Duplicate edge {from} → {to}{(when == null ? "" : $" ({when})")}.", from, i));
                    continue;
                }
                if (e.MaxLoops is < 0)
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"Edge {from} → {to}: maxLoops cannot be negative.", from, i));
                raw.Add((i, from, to, when, e.MaxLoops));
            }
        }

        // An edge is a loop edge when it closes a cycle and declares maxLoops. Other cycle-closing
        // edges are errors (the scheduler would wait forever).
        var edges = new List<FlowGraphEdge>();
        var forward = new List<(int Index, string From, string To, string? When, int? MaxLoops)>();
        foreach (var e in raw.Where(e => e.MaxLoops is null or 0))
            forward.Add(e);
        foreach (var e in raw.Where(e => e.MaxLoops is > 0))
        {
            // Loop edge candidate: does "to" reach "from" over the forward edges?
            if (Reaches(forward, e.To, e.From))
            {
                edges.Add(new FlowGraphEdge(e.Index, e.From, e.To, e.When, e.MaxLoops!.Value, IsLoop: true));
            }
            else
            {
                _problems.Add(new FlowProblem(FlowProblemSeverity.Warning,
                    $"Edge {e.From} → {e.To} has maxLoops but does not go back to an earlier node; it is treated as a normal edge.", e.From, e.Index));
                forward.Add(e);
            }
        }

        foreach (var cycle in FindCycles(forward))
        {
            _problems.Add(new FlowProblem(FlowProblemSeverity.Error,
                $"Cycle {string.Join(" → ", cycle)} has no loop limit. Set maxLoops on the edge that goes back.",
                cycle[0], forward.FirstOrDefault(f => Same(f.From, cycle[^2]) && Same(f.To, cycle[^1])).Index));
        }

        foreach (var e in forward)
            edges.Add(new FlowGraphEdge(e.Index, e.From, e.To, e.When, 0, IsLoop: false));
        Edges = edges.OrderBy(e => e.Index).ToList();

        // Conditions must name an outcome of the source node.
        foreach (var e in Edges.Where(e => e.When != null))
        {
            var source = _byId[e.From];
            if (Same(e.When!, LoopExhausted))
            {
                if (!Outgoing(e.From).Any(o => o.IsLoop))
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error,
                        $"Edge {e.From} → {e.To} uses '{LoopExhausted}', but {e.From} has no loop edge.", e.From, e.Index));
                continue;
            }
            if (!source.Outcomes.Contains(e.When!, StringComparer.OrdinalIgnoreCase))
            {
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error,
                    source.Outcomes.Count == 0
                        ? $"Edge {e.From} → {e.To} has condition '{e.When}', but {e.From} declares no outcomes."
                        : $"Edge {e.From} → {e.To}: '{e.When}' is not an outcome of {e.From} ({string.Join(", ", source.Outcomes)}).",
                    e.From, e.Index));
            }
        }
    }

    private void ValidateNodes(Func<string, CharacterInfo?>? characterLookup, IReadOnlyDictionary<string, string>? cast)
    {
        foreach (var node in Nodes)
        {
            var stage = node.Stage;
            var characterId = EffectiveCharacter(stage, cast);
            // A disabled step passes straight through without an outcome, so a condition on an arrow
            // out of it could never be decided.
            if (!stage.Enabled && Edges.Any(e => !e.IsLoop && e.IsConditional && Same(e.From, node.Id)))
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error,
                    $"'{node.Name}' is disabled but arrows out of it have conditions; enable it or remove those conditions.", node.Id));
            if (characterId == null && string.IsNullOrWhiteSpace(stage.Role))
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}' has no character or role.", node.Id));

            if (characterId != null && characterLookup != null)
            {
                var c = characterLookup(characterId);
                if (c == null)
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': unknown character '{characterId}'.", node.Id));
                else if (c.IsTemplate)
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': '{characterId}' is a template; pick a character that extends it.", node.Id));
                else if (!c.IsValid)
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': character '{characterId}' is invalid.", node.Id));
            }

            if (string.IsNullOrWhiteSpace(stage.PromptTemplate))
                _problems.Add(new FlowProblem(FlowProblemSeverity.Warning, $"'{node.Name}' has no prompt; it only gets the task and its inputs.", node.Id));

            var outcomes = node.Outcomes;
            if (outcomes.Any(o => string.IsNullOrWhiteSpace(o) || o.Any(char.IsWhiteSpace)))
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': outcomes must be single words (e.g. approved, rejected).", node.Id));
            if (outcomes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outcomes.Count)
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': duplicate outcomes.", node.Id));
            if (outcomes.Contains(LoopExhausted, StringComparer.OrdinalIgnoreCase))
                _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': '{LoopExhausted}' is reserved.", node.Id));
            if (outcomes.Count == 1)
                _problems.Add(new FlowProblem(FlowProblemSeverity.Warning, $"'{node.Name}' has a single outcome; outcomes are for choosing between paths.", node.Id));

            // Template placeholders must refer to inputs that exist.
            foreach (var placeholder in FlowPrompt.InputPlaceholders(stage.PromptTemplate))
            {
                if (GetNode(placeholder) == null)
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Error, $"'{node.Name}': {{input:{placeholder}}} refers to unknown node '{placeholder}'.", node.Id));
                else if (!Incoming(node.Id).Any(e => Same(e.From, placeholder)))
                    _problems.Add(new FlowProblem(FlowProblemSeverity.Warning, $"'{node.Name}': {{input:{placeholder}}} is not connected into this node, so it will be empty.", node.Id));
            }
        }

        if (Nodes.Count == 0)
            _problems.Add(new FlowProblem(FlowProblemSeverity.Error, "The flow has no stages."));
    }

    private void ComputeWaves()
    {
        if (!IsValid && _problems.Any(p => p.IsError && p.Message.StartsWith("Cycle", StringComparison.Ordinal)))
        {
            Waves = Array.Empty<IReadOnlyList<string>>();
            return;
        }
        var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Level(string id, HashSet<string> visiting)
        {
            if (level.TryGetValue(id, out var l)) return l;
            if (!visiting.Add(id)) return 0;
            var preds = Incoming(id).Select(e => e.From).ToList();
            l = preds.Count == 0 ? 0 : preds.Max(p => Level(p, visiting)) + 1;
            visiting.Remove(id);
            level[id] = l;
            return l;
        }
        foreach (var n in Nodes) Level(n.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Waves = Nodes.GroupBy(n => level[n.Id]).OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<string>)g.OrderBy(n => n.Index).Select(n => n.Id).ToList()).ToList();
    }

    /// <summary>Nodes reachable from <paramref name="nodeId"/> over forward edges (not including itself).</summary>
    public HashSet<string> Descendants(string nodeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(new[] { nodeId });
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var e in Edges.Where(e => !e.IsLoop && Same(e.From, current)))
                if (result.Add(e.To)) stack.Push(e.To);
        }
        return result;
    }

    /// <summary>
    /// Nodes that may run at the same time as some other node (neither depends on the other).
    /// These get their own git worktree when the flow runs.
    /// </summary>
    public HashSet<string> NodesWithParallelPeers()
    {
        var desc = Nodes.ToDictionary(n => n.Id, n => Descendants(n.Id), StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Nodes)
            foreach (var b in Nodes)
            {
                if (ReferenceEquals(a, b)) continue;
                if (!desc[a.Id].Contains(b.Id) && !desc[b.Id].Contains(a.Id))
                {
                    result.Add(a.Id);
                    result.Add(b.Id);
                }
            }
        return result;
    }

    /// <summary>Nodes between <paramref name="from"/> and <paramref name="to"/> (inclusive) over forward edges.</summary>
    public HashSet<string> NodesBetween(string from, string to)
    {
        var after = Descendants(from);
        after.Add(from);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in after)
        {
            if (Same(id, to) || Descendants(id).Contains(to))
                result.Add(id);
        }
        return result;
    }

    /// <summary>Mermaid flowchart for docs and PRs.</summary>
    public string ToMermaid(Func<string, string?>? characterLabel = null)
    {
        var sb = new StringBuilder("flowchart LR\n");
        foreach (var n in Nodes)
        {
            var character = n.Stage.Character ?? n.Stage.Role;
            var who = characterLabel?.Invoke(character) ?? character;
            var label = string.IsNullOrEmpty(who) || Same(who, n.Name) ? n.Name : $"{n.Name}<br/><small>{who}</small>";
            sb.Append("    ").Append(MermaidId(n.Id)).Append("[\"").Append(label.Replace("\"", "'")).AppendLine("\"]");
        }
        foreach (var e in Edges)
        {
            var arrow = e.IsLoop ? "-.->" : "-->";
            var label = e.IsLoop ? $"{e.When ?? "always"} ↺{e.MaxLoops}" : e.When;
            sb.Append("    ").Append(MermaidId(e.From)).Append(' ').Append(arrow);
            if (!string.IsNullOrEmpty(label)) sb.Append("|").Append(label.Replace("|", "/")).Append('|');
            sb.Append(' ').AppendLine(MermaidId(e.To));
        }
        return sb.ToString();
    }

    private static string MermaidId(string id) => "n_" + new string(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Text view of the waves, e.g. "1: design · 2: backend ∥ model · 3: review".</summary>
    public string DescribeWaves() =>
        string.Join(" · ", Waves.Select((w, i) => $"{i + 1}: {string.Join(" ∥ ", w)}"));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Reaches(List<(int Index, string From, string To, string? When, int? MaxLoops)> edges, string start, string target)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(new[] { start });
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (Same(cur, target)) return true;
            if (!seen.Add(cur)) continue;
            foreach (var e in edges.Where(e => Same(e.From, cur))) stack.Push(e.To);
        }
        return false;
    }

    /// <summary>Elementary cycles (one per strongly connected group), as paths like [a, b, a].</summary>
    private static List<List<string>> FindCycles(List<(int Index, string From, string To, string? When, int? MaxLoops)> edges)
    {
        var cycles = new List<List<string>>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = edges.SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var start in nodes)
        {
            if (reported.Contains(start)) continue;
            // BFS for the shortest path start → … → start.
            var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var e in edges.Where(e => Same(e.From, start)))
            {
                if (Same(e.To, start)) { cycles.Add(new List<string> { start, start }); reported.Add(start); queue.Clear(); break; }
                if (!prev.ContainsKey(e.To)) { prev[e.To] = start; queue.Enqueue(e.To); }
            }
            if (reported.Contains(start)) continue;
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (edges.Any(e => Same(e.From, cur) && Same(e.To, start)))
                {
                    var path = new List<string> { start };
                    for (var n = cur; !Same(n, start); n = prev[n]) path.Insert(1, n);
                    path.Add(start);
                    cycles.Add(path);
                    foreach (var p in path) reported.Add(p);
                    break;
                }
                foreach (var e in edges.Where(e => Same(e.From, cur)))
                    if (!prev.ContainsKey(e.To) && !Same(e.To, start)) { prev[e.To] = cur; queue.Enqueue(e.To); }
            }
        }
        return cycles;
    }
}
