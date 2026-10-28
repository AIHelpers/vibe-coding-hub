using System.Text;
using Avalonia.Input;

namespace AiCodeAgent.App.Controls;

/// <summary>Turns key presses into what a terminal program expects on its input (xterm-style escape sequences).</summary>
public static class TerminalKeyEncoder
{
    private const string Esc = "\u001b";

    /// <summary>
    /// The bytes (as text) for a key press, or null when the key produces text on its own (the text-input event then carries it)
    /// or is not a terminal key at all.
    /// </summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        // Ctrl+Alt is AltGr on many keyboard layouts (it types characters like @ or {): leave those to the text input.
        if (ctrl && alt) return null;
        // Windows/Command key combinations belong to the system and the app.
        if (modifiers.HasFlag(KeyModifiers.Meta)) return null;

        var parameter = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0); // xterm modifier number; 1 = none

        switch (key)
        {
            case Key.Enter:
                return alt ? Esc + "\r" : "\r";
            case Key.Tab:
                return shift ? Esc + "[Z" : "\t";
            case Key.Back:
                return ctrl ? "\b" : alt ? Esc + "\u007f" : "\u007f";
            case Key.Escape:
                return Esc;
            case Key.Space:
                if (ctrl) return "\0";
                return alt ? Esc + " " : null;
            case Key.Up: return Cursor('A', parameter, applicationCursorKeys);
            case Key.Down: return Cursor('B', parameter, applicationCursorKeys);
            case Key.Right: return Cursor('C', parameter, applicationCursorKeys);
            case Key.Left: return Cursor('D', parameter, applicationCursorKeys);
            case Key.Home: return Cursor('H', parameter, applicationCursorKeys);
            case Key.End: return Cursor('F', parameter, applicationCursorKeys);
            case Key.Insert: return Tilde(2, parameter);
            case Key.Delete: return Tilde(3, parameter);
            case Key.PageUp: return Tilde(5, parameter);
            case Key.PageDown: return Tilde(6, parameter);
            case Key.F1: return Function('P', parameter);
            case Key.F2: return Function('Q', parameter);
            case Key.F3: return Function('R', parameter);
            case Key.F4: return Function('S', parameter);
            case Key.F5: return Tilde(15, parameter);
            case Key.F6: return Tilde(17, parameter);
            case Key.F7: return Tilde(18, parameter);
            case Key.F8: return Tilde(19, parameter);
            case Key.F9: return Tilde(20, parameter);
            case Key.F10: return Tilde(21, parameter);
            case Key.F11: return Tilde(23, parameter);
            case Key.F12: return Tilde(24, parameter);
        }

        if (ctrl)
        {
            // Ctrl+Shift+letter is an app shortcut (command palette, copy, paste), not a control character.
            if (shift && key is >= Key.A and <= Key.Z) return null;
            if (key is >= Key.A and <= Key.Z) return Prefix(alt, ((char)(1 + (key - Key.A))).ToString());
            switch (key)
            {
                case Key.OemOpenBrackets: return Prefix(alt, "\u001b");   // Ctrl+[
                case Key.OemPipe: return Prefix(alt, "\u001c");           // Ctrl+\
                case Key.OemCloseBrackets: return Prefix(alt, "\u001d");  // Ctrl+]
                case Key.D2: return "\0";                                 // Ctrl+@ / Ctrl+2
                case Key.D6: return "\u001e";                             // Ctrl+^
                case Key.OemMinus: return "\u001f";                       // Ctrl+_
            }

            return null;
        }

        if (alt)
        {
            // Alt+letter / Alt+digit: ESC then the character, as terminals have always done.
            if (key is >= Key.A and <= Key.Z) return Esc + (char)((shift ? 'A' : 'a') + (key - Key.A));
            if (key is >= Key.D0 and <= Key.D9 && !shift) return Esc + (char)('0' + (key - Key.D0));
        }

        return null;
    }

    /// <summary>Text to paste, with line breaks as Enter presses and, when the program asked for it, marked as a paste.</summary>
    public static string EncodePaste(string text, bool bracketed)
    {
        var normalised = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!bracketed) return normalised;
        // A pasted escape character could close the bracket early and smuggle in commands; drop it.
        return Esc + "[200~" + normalised.Replace(Esc, string.Empty) + Esc + "[201~";
    }

    private static string Prefix(bool alt, string value) => alt ? Esc + value : value;

    private static string Cursor(char final, int parameter, bool application)
    {
        if (parameter > 1) return Esc + "[1;" + parameter + final;
        return application ? Esc + "O" + final : Esc + "[" + final;
    }

    private static string Function(char final, int parameter) =>
        parameter > 1 ? Esc + "[1;" + parameter + final : Esc + "O" + final;

    private static string Tilde(int number, int parameter) =>
        parameter > 1 ? Esc + "[" + number + ";" + parameter + "~" : Esc + "[" + number + "~";
}
