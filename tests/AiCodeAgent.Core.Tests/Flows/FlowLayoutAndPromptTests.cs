using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using static AiCodeAgent.Core.Tests.Flows.FlowGraphTests;

namespace AiCodeAgent.Core.Tests.Flows;

public class FlowLayoutTests
{
    [Fact]
    public void Layout_PutsWavesInColumns_AndCentersThem()
    {
        var g = FlowGraph.Build(FeatureTeam());
        var pos = FlowLayout.Compute(g, columnWidth: 200, rowHeight: 100, originX: 0, originY: 0);

        Assert.Equal(0, pos["design"].X);
        Assert.Equal(200, pos["backend"].X);
        Assert.Equal(200, pos["model"].X);
        Assert.Equal(400, pos["review"].X);
        Assert.Equal(600, pos["deploy"].X);
        // design sits between backend and model.
        Assert.Equal((pos["backend"].Y + pos["model"].Y) / 2, pos["design"].Y);
        Assert.NotEqual(pos["backend"].Y, pos["model"].Y);
    }

    [Fact]
    public void Layout_RemovesCrossingsForCrissCross()
    {
        // a1→b2, a2→b1 in stage order would cross; the barycenter sweep reorders column b.
        var p = new SdlcPipelineDefinition
        {
            Name = "x",
            Stages = new() { Stage("a1"), Stage("a2"), Stage("b1"), Stage("b2") },
            Edges = new() { Edge("a1", "b2"), Edge("a2", "b1") }
        };
        var g = FlowGraph.Build(p);
        var pos = FlowLayout.Compute(g);
        Assert.Equal(0, FlowLayout.CountCrossings(g, pos));
    }

    [Fact]
    public void Apply_WritesPositionsIntoStages()
    {
        var p = FeatureTeam();
        var laidOut = FlowLayout.Apply(p, FlowGraph.Build(p));
        Assert.All(laidOut.Stages, s => Assert.NotNull(s.X));
        Assert.True(laidOut.Stages[4].X > laidOut.Stages[0].X);
    }
}

public class FlowPromptTests
{
    private static readonly FlowNodeOutput Design = new("design", "alex", "The plan.", null, 1);
    private static readonly FlowNodeOutput Model = new("model", "mia", "Model trained.", null, 1);

    [Fact]
    public void Build_AddsTaskInputsAndOutcomeInstruction()
    {
        var prompt = FlowPrompt.Build("Review: {task}", "add pagination", new[] { Design, Model }, outcomes: new[] { "approved", "rejected" });

        Assert.StartsWith("Review: add pagination", prompt);
        Assert.Contains("<input from=\"design\" character=\"alex\">\nThe plan.\n</input>", prompt);
        Assert.Contains("<input from=\"model\" character=\"mia\">", prompt);
        Assert.EndsWith("Choose one of: approved, rejected.", prompt);
        Assert.Contains("OUTCOME: <approved | rejected>", prompt);
    }

    [Fact]
    public void InlinePlaceholder_IsNotRepeatedInInputsBlock()
    {
        var prompt = FlowPrompt.Build("Build it.\n{input:design}", "t", new[] { Design, Model });
        Assert.Contains("Build it.\nThe plan.", prompt);
        Assert.DoesNotContain("from=\"design\"", prompt);
        Assert.Contains("from=\"model\"", prompt);
        Assert.Contains("(no output from ghost)", FlowPrompt.Build("{input:ghost}", "t", Array.Empty<FlowNodeOutput>()));
    }

    [Fact]
    public void Feedback_IsAddedForLoops()
    {
        var review = new FlowNodeOutput("review", "quinn", "Missing tests.", "rejected", 1);
        var prompt = FlowPrompt.Build("Implement.", "t", new[] { Design }, new[] { review });
        Assert.Contains("sent back for another round", prompt);
        Assert.Contains("<feedback from=\"review\" character=\"quinn\" outcome=\"rejected\">\nMissing tests.\n</feedback>", prompt);
    }

    [Fact]
    public void EmptyTemplate_FallsBackToTask()
    {
        Assert.StartsWith("Task: fix it", FlowPrompt.Build(null, "fix it", Array.Empty<FlowNodeOutput>()));
    }

    [Theory]
    [InlineData("Looks good.\nOUTCOME: approved", "approved")]
    [InlineData("Looks good.\n**OUTCOME:** Approved", "approved")]
    [InlineData("OUTCOME: rejected\nlater changed my mind\nOUTCOME: approved", "approved")]
    [InlineData("  outcome : rejected  ", "rejected")]
    [InlineData("OUTCOME: maybe", null)]
    [InlineData("no verdict here", null)]
    [InlineData("the OUTCOME: approved is inline", null)]
    public void ParseOutcome(string answer, string? expected)
    {
        Assert.Equal(expected, FlowPrompt.ParseOutcome(answer, new[] { "approved", "rejected" }));
    }

    [Fact]
    public void Cap_KeepsStartAndEnd()
    {
        var text = "START" + new string('x', FlowPrompt.MaxInputChars * 2) + "END";
        var capped = FlowPrompt.Cap(text);
        Assert.StartsWith("START", capped);
        Assert.EndsWith("END", capped);
        Assert.Contains("characters cut", capped);
        Assert.True(capped.Length < FlowPrompt.MaxInputChars + 100);
        Assert.Equal("(no output)", FlowPrompt.Cap(""));
    }
}
