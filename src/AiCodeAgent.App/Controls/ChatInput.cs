using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AiCodeAgent.App.Controls;

/// <summary>
/// Chat-style keys for a multi-line <see cref="TextBox"/> (<c>AcceptsReturn="True"</c>): <b>Enter</b> or
/// <b>Ctrl+Enter</b> runs <see cref="SubmitCommandProperty"/>; <b>Shift+Enter</b> (and Alt+Enter) is left to the
/// TextBox, which inserts a new line.
/// <code>&lt;TextBox AcceptsReturn="True" ctl:ChatInput.SubmitCommand="{Binding SendCommand}"/&gt;</code>
/// The handler runs in the tunnel phase, i.e. before the TextBox's own Enter handling would insert a newline.
/// </summary>
public sealed class ChatInput : AvaloniaObject
{
    public static readonly AttachedProperty<ICommand?> SubmitCommandProperty =
        AvaloniaProperty.RegisterAttached<ChatInput, TextBox, ICommand?>("SubmitCommand");

    static ChatInput()
    {
        SubmitCommandProperty.Changed.AddClassHandler<TextBox>(OnSubmitCommandChanged);
    }

    private ChatInput()
    {
    }

    public static ICommand? GetSubmitCommand(TextBox element) => element.GetValue(SubmitCommandProperty);

    public static void SetSubmitCommand(TextBox element, ICommand? value) => element.SetValue(SubmitCommandProperty, value);

    /// <summary>True for plain Enter and Ctrl+Enter; false when Shift or Alt is held (those insert a new line).</summary>
    public static bool IsSubmitGesture(Key key, KeyModifiers modifiers) =>
        key == Key.Enter && modifiers is KeyModifiers.None or KeyModifiers.Control;

    private static void OnSubmitCommandChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        // Remove first so setting the property twice never registers the handler twice.
        textBox.RemoveHandler(InputElement.KeyDownEvent, OnKeyDownTunnel);
        if (e.NewValue is ICommand)
            textBox.AddHandler(InputElement.KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private static void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox textBox || !IsSubmitGesture(e.Key, e.KeyModifiers))
            return;

        var command = GetSubmitCommand(textBox);
        if (command == null)
            return;

        // Enter never inserts a newline here, even when there is nothing to send (empty box, agent busy).
        e.Handled = true;
        if (command.CanExecute(null))
            command.Execute(null);
    }
}
