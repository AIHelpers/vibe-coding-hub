using System.Globalization;
using System.Text;
using AiCodeAgent.Core.Agent;

namespace AiCodeAgent.Core.Flows;

/// <summary>
/// A small picture of a flow for gallery cards: boxes laid out by <see cref="FlowLayout"/>, arrows between them, and loop
/// arrows below. Three SVG-style path strings (usable as an Avalonia <c>Path.Data</c>) so a card needs no canvas.
/// </summary>
/// <param name="Nodes">One rectangle per step.</param>
/// <param name="Edges">Forward arrows with arrowheads.</param>
/// <param name="Loops">Arrows that go back to an earlier step (draw them dashed).</param>
/// <param name="Width">Width of the drawing.</param>
/// <param name="Height">Height of the drawing.</param>
public sealed record FlowDiagram(string Nodes, string Edges, string Loops, double Width, double Height)
{
    public const double NodeWidth = 110;
    public const double NodeHeight = 40;

    public static FlowDiagram Build(SdlcPipelineDefinition definition)
    {
        var graph = FlowGraph.Build(definition);
        var pos = FlowLayout.Compute(graph, NodeWidth + 44, NodeHeight + 26, 8, 8);
        string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        var nodes = new StringBuilder();
        var edges = new StringBuilder();
        var loops = new StringBuilder();
        double right = 0, bottom = 0;
        foreach (var (x, y) in pos.Values)
        {
            nodes.Append($"M{N(x)},{N(y)} h{N(NodeWidth)} v{N(NodeHeight)} h-{N(NodeWidth)} z ");
            right = Math.Max(right, x + NodeWidth);
            bottom = Math.Max(bottom, y + NodeHeight);
        }
        var loopY = bottom + 12;
        var hasLoops = false;
        foreach (var e in graph.Edges)
        {
            if (!pos.TryGetValue(e.From, out var a) || !pos.TryGetValue(e.To, out var b)) continue;
            if (e.IsLoop)
            {
                hasLoops = true;
                var sx = a.X + NodeWidth / 2;
                var tx = b.X + NodeWidth / 2;
                loops.Append($"M{N(sx)},{N(a.Y + NodeHeight)} L{N(sx)},{N(loopY)} L{N(tx)},{N(loopY)} L{N(tx)},{N(b.Y + NodeHeight + 6)} ");
                loops.Append($"M{N(tx)},{N(b.Y + NodeHeight)} l-4,7 l8,0 z ");
            }
            else
            {
                var x1 = a.X + NodeWidth;
                var y1 = a.Y + NodeHeight / 2;
                var x2 = b.X;
                var y2 = b.Y + NodeHeight / 2;
                edges.Append($"M{N(x1)},{N(y1)} L{N(x2 - 6)},{N(y2)} ");
                edges.Append($"M{N(x2)},{N(y2)} l-7,-4 l0,8 z ");
            }
        }
        return new FlowDiagram(nodes.ToString().Trim(), edges.ToString().Trim(), loops.ToString().Trim(), right + 8, (hasLoops ? loopY : bottom) + 8);
    }
}
