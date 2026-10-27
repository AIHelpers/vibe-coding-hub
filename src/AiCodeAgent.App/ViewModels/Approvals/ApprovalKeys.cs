using Avalonia.Input;

namespace AiCodeAgent.App.ViewModels;

public enum ApprovalKeyAction { None, Approve, Deny, CloseFeedback }

/// <summary>Keyboard for the approval dialog: Enter = Approve, Esc = Deny (or close the feedback box first).</summary>
public static class ApprovalKeys
{
    /// <param name="isEnteringFeedback">The "Deny with feedback" box is open (there Enter sends the feedback).</param>
    /// <param name="focusIsOnButton">A button has focus: Enter presses that button, never "Approve" behind its back.</param>
    public static ApprovalKeyAction Resolve(Key key, KeyModifiers modifiers, bool isEnteringFeedback, bool focusIsOnButton)
    {
        if (key == Key.Escape)
            return isEnteringFeedback ? ApprovalKeyAction.CloseFeedback : ApprovalKeyAction.Deny;

        if (key == Key.Enter && modifiers == KeyModifiers.None && !isEnteringFeedback && !focusIsOnButton)
            return ApprovalKeyAction.Approve;

        return ApprovalKeyAction.None;
    }
}
