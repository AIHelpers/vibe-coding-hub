namespace AiCodeAgent.Core.Flows;

/// <summary>
/// Layered left-to-right layout for the Flows canvas: one column per execution wave, nodes in a
/// column ordered by the average row of their predecessors (barycenter) to reduce crossing arrows.
/// Loop edges are ignored for layout; the canvas draws them curving back above the nodes.
/// </summary>
public static class FlowLayout
{
    public const double DefaultColumnWidth = 260;
    public const double DefaultRowHeight = 130;

    /// <summary>Top-left position per node id.</summary>
    public static Dictionary<string, (double X, double Y)> Compute(
        FlowGraph graph,
        double columnWidth = DefaultColumnWidth,
        double rowHeight = DefaultRowHeight,
        double originX = 40,
        double originY = 40)
    {
        var waves = graph.Waves.Count > 0
            ? graph.Waves.Select(w => w.ToList()).ToList()
            : new List<List<string>> { graph.Nodes.Select(n => n.Id).ToList() }; // invalid graph: one column

        var row = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < waves[0].Count; i++) row[waves[0][i]] = i;

        // Two sweeps: left→right by predecessors, then right→left by successors, then left→right again.
        for (var sweep = 0; sweep < 3; sweep++)
        {
            var leftToRight = sweep != 1;
            var order = leftToRight ? Enumerable.Range(1, waves.Count - 1) : Enumerable.Range(0, waves.Count - 1).Reverse();
            foreach (var w in order)
            {
                var wave = waves[w];
                double Key(string id)
                {
                    var neighbours = leftToRight
                        ? graph.Incoming(id).Select(e => e.From)
                        : graph.Outgoing(id).Where(e => !e.IsLoop).Select(e => e.To);
                    var known = neighbours.Where(row.ContainsKey).Select(n => row[n]).ToList();
                    return known.Count > 0 ? known.Average() : row.TryGetValue(id, out var r) ? r : wave.IndexOf(id);
                }
                var sorted = wave.OrderBy(Key).ThenBy(id => graph.GetNode(id)?.Index ?? 0).ToList();
                waves[w] = sorted;
                for (var i = 0; i < sorted.Count; i++) row[sorted[i]] = i;
            }
        }

        // Center each column vertically against the tallest one.
        var tallest = waves.Max(w => w.Count);
        var result = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        for (var w = 0; w < waves.Count; w++)
        {
            var offset = (tallest - waves[w].Count) * rowHeight / 2;
            for (var i = 0; i < waves[w].Count; i++)
                result[waves[w][i]] = (originX + w * columnWidth, originY + offset + i * rowHeight);
        }
        return result;
    }

    /// <summary>Copy of the pipeline with every stage moved to its computed position.</summary>
    public static Agent.SdlcPipelineDefinition Apply(Agent.SdlcPipelineDefinition pipeline, FlowGraph graph,
        double columnWidth = DefaultColumnWidth, double rowHeight = DefaultRowHeight)
    {
        var pos = Compute(graph, columnWidth, rowHeight);
        var stages = pipeline.Stages.Select((s, i) =>
        {
            var id = graph.Nodes[i].Id;
            return pos.TryGetValue(id, out var p) ? s with { X = p.X, Y = p.Y } : s;
        }).ToList();
        return pipeline with { Stages = stages };
    }

    /// <summary>Number of pairs of forward edges that cross between adjacent columns (for tests and diagnostics).</summary>
    public static int CountCrossings(FlowGraph graph, IReadOnlyDictionary<string, (double X, double Y)> positions)
    {
        var edges = graph.Edges.Where(e => !e.IsLoop && positions.ContainsKey(e.From) && positions.ContainsKey(e.To)).ToList();
        var crossings = 0;
        for (var i = 0; i < edges.Count; i++)
            for (var j = i + 1; j < edges.Count; j++)
            {
                var a = edges[i];
                var b = edges[j];
                var (ax1, ay1) = positions[a.From];
                var (ax2, ay2) = positions[a.To];
                var (bx1, by1) = positions[b.From];
                var (bx2, by2) = positions[b.To];
                if (ax1 != bx1 || ax2 != bx2) continue; // only compare edges spanning the same columns
                if ((ay1 - by1) * (ay2 - by2) < 0) crossings++;
            }
        return crossings;
    }
}
