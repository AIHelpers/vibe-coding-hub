using Avalonia;
using Avalonia.Controls;
using MdViewer = Markdown.Avalonia.MarkdownScrollViewer;

namespace AiCodeAgent.App.Views;

/// <summary>
/// Renders Markdown with the chat's renderer and styles. Used for flow step output and Background Tasks results,
/// so headings, bold text, tables and code blocks look the same as in the chat.
/// </summary>
public partial class MarkdownOutputView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownOutputView, string?>(nameof(Markdown));

    public MarkdownOutputView()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            this.FindControl<MdViewer>("Viewer")!.Markdown = change.GetNewValue<string?>() ?? string.Empty;
    }
}
