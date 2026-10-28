using System.Text;

namespace AiCodeAgent.Core.Terminal;

/// <summary>How to start a program on a pseudo-terminal.</summary>
public sealed class PtyStartOptions
{
    /// <summary>Program to run: a path, or a name found on PATH.</summary>
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Working directory; the current directory of this process when null.</summary>
    public string? WorkingDirectory { get; init; }

    public int Cols { get; init; } = 80;

    public int Rows { get; init; } = 24;

    /// <summary>Environment changes on top of this process's environment; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>A program running on a pseudo-terminal (ConPTY on Windows, a pty from <c>forkpty</c> elsewhere).</summary>
public interface IPtyProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>; a program killed by a signal reports 128 + the signal number.</summary>
    int? ExitCode { get; }

    /// <summary>Raw output bytes (UTF-8), on a background thread.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Raised once, after the last output was delivered, with the exit code.</summary>
    event Action<int>? Exited;

    /// <summary>Sends keystrokes / pasted text to the program.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Write(string text);

    void Resize(int cols, int rows);

    /// <summary>Ends the program and everything it started on this terminal.</summary>
    void Kill();

    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Starts programs on a pseudo-terminal.</summary>
public static class PtyProcess
{
    public static IPtyProcess Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows()) return WindowsConPty.Start(options);
        return UnixPty.Start(options);
    }

    /// <summary>The environment a terminal program expects (colours, terminal type) on top of the caller's own.</summary>
    public static Dictionary<string, string?> DefaultEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var env = new Dictionary<string, string?>
        {
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "VibeCodingHub"
        };
        if (!OperatingSystem.IsWindows()) env["TERM"] = "xterm-256color";
        if (overrides != null)
            foreach (var (key, value) in overrides)
                env[key] = value;
        return env;
    }

    /// <summary>This process's environment with <paramref name="overrides"/> applied, as name/value pairs.</summary>
    internal static List<KeyValuePair<string, string>> MergedEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var merged = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) merged[key] = value;
        }

        foreach (var (key, value) in DefaultEnvironment(overrides))
        {
            if (value == null) merged.Remove(key);
            else merged[key] = value;
        }

        return merged.ToList();
    }
}

/// <summary>Shared plumbing: a reader thread that delivers output, then waits for the exit and raises <see cref="Exited"/>.</summary>
internal abstract class PtyProcessBase : IPtyProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private Thread? _reader;
    private int _disposed;

    public abstract int ProcessId { get; }

    public bool HasExited { get; private set; }

    public int? ExitCode { get; private set; }

    private readonly object _eventLock = new();
    private Action<byte[]>? _dataReceived;
    private Action<int>? _exited;
    private List<byte[]>? _pending = new();

    /// <summary>
    /// Output that arrives before the first subscriber is kept and handed to it when it subscribes, so a program that prints
    /// and exits at once is not missed (the reader starts inside <c>Start</c>, before the caller can subscribe).
    /// </summary>
    public event Action<byte[]>? DataReceived
    {
        add
        {
            List<byte[]>? replay;
            lock (_eventLock)
            {
                replay = _pending;
                _pending = null;
                _dataReceived += value;
            }

            if (replay != null && value != null)
                foreach (var chunk in replay)
                    value(chunk);
        }
        remove
        {
            lock (_eventLock) _dataReceived -= value;
        }
    }

    /// <summary>Raised once the program ended and all its output was delivered; a late subscriber is called at once.</summary>
    public event Action<int>? Exited
    {
        add
        {
            var already = false;
            lock (_eventLock)
            {
                if (HasExited && _exitRaised) already = true;
                else _exited += value;
            }

            if (already) value?.Invoke(ExitCode ?? -1);
        }
        remove
        {
            lock (_eventLock) _exited -= value;
        }
    }

    private bool _exitRaised;

    private void DeliverData(byte[] chunk)
    {
        Action<byte[]>? handler;
        lock (_eventLock)
        {
            handler = _dataReceived;
            if (handler == null)
            {
                _pending?.Add(chunk);
                return;
            }
        }

        handler(chunk);
    }

    /// <summary>Reads into <paramref name="buffer"/>; 0 or less means the terminal is closed.</summary>
    protected abstract int ReadCore(byte[] buffer);

    protected abstract void WriteCore(ReadOnlySpan<byte> data);

    protected abstract void ResizeCore(int cols, int rows);

    protected abstract void KillCore();

    /// <summary>Blocks until the program is gone and returns its exit code.</summary>
    protected abstract int WaitForChild();

    /// <summary>Releases handles; called once, after the reader thread finished.</summary>
    protected abstract void CleanupCore();

    protected void StartReader()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-reader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n;
                try { n = ReadCore(buffer); }
                catch { break; }
                if (n <= 0) break;
                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                try { DeliverData(chunk); }
                catch { /* a subscriber must not kill the reader */ }
            }
        }
        finally
        {
            var code = -1;
            try { code = WaitForChild(); }
            catch { /* keep -1 */ }
            ExitCode = code;
            HasExited = true;
            try { CleanupCore(); }
            catch { /* best effort */ }
            Action<int>? exited;
            lock (_eventLock)
            {
                _exitRaised = true;
                exited = _exited;
            }

            try { exited?.Invoke(code); }
            catch { /* ignore */ }
            _exit.TrySetResult(code);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (HasExited || data.IsEmpty) return;
        lock (_writeLock)
        {
            try { WriteCore(data); }
            catch (Exception) when (HasExited) { /* the program ended while we wrote */ }
        }
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        if (HasExited) return;
        ResizeCore(Math.Max(1, cols), Math.Max(1, rows));
    }

    public void Kill()
    {
        if (HasExited) return;
        try { KillCore(); }
        catch { /* already gone */ }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        // The reader ends when the program is gone; do not wait for it forever.
        _reader?.Join(TimeSpan.FromSeconds(2));
        GC.SuppressFinalize(this);
    }
}
