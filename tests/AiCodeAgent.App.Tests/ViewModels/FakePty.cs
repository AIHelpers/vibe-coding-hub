using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Terminal;

namespace AiCodeAgent.App.Tests.ViewModels;

/// <summary>A pseudo-terminal that records what is typed and lets a test print output and end the program.</summary>
internal sealed class FakePty : IPtyProcess
{
    private readonly TaskCompletionSource<int> _exit = new();

    public FakePty(PtyStartOptions options) => Options = options;

    public PtyStartOptions Options { get; }

    public List<string> Written { get; } = new();

    public (int Cols, int Rows)? LastResize { get; private set; }

    public bool Disposed { get; private set; }

    public bool Killed { get; private set; }

    public int ProcessId => 4242;

    public bool HasExited { get; private set; }

    public int? ExitCode { get; private set; }

    public event Action<byte[]>? DataReceived;

    public event Action<int>? Exited;

    public string AllWritten => string.Concat(Written);

    /// <summary>The program prints <paramref name="text"/>.</summary>
    public void Emit(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));

    /// <summary>The program prints raw bytes (to split a character across reads).</summary>
    public void EmitBytes(params byte[] bytes) => DataReceived?.Invoke(bytes);

    /// <summary>The program ends.</summary>
    public void Exit(int code)
    {
        HasExited = true;
        ExitCode = code;
        Exited?.Invoke(code);
        _exit.TrySetResult(code);
    }

    public void Write(ReadOnlySpan<byte> data) => Written.Add(Encoding.UTF8.GetString(data));

    public void Write(string text) => Written.Add(text);

    public void Resize(int cols, int rows) => LastResize = (cols, rows);

    public void Kill() => Killed = true;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => _exit.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
        Disposed = true;
        Killed = true;
    }
}
