using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.GitHosting;
using Xunit;

namespace AiCodeAgent.App.Tests.ViewModels;

public class IssueCommandTests
{
    [Fact]
    public void Parse_NumberAndFilters()
    {
        var c = ChatViewModel.ParseIssueCommand("#42");
        Assert.Equal(42, c.Number);

        var f = ChatViewModel.ParseIssueCommand("label:bug assignee:me");
        Assert.Null(f.Number);
        Assert.Equal("bug", f.Label);
        Assert.Equal("me", f.Assignee);

        Assert.True(ChatViewModel.ParseIssueCommand("clear").Clear);
        Assert.Null(ChatViewModel.ParseIssueCommand("").Number);
    }

    [Fact]
    public void List_ShowsNumbersLabelsAndAssignee()
    {
        var repo = new HostRepo("github.com", "o", "r");
        var issue = new HostIssue(7, "Crash", "", "ann", new[] { "bug" }, "bob", "u", System.DateTimeOffset.UnixEpoch, System.Array.Empty<HostIssueComment>());
        var text = ChatViewModel.FormatIssueList(repo, new[] { issue }, ChatViewModel.ParseIssueCommand("label:bug"));

        Assert.Contains("o/r", text);
        Assert.Contains("`#7` Crash", text);
        Assert.Contains("[bug]", text);
        Assert.Contains("@bob", text);
        Assert.Contains("label bug", text);
        Assert.Contains("No open issues", ChatViewModel.FormatIssueList(repo, System.Array.Empty<HostIssue>(), ChatViewModel.ParseIssueCommand("")));
    }
}
