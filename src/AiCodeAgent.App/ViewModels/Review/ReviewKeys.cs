using Avalonia.Input;

namespace AiCodeAgent.App.ViewModels;

public enum ReviewKeyAction { None, Next, Previous, Approve, RequestChanges, Decline, SubmitComment, CancelComment }

/// <summary>
/// Keyboard of the Review Queue: J / Down = next, K / Up = previous, A / Enter = approve, R = request changes, X / Delete =
/// decline. While the comment box is open only Ctrl+Enter (send) and Esc (close) act, so typing never triggers an action.
/// </summary>
public static class ReviewKeys
{
    /// <param name="isEnteringComment">The comment box is open.</param>
    /// <param name="focusInTextInput">A text box has focus (the comment box, or any other): letters are text there.</param>
    /// <param name="focusIsOnButton">A button has focus: Enter presses that button, never "approve" behind its back.</param>
    public static ReviewKeyAction Resolve(Key key, KeyModifiers modifiers, bool isEnteringComment, bool focusInTextInput, bool focusIsOnButton)
    {
        if (isEnteringComment)
        {
            if (key == Key.Escape) return ReviewKeyAction.CancelComment;
            if (key == Key.Enter && modifiers == KeyModifiers.Control) return ReviewKeyAction.SubmitComment;
            return ReviewKeyAction.None;
        }

        if (focusInTextInput || modifiers != KeyModifiers.None) return ReviewKeyAction.None;

        return key switch
        {
            Key.J or Key.Down => ReviewKeyAction.Next,
            Key.K or Key.Up => ReviewKeyAction.Previous,
            Key.A => ReviewKeyAction.Approve,
            Key.Enter when !focusIsOnButton => ReviewKeyAction.Approve,
            Key.R => ReviewKeyAction.RequestChanges,
            Key.X or Key.Delete => ReviewKeyAction.Decline,
            _ => ReviewKeyAction.None
        };
    }
}
