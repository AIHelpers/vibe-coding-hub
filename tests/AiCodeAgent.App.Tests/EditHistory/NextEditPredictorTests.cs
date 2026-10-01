using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.EditHistory;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using NSubstitute;

namespace AiCodeAgent.App.Tests.EditHistory;

public class NextEditPredictorTests
{
    private static IAiProvider FakeProvider(string responseContent, string model = "test-model")
    {
        var provider = Substitute.For<IAiProvider>();
        provider.Name.Returns("fake");
        provider.SupportedModels.Returns(new[] { model });
        provider
            .CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompletionResponse { Content = responseContent, Model = model });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { model });
        return provider;
    }

    [Fact]
    public async Task PredictAsync_ParsesValidJsonResponse()
    {
        var json = """
        {
          "spans": [
            {
              "startLine": 5,
              "endLine": 5,
              "newText": "Console.WriteLine();",
              "isInsert": true,
              "confidence": 0.8
            }
          ],
          "confidence": 0.75
        }
        """;
        var provider = FakeProvider(json);
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("a.cs", "code", 5, 1, Array.Empty<EditRecord>());

        Assert.Equal("a.cs", result.FilePath);
        Assert.Equal(0.75, result.Confidence);
        Assert.Single(result.Spans);
        Assert.Equal(5, result.Spans[0].StartLine);
        Assert.Equal("Console.WriteLine();", result.Spans[0].NewText);
        Assert.True(result.Spans[0].IsInsert);
        Assert.True(result.LatencyMs >= 0);
    }

    [Fact]
    public async Task PredictAsync_ExtractsJsonFromSurroundingProse()
    {
        var raw = "Sure! Here is the prediction:\n{\"spans\":[],\"confidence\":0}\nDone.";
        var provider = FakeProvider(raw);
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("a.cs", "code", 1, 1, Array.Empty<EditRecord>());

        Assert.Empty(result.Spans);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task PredictAsync_EmptyResponse_ReturnsEmptyPrediction()
    {
        var provider = FakeProvider("");
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("a.cs", "code", 1, 1, Array.Empty<EditRecord>());

        Assert.Empty(result.Spans);
        Assert.Equal(0, result.Confidence);
        Assert.Equal("a.cs", result.FilePath);
    }

    [Fact]
    public async Task PredictAsync_InvalidJson_ReturnsEmptyPrediction()
    {
        var provider = FakeProvider("{ not valid json");
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("a.cs", "code", 1, 1, Array.Empty<EditRecord>());

        Assert.Empty(result.Spans);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task PredictAsync_MultipleSpans_AllParsed()
    {
        var json = """
        {
          "spans": [
            { "startLine": 3, "endLine": 3, "newText": "a", "isInsert": true, "confidence": 0.9 },
            { "startLine": 7, "endLine": 9, "newText": "b", "isInsert": false, "confidence": 0.6 }
          ],
          "confidence": 0.7
        }
        """;
        var provider = FakeProvider(json);
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("a.cs", "code", 3, 1, Array.Empty<EditRecord>());

        Assert.Equal(2, result.Spans.Count);
        Assert.True(result.Spans[0].IsInsert);
        Assert.False(result.Spans[1].IsInsert);
        Assert.Equal(7, result.Spans[1].StartLine);
        Assert.Equal(9, result.Spans[1].EndLine);
    }

    [Fact]
    public async Task PredictAsync_SendsRecentEditsInPrompt()
    {
        CompletionRequest? captured = null;
        var provider = Substitute.For<IAiProvider>();
        provider.Name.Returns("fake");
        provider.SupportedModels.Returns(new[] { "m" });
        provider
            .CompleteAsync(Arg.Do<CompletionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new CompletionResponse { Content = "{\"spans\":[],\"confidence\":0}" });

        var predictor = new NextEditPredictor(provider);
        var edits = new List<EditRecord>
        {
            new() { FilePath = "a.cs", StartLine = 2, After = "edited" }
        };

        await predictor.PredictAsync("a.cs", "content", 3, 1, edits);

        Assert.NotNull(captured);
        Assert.Contains("Recent edits", captured!.Messages[0].Content);
        Assert.Contains("edited", captured.Messages[0].Content);
    }

    [Fact]
    public async Task PredictAsync_NullFilePath_ReturnsEmptyWithoutCallingProvider()
    {
        var provider = FakeProvider("{}");
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("", "code", 1, 1, Array.Empty<EditRecord>());

        Assert.Empty(result.Spans);
        await provider.DidNotReceive().CompleteAsync(
            Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NextEditPredictor(null!));
    }

    [Fact]
    public async Task PredictAsync_DefaultsEndLineToStartLineWhenMissing()
    {
        var json = """
        {
          "spans": [
            { "startLine": 4, "newText": "x", "isInsert": true, "confidence": 0.5 }
          ],
          "confidence": 0.5
        }
        """;
        var provider = FakeProvider(json);
        var predictor = new NextEditPredictor(provider);

        var result = await predictor.PredictAsync("a.cs", "code", 4, 1, Array.Empty<EditRecord>());

        Assert.Equal(4, result.Spans[0].EndLine);
    }

    [Fact]
    public async Task PredictAsync_RespectsCancellation()
    {
        var provider = Substitute.For<IAiProvider>();
        provider.Name.Returns("fake");
        provider.SupportedModels.Returns(new[] { "m" });
        provider
            .CompleteAsync(Arg.Any<CompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var ct = c.ArgAt<CancellationToken>(1);
                ct.ThrowIfCancellationRequested();
                return new CompletionResponse { Content = "{}" };
            });

        var predictor = new NextEditPredictor(provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            predictor.PredictAsync("a.cs", "code", 1, 1, Array.Empty<EditRecord>(), cts.Token));
    }
}