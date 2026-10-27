using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.App.Views;
using Avalonia;
using ColorTextBlock.Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>E2-S8: Markdown in flow step output / background task results renders instead of showing raw syntax.</summary>
public class MarkdownOutputViewTests
{
    private const string Sample = """
        ## TodoApp 1.2

        Shipped **three** fixes:

        | Area | Result |
        |------|--------|
        | Build | passing |

        ```csharp
        var x = 1;
        ```
        """;

    /// <summary>
    /// All text the user would see. Markdown.Avalonia draws paragraphs, headings and table cells with its own
    /// CTextBlock (not a TextBlock), so both kinds of control are read.
    /// </summary>
    private static string VisibleText(Control root)
    {
        var parts = new List<string>();
        foreach (var visual in root.GetVisualDescendants())
        {
            switch (visual)
            {
                case CTextBlock c:
                    if (!string.IsNullOrEmpty(c.Text)) parts.Add(c.Text);
                    break;
                case TextBlock t:
                    if (!string.IsNullOrEmpty(t.Text)) parts.Add(t.Text);
                    if (t.Inlines != null) parts.AddRange(t.Inlines.OfType<Run>().Select(r => r.Text ?? string.Empty));
                    break;
            }
        }
        return string.Join("\n", parts);
    }

    private static (MarkdownOutputView View, Window Window) Show(string markdown)
    {
        var view = new MarkdownOutputView { Markdown = markdown };
        var window = new Window { Width = 700, Height = 600, Content = view };
        window.Show();
        Layout(window);
        return (view, window);
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Markdown_RendersWithoutRawSyntax()
    {
        var (view, _) = Show(Sample);
        var all = VisibleText(view);

        Assert.Contains("TodoApp 1.2", all);
        Assert.Contains("passing", all);
        Assert.DoesNotContain("##", all);
        Assert.DoesNotContain("**", all);
        Assert.DoesNotContain("|---", all);
        Assert.DoesNotContain("```", all);
    }

    [AvaloniaFact]
    public void ChangingMarkdown_ReRenders()
    {
        var (view, window) = Show("first");
        Assert.Contains("first", VisibleText(view));

        view.Markdown = "## second";
        Layout(window);

        var all = VisibleText(view);
        Assert.Contains("second", all);
        Assert.DoesNotContain("##", all);
    }
}
