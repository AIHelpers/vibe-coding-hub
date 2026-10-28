using System;
using System.Text;
using AiCodeAgent.Core.Terminal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>A tab of the terminal panel.</summary>
public abstract partial class TerminalTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Shell tabs can be closed; the Agent output tab stays.</summary>
    public abstract bool IsClosable { get; }
}

/// <summary>The read-only log of what the agent ran (the former terminal pane).</summary>
public sealed class AgentOutputTabViewModel : TerminalTabViewModel
{
    public AgentOutputTabViewModel()
    {
        Title = "Agent output";
    }

    public override bool IsClosable => false;
}

/// <summary>
/// One interactive shell: a program on a pseudo-terminal whose output feeds a <see cref="TerminalEmulator"/>.
/// The session keeps running while the panel is hidden or another tab is selected.
/// This is the user's own shell, so the agent's Docker sandbox setting does not apply to it.
/// </summary>
public sealed partial class ShellTabViewModel : TerminalTabViewModel, ITerminalSession, IDisposable
{
    private readonly Func<PtyStartOptions, IPtyProcess> _factory;
    private readonly Action<Action> _post;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly string _baseTitle;
    private IPtyProcess? _pty;
    private bool _disposed;

    [ObservableProperty]
    private bool _isExited;

    [ObservableProperty]
    private int? _exitCode;

    public ShellTabViewModel(string baseTitle, ShellCommand shell, string workingDirectory, int cols, int rows,
        Func<PtyStartOptions, IPtyProcess> factory, Action<Action> post)
    {
        _baseTitle = baseTitle;
        _factory = factory;
        _post = post;
        Shell = shell;
        WorkingDirectory = workingDirectory;
        Title = baseTitle;
        Emulator = new TerminalEmulator(cols, rows);
        Emulator.Reply += reply => _pty?.Write(reply);
        Emulator.TitleChanged += () => _post(UpdateTitle);
    }

    public override bool IsClosable => true;

    public ShellCommand Shell { get; }

    public string WorkingDirectory { get; }

    public TerminalEmulator Emulator { get; }

    public event Action? Changed;

    /// <summary>Starts the shell. A failure (shell not found, no ConPTY) is written into the terminal itself.</summary>
    public void Start()
    {
        try
        {
            var pty = _factory(new PtyStartOptions
            {
                FileName = Shell.FileName,
                Arguments = Shell.Arguments,
                WorkingDirectory = WorkingDirectory,
                Cols = Emulator.Cols,
                Rows = Emulator.Rows
            });
            _pty = pty;
            pty.DataReceived += OnData;
            pty.Exited += OnExited;
        }
        catch (Exception ex)
        {
            Emulator.Write("\u001b[31mCannot start " + Shell.Display + ": " + ex.Message.Replace("\r", " ").Replace("\n", " ") + "\u001b[0m\r\n");
            Emulator.Write("Set another shell in Settings → Terminal.\r\n");
            _post(() =>
            {
                IsExited = true;
                ExitCode = -1;
            });
            Changed?.Invoke();
        }
    }

    private void OnData(byte[] bytes)
    {
        string text;
        lock (_decoder)
        {
            var chars = new char[_decoder.GetCharCount(bytes, 0, bytes.Length)];
            var n = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
            text = new string(chars, 0, n);
        }

        Emulator.Write(text);
        Changed?.Invoke();
    }

    private void OnExited(int code)
    {
        Emulator.Write("\r\n\u001b[2m[Process exited with code " + code + "]\u001b[0m\r\n");
        _post(() =>
        {
            ExitCode = code;
            IsExited = true;
            UpdateTitle();
        });
        Changed?.Invoke();
    }

    private void UpdateTitle()
    {
        var title = Emulator.Title?.Trim();
        var shown = string.IsNullOrEmpty(title) ? _baseTitle : Shorten(title);
        Title = IsExited ? shown + " (exited)" : shown;
    }

    private static string Shorten(string title) => title.Length <= 28 ? title : title[..27] + "…";

    public void SendInput(string text)
    {
        if (IsExited || _disposed || string.IsNullOrEmpty(text)) return;
        _pty?.Write(text);
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Emulator.Cols && rows == Emulator.Rows) return;
        Emulator.Resize(cols, rows);
        if (!IsExited) _pty?.Resize(cols, rows);
        Changed?.Invoke();
    }

    /// <summary>Empties the visible screen and the scrollback (the shell redraws its prompt on the next Enter).</summary>
    public void ClearScreen()
    {
        Emulator.Write("\u001b[3J\u001b[H\u001b[2J");
        Changed?.Invoke();
    }

    /// <summary>The text of the last lines of output, for "ask the agent about this error".</summary>
    public string LastOutput(int maxLines = 40) => Emulator.GetLastLines(maxLines);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var pty = _pty;
        if (pty == null) return;
        pty.DataReceived -= OnData;
        pty.Exited -= OnExited;
        pty.Dispose();
    }
}
