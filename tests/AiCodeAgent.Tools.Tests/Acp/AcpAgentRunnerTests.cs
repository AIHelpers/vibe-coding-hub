using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Acp;
using Xunit;

namespace AiCodeAgent.Tools.Tests.Acp;

/// <summary>E6-S5: the prototype ACP client against a scripted in-memory agent.</summary>
public class AcpAgentRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acp-" + Guid.NewGuid().ToString("N"));

    public AcpAgentRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ExternalAgentRequest Request(PermissionMode mode = PermissionMode.Ask, int seconds = 5) =>
        new("fake-agent", Array.Empty<string>(), _dir, "Review the change", mode, TimeSpan.FromSeconds(seconds));

    private static Task<ExternalAgentResult> Run(FakeAcpAgent agent, ExternalAgentRequest request, Action<string>? progress = null, CancellationToken ct = default) =>
        new AcpAgentRunner(_ => agent).RunAsync(request, progress, ct);

    private static string Outcome(JsonElement answer) =>
        answer.GetProperty("outcome").GetProperty("outcome").GetString() == "selected"
            ? answer.GetProperty("outcome").GetProperty("optionId").GetString()!
            : "cancelled";

    [Fact]
    public async Task OneTurn_ReturnsTheTextAfterTheLastToolCall()
    {
        var progress = new System.Collections.Generic.List<string>();
        await using var agent = FakeAcpAgent.Scripted(async (a, args) =>
        {
            Assert.Equal("Review the change", args.GetProperty("prompt")[0].GetProperty("text").GetString());
            await a.Say("Let me look at the files. ");
            await a.ToolCall("Read Program.cs");
            await a.Say("Looks fine. ");
            await a.Say("OUTCOME: approved");
            return new { stopReason = "end_turn" };
        });

        var result = await Run(agent, Request(), progress.Add);

        Assert.Equal("Looks fine. OUTCOME: approved", result.Text);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(1, result.ToolCalls);
        Assert.Contains("ACP tool: Read Program.cs", progress);
        Assert.Equal(new[] { "initialize", "session/new", "session/prompt" }, agent.Requests.ToArray());
    }

    [Fact]
    public async Task WhenTheAgentSaysNothingAfterItsLastTool_TheWholeMessageIsUsed()
    {
        await using var agent = FakeAcpAgent.Scripted(async (a, _) =>
        {
            await a.Say("All done: 3 files changed.");
            await a.ToolCall("Write report");
            return new { stopReason = "end_turn" };
        });

        var result = await Run(agent, Request());
        Assert.Equal("All done: 3 files changed.", result.Text);
    }

    [Theory]
    [InlineData(PermissionMode.Ask, "read", "yes")]
    [InlineData(PermissionMode.Ask, "execute", "no")]
    [InlineData(PermissionMode.Ask, "edit", "no")]
    [InlineData(PermissionMode.Plan, "edit", "no")]
    [InlineData(PermissionMode.AutoEdit, "edit", "yes")]
    [InlineData(PermissionMode.AutoEdit, "execute", "no")]
    [InlineData(PermissionMode.FullAuto, "execute", "yes")]
    public async Task PermissionRequests_FollowThePermissionMode(PermissionMode mode, string kind, string expectedOption)
    {
        string? answered = null;
        await using var agent = FakeAcpAgent.Scripted(async (a, _) =>
        {
            answered = Outcome(await a.Ask(kind));
            await a.Say("ok");
            return new { stopReason = "end_turn" };
        });

        var result = await Run(agent, Request(mode));

        Assert.Equal(expectedOption, answered);
        var recorded = Assert.Single(result.Permissions);
        Assert.Equal(kind, recorded.Kind);
        Assert.Equal(expectedOption == "yes", recorded.Allowed);
    }

    [Fact]
    public async Task Files_CanBeReadInsideTheWorkingDirectoryOnly_AndWrittenOnlyWhenEditsAreAllowed()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one\ntwo\nthree");
        string? read = null, outsideError = null, askWrite = null, autoWrite = null;
        await using var agent = FakeAcpAgent.Scripted(async (a, _) =>
        {
            var r = await a.Connection.RequestAsync("fs/read_text_file", new { sessionId = "s1", path = Path.Combine(_dir, "a.txt"), line = 2, limit = 1 }, default);
            read = r.GetProperty("content").GetString();
            try { await a.Connection.RequestAsync("fs/read_text_file", new { sessionId = "s1", path = Path.Combine(Path.GetTempPath(), "..", "etc-passwd") }, default); }
            catch (AcpRpcException ex) { outsideError = ex.Message; }
            try { await a.Connection.RequestAsync("fs/write_text_file", new { sessionId = "s1", path = Path.Combine(_dir, "b.txt"), content = "x" }, default); }
            catch (AcpRpcException ex) { askWrite = ex.Message; }
            await a.Say("done");
            return new { stopReason = "end_turn" };
        });
        await Run(agent, Request(PermissionMode.Ask));

        Assert.Equal("two", read);
        Assert.Contains("outside", outsideError);
        Assert.Contains("not allowed", askWrite);
        Assert.False(File.Exists(Path.Combine(_dir, "b.txt")));

        await using var writer = FakeAcpAgent.Scripted(async (a, _) =>
        {
            await a.Connection.RequestAsync("fs/write_text_file", new { sessionId = "s1", path = Path.Combine(_dir, "b.txt"), content = "x" }, default);
            autoWrite = "written";
            await a.Say("done");
            return new { stopReason = "end_turn" };
        });
        await Run(writer, Request(PermissionMode.AutoEdit));
        Assert.Equal("written", autoWrite);
        Assert.Equal("x", File.ReadAllText(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public async Task UnknownVersion_IsRejected()
    {
        await using var agent = FakeAcpAgent.Scripted((_, _) => Task.FromResult<object?>(new { stopReason = "end_turn" }), version: 2);
        var ex = await Assert.ThrowsAsync<ExternalAgentException>(() => Run(agent, Request()));
        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public async Task AuthRequired_SaysToSignIn()
    {
        await using var agent = new FakeAcpAgent((_, method, _) => method == "initialize"
            ? Task.FromResult<object?>(FakeAcpAgent.Initialize())
            : throw new AcpRpcException(AcpRpcException.AuthRequired, "Authentication required"));
        var ex = await Assert.ThrowsAsync<ExternalAgentException>(() => Run(agent, Request()));
        Assert.Contains("sign in", ex.Message);
    }

    [Fact]
    public async Task AgentCrash_IsReportedWithItsStderr()
    {
        await using var agent = FakeAcpAgent.Scripted((a, _) => { a.Hangup(); return new TaskCompletionSource<object?>().Task; });
        var ex = await Assert.ThrowsAsync<ExternalAgentException>(() => Run(agent, Request()));
        Assert.Contains("exited", ex.Message);
        Assert.Contains("fake agent stderr", ex.Message);
    }

    [Fact]
    public async Task Cancel_SendsSessionCancel()
    {
        using var cts = new CancellationTokenSource();
        await using var agent = FakeAcpAgent.Scripted((_, _) =>
        {
            cts.CancelAfter(50);
            return new TaskCompletionSource<object?>().Task; // never answers
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(agent, Request(), ct: cts.Token));
        await Task.Delay(100);
        Assert.Contains("session/cancel", agent.Notifications);
    }

    [Fact]
    public async Task Timeout_IsAnError()
    {
        await using var agent = FakeAcpAgent.Scripted((_, _) => new TaskCompletionSource<object?>().Task);
        var request = Request() with { Timeout = TimeSpan.FromMilliseconds(150) };
        var ex = await Assert.ThrowsAsync<ExternalAgentException>(() => Run(agent, request));
        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public void Policy_PicksOnceOptions()
    {
        var options = new[]
        {
            new AcpPermissionOption("a", "Always", "allow_always"),
            new AcpPermissionOption("o", "Once", "allow_once"),
            new AcpPermissionOption("r", "Reject", "reject_once")
        };
        Assert.Equal("o", AcpPermissionPolicy.Choose(PermissionMode.FullAuto, "execute", options)!.OptionId);
        Assert.Equal("r", AcpPermissionPolicy.Choose(PermissionMode.Ask, "execute", options)!.OptionId);
        // With only an "always" allow, only FullAuto may use it.
        var onlyAlways = new[] { new AcpPermissionOption("a", "Always", "allow_always") };
        Assert.Equal("a", AcpPermissionPolicy.Choose(PermissionMode.FullAuto, "read", onlyAlways)!.OptionId);
        Assert.Null(AcpPermissionPolicy.Choose(PermissionMode.Ask, "read", onlyAlways));
    }

    /// <summary>
    /// Smoke test against a real agent. Set VCH_ACP_COMMAND (and optionally VCH_ACP_ARGS, space separated), for example
    /// VCH_ACP_COMMAND=gemini VCH_ACP_ARGS=--acp. Without it the test does nothing.
    /// </summary>
    [Fact]
    public async Task RealAgent_AnswersAPrompt()
    {
        var command = Environment.GetEnvironmentVariable("VCH_ACP_COMMAND");
        if (string.IsNullOrWhiteSpace(command)) return;
        var args = (Environment.GetEnvironmentVariable("VCH_ACP_ARGS") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var result = await new AcpAgentRunner().RunAsync(
            new ExternalAgentRequest(command, args, _dir, "Reply with the single word: pong", PermissionMode.Plan, TimeSpan.FromSeconds(120)),
            null, CancellationToken.None);

        Assert.Contains("pong", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}
