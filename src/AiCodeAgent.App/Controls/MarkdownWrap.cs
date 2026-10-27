using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiCodeAgent.App.Controls;

/// <summary>
/// MarkdownScrollViewer (Markdown.Avalonia) exposes no scroll-bar property to XAML; it hosts its own ScrollViewer.
/// With horizontal scrolling on, that ScrollViewer measures its content at infinite width, so long lines are cut off
/// instead of wrapping. <c>ctl:MarkdownWrap.WrapText="True"</c> switches horizontal scrolling off on that inner viewer.
/// </summary>
public static class MarkdownWrap
{
    public static readonly AttachedProperty<bool> WrapTextProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("WrapText");

    public static bool GetWrapText(Control control) => control.GetValue(WrapTextProperty);

    public static void SetWrapText(Control control, bool value) => control.SetValue(WrapTextProperty, value);

    static MarkdownWrap()
    {
        WrapTextProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true) return;
            control.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Apply(control), DispatcherPriority.Loaded);
            if (control.IsAttachedToVisualTree()) Dispatcher.UIThread.Post(() => Apply(control), DispatcherPriority.Loaded);
        });
    }

    internal static void Apply(Control control)
    {
        var viewer = control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer == null) return;

        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // The library may set it again when the content changes: keep it off.
        viewer.GetObservable(ScrollViewer.HorizontalScrollBarVisibilityProperty).Subscribe(v =>
        {
            if (v != ScrollBarVisibility.Disabled) viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        });
    }
}
