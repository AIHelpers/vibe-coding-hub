using AiCodeAgent.Context;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AiCodeAgent.Context.Tests;

public class InMemoryContextManagerTests
{
    private static InMemoryContextManager CreateManager() =>
        new(Substitute.For<ILogger<InMemoryContextManager>>());

    [Fact]
    public async Task GetContextAsync_ReturnsEmptyList_ForNewSession()
    {
        var manager = CreateManager();

        var context = await manager.GetContextAsync("new-session");

        Assert.Empty(context);
    }

    [Fact]
    public async Task AddMessageAsync_AddsMessageToSession()
    {
        var manager = CreateManager();
        var message = new Message { Role = MessageRole.User, Content = "hello" };

        await manager.AddMessageAsync("s1", message);
        var context = await manager.GetContextAsync("s1");

        Assert.Single(context);
        Assert.Equal(MessageRole.User, context[0].Role);
        Assert.Equal("hello", context[0].Content);
    }

    [Fact]
    public async Task AddMessageAsync_EstimatesTokenCount()
    {
        var manager = CreateManager();
        // 8 chars / 4 + 1 = 3 tokens
        var message = new Message { Role = MessageRole.User, Content = "12345678" };

        await manager.AddMessageAsync("s1", message);
        var count = await manager.GetTokenCountAsync("s1");

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task AddMessageAsync_HandlesNullContent()
    {
        var manager = CreateManager();
        var message = new Message { Role = MessageRole.User, Content = null! };

        await manager.AddMessageAsync("s1", message);
        var count = await manager.GetTokenCountAsync("s1");

        Assert.Equal(1, count); // (0 / 4) + 1
    }

    [Fact]
    public async Task GetContextAsync_ReturnsCopy_NotReference()
    {
        var manager = CreateManager();
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.User, Content = "a" });

        var context1 = await manager.GetContextAsync("s1");
        var context2 = await manager.GetContextAsync("s1");

        Assert.NotSame(context1, context2);
        Assert.Single(context1);
        Assert.Single(context2);
    }

    [Fact]
    public async Task GetTokenCountAsync_ReturnsZero_ForUnknownSession()
    {
        var manager = CreateManager();

        var count = await manager.GetTokenCountAsync("unknown");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetTokenCountAsync_SumsAllMessages()
    {
        var manager = CreateManager();
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.User, Content = "1234" }); // 2 tokens
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.Assistant, Content = "1234" }); // 2 tokens

        var count = await manager.GetTokenCountAsync("s1");

        Assert.Equal(4, count);
    }

    [Fact]
    public async Task TrimContextAsync_DoesNothing_WhenUnderLimit()
    {
        var manager = CreateManager();
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.System, Content = "sys" });
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.User, Content = "hi" });

        await manager.TrimContextAsync("s1", maxTokens: 1000);
        var context = await manager.GetContextAsync("s1");

        Assert.Equal(2, context.Count);
    }

    [Fact]
    public async Task TrimContextAsync_TrimsOlderMessages_WhenOverLimit()
    {
        var manager = CreateManager();
        // System message at index 0 is preserved; trimming starts at index 1.
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.System, Content = "sys" });
        // Add 25 messages so that more than 20 recent are kept boundary is crossed.
        for (int i = 0; i < 25; i++)
            await manager.AddMessageAsync("s1", new Message { Role = MessageRole.User, Content = new string('x', 100) });

        var beforeCount = (await manager.GetContextAsync("s1")).Count;
        // Each message is ~26 tokens; total ~650. Set limit low to force trimming.
        await manager.TrimContextAsync("s1", maxTokens: 100);
        var afterCount = (await manager.GetContextAsync("s1")).Count;

        Assert.True(afterCount < beforeCount);
        // System message preserved
        var context = await manager.GetContextAsync("s1");
        Assert.Equal(MessageRole.System, context[0].Role);
    }

    [Fact]
    public async Task TrimContextAsync_DoesNothing_ForUnknownSession()
    {
        var manager = CreateManager();

        // Should not throw
        await manager.TrimContextAsync("unknown", maxTokens: 100);
    }

    [Fact]
    public async Task ClearAsync_RemovesSession()
    {
        var manager = CreateManager();
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.User, Content = "hi" });

        await manager.ClearAsync("s1");
        var context = await manager.GetContextAsync("s1");

        Assert.Empty(context);
    }

    [Fact]
    public async Task SessionsAreIsolated()
    {
        var manager = CreateManager();
        await manager.AddMessageAsync("s1", new Message { Role = MessageRole.User, Content = "a" });
        await manager.AddMessageAsync("s2", new Message { Role = MessageRole.User, Content = "b" });

        var c1 = await manager.GetContextAsync("s1");
        var c2 = await manager.GetContextAsync("s2");

        Assert.Single(c1);
        Assert.Equal("a", c1[0].Content);
        Assert.Single(c2);
        Assert.Equal("b", c2[0].Content);
    }
}