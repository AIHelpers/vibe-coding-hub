using System.Text;

namespace AiCodeAgent.Core.GitHosting;

/// <summary>Turns an issue into the text of a task: title, body and the discussion.</summary>
public static class IssueTaskBuilder
{
    public const int MaxCommentChars = 4000;

    public static string Build(HostIssue issue)
    {
        var sb = new StringBuilder();
        sb.Append("Resolve issue #").Append(issue.Number).Append(": ").AppendLine(issue.Title.Trim());
        sb.Append("Link: ").AppendLine(issue.Url);
        if (issue.Labels.Count > 0) sb.Append("Labels: ").AppendLine(string.Join(", ", issue.Labels));
        if (!string.IsNullOrWhiteSpace(issue.Body))
        {
            sb.AppendLine().AppendLine("Description:").AppendLine(issue.Body.Trim());
        }
        if (issue.Comments.Count > 0)
        {
            sb.AppendLine().AppendLine("Discussion:");
            var used = 0;
            foreach (var c in issue.Comments)
            {
                var text = (c.Body ?? string.Empty).Trim();
                if (text.Length == 0) continue;
                if (used + text.Length > MaxCommentChars)
                {
                    sb.AppendLine("- (more comments omitted)");
                    break;
                }
                used += text.Length;
                sb.Append("- @").Append(c.Author).Append(": ").AppendLine(text.Replace("\r", string.Empty).Replace("\n", "\n  "));
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static IssueLink LinkOf(string provider, HostRepo repo, HostIssue issue) =>
        new(provider, repo.Host, repo.Owner, repo.Name, issue.Number, issue.Url, issue.Title);
}
