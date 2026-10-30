namespace AiCodeAgent.Bench.Tests;

public class UsageParsersTests
{
    [Fact]
    public void FlowJson_GivesTokensCostModelsAndRunId()
    {
        const string json = """
            {
              "flow": "bug-fix", "exitCode": 0, "status": "completed", "runId": "run-42",
              "totals": { "tokensIn": 1000, "tokensOut": 250, "cacheReadTokens": 5, "cacheWriteTokens": 0, "costUsd": 0.0421, "hasUnpricedModels": false },
              "steps": [ { "step": "a", "model": "m-1" }, { "step": "b", "model": "m-1" }, { "step": "c", "model": null } ]
            }
            """;
        var u = UsageParsers.FromFlowJson("progress line\n" + json + "\n", out var exit);
        Assert.Equal(0, exit);
        Assert.Equal(1250, u.Tokens);
        Assert.Equal(0.0421m, u.CostUsd);
        Assert.False(u.Unpriced);
        Assert.Equal(new[] { "m-1" }, u.Models);
        Assert.Equal("run-42", u.RunId);
        Assert.Equal("completed", u.Status);
    }

    [Fact]
    public void FlowJson_FlagsUnpricedModels()
    {
        var u = UsageParsers.FromFlowJson("""{ "totals": { "tokensIn": 1, "tokensOut": 1, "costUsd": 0, "hasUnpricedModels": true } }""", out _);
        Assert.True(u.Unpriced);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{ broken")]
    public void FlowJson_Garbage_IsUnknown_NotACrash(string text)
    {
        var u = UsageParsers.FromFlowJson(text, out var exit);
        Assert.Null(u.Tokens);
        Assert.Null(exit);
    }

    [Fact]
    public void AuditLines_GiveTheRunEnd()
    {
        var lines = new[]
        {
            """{"seq":1,"prev":"0","hash":"h1","entry":{"at":"2026-01-01T00:00:00.000Z","kind":"run.start","user":"u","model":"m-9","data":{}}}""",
            """{"seq":2,"prev":"h1","hash":"h2","entry":{"at":"2026-01-01T00:01:00.000Z","kind":"tool.call","user":"u","model":"m-9","data":{}}}""",
            """{"seq":3,"prev":"h2","hash":"h3","entry":{"at":"2026-01-01T00:02:00.000Z","kind":"run.end","user":"u","model":"m-9","data":{"status":"completed","durationMs":120000,"tokens":4321,"costUsd":0.31,"entries":3}}}"""
        };
        var u = UsageParsers.FromAuditLines(lines, "run-x");
        Assert.Equal(4321, u.Tokens);
        Assert.Equal(0.31m, u.CostUsd);
        Assert.Equal("completed", u.Status);
        Assert.Equal(new[] { "m-9" }, u.Models);
        Assert.Equal("run-x", u.RunId);
    }

    [Fact]
    public void AuditLines_WithoutARunEnd_HaveNoUsage()
    {
        var u = UsageParsers.FromAuditLines(new[] { """{"seq":1,"entry":{"kind":"run.start","data":{}}}""", "not json" });
        Assert.Null(u.Tokens);
        Assert.Null(u.CostUsd);
    }
}
