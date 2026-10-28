using AiCodeAgent.Core.Terminal;

namespace AiCodeAgent.App.ViewModels;

/// <summary>What the terminal control needs from a shell tab: the screen model, a way to type into it and to resize it.</summary>
public interface ITerminalSession
{
    TerminalEmulator Emulator { get; }

    /// <summary>The screen changed. Raised on any thread, often.</summary>
    event Action? Changed;

    bool IsExited { get; }

    /// <summary>Sends keystrokes or pasted text to the program.</summary>
    void SendInput(string text);

    /// <summary>The control's size in characters changed.</summary>
    void Resize(int cols, int rows);
}
