using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AiCodeAgent.Core.Terminal;

/// <summary>
/// A pseudo-terminal on Windows 10 (1809) and later: the ConPTY API (<c>CreatePseudoConsole</c>) gives a pair of pipes that
/// carry VT sequences, and the program is started attached to the pseudo console. Ctrl+C is the byte 0x03 on the input pipe.
/// </summary>
internal sealed class WindowsConPty : PtyProcessBase
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;
    private const int StartfUseStdHandles = 0x00000100;

    private readonly IntPtr _hPC;
    private readonly IntPtr _process;
    private readonly int _pid;
    private IntPtr _input;  // we write keystrokes here
    private IntPtr _output; // we read screen output here
    private readonly ManualResetEventSlim _processExited = new(false);
    private long _lastDataTicks = Environment.TickCount64;
    private int _exitCode = -1;
    private int _pseudoConsoleClosed;

    private WindowsConPty(IntPtr hPC, IntPtr process, int pid, IntPtr input, IntPtr output)
    {
        _hPC = hPC;
        _process = process;
        _pid = pid;
        _input = input;
        _output = output;
    }

    public override int ProcessId => _pid;

    public static WindowsConPty Start(PtyStartOptions options)
    {
        if (Environment.OSVersion.Version.Build < 17763)
            throw new PlatformNotSupportedException("The interactive terminal needs Windows 10 version 1809 or later (ConPTY).");

        var file = ShellLocator.FindExecutable(options.FileName)
                   ?? throw new FileNotFoundException($"Cannot find '{options.FileName}' to run in the terminal.", options.FileName);

        // two pipes: keystrokes in, screen output out
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var hr = CreatePseudoConsole(new Coord((short)options.Cols, (short)options.Rows), inputRead, outputWrite, 0, out var hPC);
        // the pseudo console owns these ends now
        CloseHandle(inputRead);
        CloseHandle(outputWrite);
        if (hr != 0)
        {
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            throw new Win32Exception(hr, "CreatePseudoConsole failed.");
        }

        var attributeList = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // fails by design and returns the size needed
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // STARTF_USESTDHANDLES with invalid handles: without it a program started from a process whose own standard handles
            // are redirected (a test host, a service) writes to those instead of to the pseudo console.
            var invalid = new IntPtr(-1);
            var startup = new StartupInfoEx
            {
                Info = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = StartfUseStdHandles, hStdInput = invalid, hStdOutput = invalid, hStdError = invalid },
                AttributeList = attributeList
            };

            environment = BuildEnvironmentBlock(PtyProcess.MergedEnvironment(options.Environment));
            var commandLine = new StringBuilder(BuildCommandLine(file, options.Arguments));
            var cwd = string.IsNullOrWhiteSpace(options.WorkingDirectory) || !Directory.Exists(options.WorkingDirectory) ? null : options.WorkingDirectory;

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environment, cwd, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot start '{file}'.");

            CloseHandle(info.hThread);
            var pty = new WindowsConPty(hPC, info.hProcess, info.dwProcessId, inputWrite, outputRead);
            pty.StartWatcher();
            pty.StartReader();
            return pty;
        }
        catch
        {
            ClosePseudoConsole(hPC);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    /// <summary>The program's end closes the pseudo console, which ends the output pipe and so the reader loop.</summary>
    private void StartWatcher()
    {
        var watcher = new Thread(() =>
        {
            WaitForSingleObject(_process, Infinite);
            GetExitCodeProcess(_process, out var code);
            _exitCode = unchecked((int)code);
            _processExited.Set();

            // Let the last output through before the pseudo console is closed.
            for (var i = 0; i < 20; i++)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks) > 100) break;
                Thread.Sleep(50);
            }

            ClosePseudoConsoleOnce();
        }) { IsBackground = true, Name = "conpty-watcher" };
        watcher.Start();
    }

    private void ClosePseudoConsoleOnce()
    {
        if (Interlocked.Exchange(ref _pseudoConsoleClosed, 1) == 0) ClosePseudoConsole(_hPC);
    }

    protected override int ReadCore(byte[] buffer)
    {
        if (!ReadFile(_output, buffer, (uint)buffer.Length, out var read, IntPtr.Zero)) return -1;
        Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
        return (int)read;
    }

    protected override void WriteCore(ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var chunk = new byte[bytes.Length - offset];
            Buffer.BlockCopy(bytes, offset, chunk, 0, chunk.Length);
            if (!WriteFile(_input, chunk, (uint)chunk.Length, out var written, IntPtr.Zero))
                throw new IOException("write to the terminal failed", new Win32Exception(Marshal.GetLastWin32Error()));
            offset += (int)written;
        }
    }

    protected override void ResizeCore(int cols, int rows)
    {
        if (Volatile.Read(ref _pseudoConsoleClosed) == 0)
            ResizePseudoConsole(_hPC, new Coord((short)Math.Clamp(cols, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
    }

    protected override void KillCore()
    {
        TerminateProcess(_process, 1);
        // Closing the pseudo console also ends everything attached to it; the reader keeps draining meanwhile.
        _ = Task.Run(ClosePseudoConsoleOnce);
    }

    protected override int WaitForChild()
    {
        _processExited.Wait();
        return _exitCode;
    }

    protected override void CleanupCore()
    {
        ClosePseudoConsoleOnce();
        CloseHandle(_input);
        CloseHandle(_output);
        CloseHandle(_process);
        _input = IntPtr.Zero;
        _output = IntPtr.Zero;
    }

    // ----- command line and environment -----

    /// <summary>The command line CreateProcess wants: the program and its arguments, each quoted when needed.</summary>
    internal static string BuildCommandLine(string file, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(file));
        foreach (var argument in arguments) sb.Append(' ').Append(Quote(argument));
        return sb.ToString();
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(List<KeyValuePair<string, string>> variables)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in variables.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    // ----- native -----

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public readonly short X;
        public readonly short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
