using System.Runtime.InteropServices;
using System.Text;

namespace AiCodeAgent.Core.Terminal;

/// <summary>
/// A pseudo-terminal on Linux/macOS/BSD: <c>forkpty</c> creates the pty pair, makes the child a session leader with the pty
/// as its controlling terminal (so Ctrl+C, job control and window-size changes work like in any terminal), and the child
/// runs <c>execve</c>. All calls go through function pointers resolved before the fork, so the child needs no runtime
/// work between <c>fork</c> and <c>exec</c>; the fork itself happens inside a no-GC region so a collection cannot start
/// while only one thread exists in the child.
/// </summary>
internal sealed unsafe class UnixPty : PtyProcessBase
{
    private const int SigHup = 1;
    private const int SigKill = 9;
    private const int Eintr = 4;
    private const int Eagain = 11;

    private readonly int _master;
    private readonly int _pid;

    private UnixPty(int master, int pid)
    {
        _master = master;
        _pid = pid;
    }

    public override int ProcessId => _pid;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    public static UnixPty Start(PtyStartOptions options)
    {
        _ = Libc.Loaded; // resolve every native function before the fork

        var file = ShellLocator.FindExecutable(options.FileName)
                   ?? throw new FileNotFoundException($"Cannot find '{options.FileName}' to run in the terminal.", options.FileName);
        var cwd = string.IsNullOrWhiteSpace(options.WorkingDirectory) || !Directory.Exists(options.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : options.WorkingDirectory;

        var allocations = new List<nint>();
        byte* Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            for (var i = 0; i < bytes.Length; i++) p[i] = bytes[i];
            p[bytes.Length] = 0;
            allocations.Add((nint)p);
            return p;
        }

        byte** Table(IReadOnlyList<string> items)
        {
            var table = (byte**)NativeMemory.Alloc((nuint)((items.Count + 1) * sizeof(byte*)));
            for (var i = 0; i < items.Count; i++) table[i] = Utf8(items[i]);
            table[items.Count] = null;
            allocations.Add((nint)table);
            return table;
        }

        var pFile = Utf8(file);
        var pCwd = Utf8(cwd);
        var argv = Table(new[] { file }.Concat(options.Arguments).ToList());
        var envp = Table(PtyProcess.MergedEnvironment(options.Environment).Select(kv => kv.Key + "=" + kv.Value).ToList());
        var emptySigSet = (byte*)NativeMemory.AllocZeroed(128);
        allocations.Add((nint)emptySigSet);

        var size = new WinSize { Rows = (ushort)Math.Clamp(options.Rows, 1, 1000), Cols = (ushort)Math.Clamp(options.Cols, 1, 1000) };
        var sigSetMask = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 3 : 2; // SIG_SETMASK

        // The calls the child makes must be compiled and have their transition stubs before the fork: the child has one thread,
        // so it cannot JIT. Harmless arguments make every call fail without side effects.
        Libc.Chdir(null);
        Libc.Execve(null, null, null);
        Libc.Sigprocmask(99, null, null);
        Libc.Signal(-1, 0);
        _ = Libc.Getpid();

        var noGc = false;
        try { noGc = GC.TryStartNoGCRegion(1_000_000); }
        catch { /* not available: go without */ }

        int master, pid;
        try
        {
            pid = ForkAndExec(&master, &size, pFile, pCwd, argv, envp, emptySigSet, sigSetMask);
        }
        finally
        {
            if (noGc)
            {
                try { GC.EndNoGCRegion(); }
                catch { /* the region ended on its own */ }
            }
        }

        foreach (var p in allocations) NativeMemory.Free((void*)p);

        if (pid < 0) throw new IOException("forkpty failed (errno " + Marshal.GetLastSystemError() + ").");

        var pty = new UnixPty(master, pid);
        pty.StartReader();
        return pty;
    }

    /// <summary>Forks; the child never returns. Kept free of try blocks and allocations so it is plain compiled code in the child.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ForkAndExec(int* master, WinSize* size, byte* file, byte* cwd, byte** argv, byte** envp, byte* emptySigSet, int sigSetMask)
    {
        var pid = Libc.Forkpty(master, null, null, size);
        if (pid != 0) return pid;

        // ---- child: nothing but plain native calls from here to exec ----
        Libc.Signal(13, 0);                              // SIGPIPE back to the default (the runtime ignores it)
        Libc.Sigprocmask(sigSetMask, emptySigSet, null); // unblock every signal
        Libc.Chdir(cwd);
        Libc.Execve(file, argv, envp);
        Libc.Kill(Libc.Getpid(), SigKill);               // exec failed
        return 0;
    }

    protected override int ReadCore(byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            var failures = 0;
            while (true)
            {
                var n = Libc.Read(_master, p, (nuint)buffer.Length);
                if (n >= 0) return (int)n;
                if (Marshal.GetLastSystemError() == Eintr) continue;

                // EIO: the program side is closed. On Linux the last output can still be on its way from the tty's
                // buffers when the first EIO is reported, so ask a few more times before calling it the end.
                if (++failures > 3) return -1;
                Thread.Sleep(10);
            }
        }
    }

    protected override void WriteCore(ReadOnlySpan<byte> data)
    {
        fixed (byte* start = data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var n = Libc.Write(_master, start + offset, (nuint)(data.Length - offset));
                if (n >= 0)
                {
                    offset += (int)n;
                    continue;
                }

                var errno = Marshal.GetLastSystemError();
                if (errno == Eintr) continue;
                if (errno == Eagain)
                {
                    Thread.Sleep(2);
                    continue;
                }

                throw new IOException("write to the terminal failed (errno " + errno + ").");
            }
        }
    }

    protected override void ResizeCore(int cols, int rows)
    {
        var size = new WinSize { Rows = (ushort)Math.Clamp(rows, 1, 1000), Cols = (ushort)Math.Clamp(cols, 1, 1000) };
        var request = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 0x80087467u : 0x5414u; // TIOCSWINSZ
        // The kernel then sends SIGWINCH to the foreground process group, which makes the shell redraw.
        Libc.Ioctl(_master, request, &size);
    }

    protected override void KillCore()
    {
        Signal(SigHup);
        ScheduleHardKill();
    }

    /// <summary>SIGKILL for whatever ignored the hang-up; kept out of the unsafe class body so it can await.</summary>
    private void ScheduleHardKill()
    {
        var pid = _pid;
        _ = Task.Run(() => HardKillAfterDelay(pid));
    }

    private void HardKillAfterDelay(int pid)
    {
        Thread.Sleep(500);
        if (HasExited) return;
        Signal(SigKill);
        Libc.Kill(pid, SigKill);
    }

    /// <summary>Signals the shell's process group and the terminal's foreground job (what the user would be running).</summary>
    private void Signal(int signal)
    {
        if (HasExited) return;
        var foreground = Libc.Tcgetpgrp(_master);
        if (foreground > 1) Libc.Kill(-foreground, signal);
        Libc.Kill(-_pid, signal);
    }

    protected override int WaitForChild()
    {
        int status;
        while (true)
        {
            var r = Libc.Waitpid(_pid, &status, 0);
            if (r == _pid) break;
            if (r == -1 && Marshal.GetLastSystemError() == Eintr) continue;
            return -1;
        }

        return (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
    }

    protected override void CleanupCore() => Libc.Close(_master);

    /// <summary>The C library functions, looked up once.</summary>
    private static class Libc
    {
        public static readonly delegate* unmanaged[Cdecl]<int*, byte*, void*, WinSize*, int> Forkpty;
        public static readonly delegate* unmanaged[Cdecl]<int, byte*, nuint, nint> Read;
        public static readonly delegate* unmanaged[Cdecl]<int, byte*, nuint, nint> Write;
        public static readonly delegate* unmanaged[Cdecl]<int, int> Close;
        public static readonly delegate* unmanaged[Cdecl]<int, int, int> Kill;
        public static readonly delegate* unmanaged[Cdecl]<int, int*, int, int> Waitpid;
        public static readonly delegate* unmanaged[Cdecl]<int, nuint, void*, int> Ioctl;
        public static readonly delegate* unmanaged[Cdecl]<int, int> Tcgetpgrp;
        public static readonly delegate* unmanaged[Cdecl]<byte*, int> Chdir;
        public static readonly delegate* unmanaged[Cdecl]<byte*, byte**, byte**, int> Execve;
        public static readonly delegate* unmanaged[Cdecl]<int> Getpid;
        public static readonly delegate* unmanaged[Cdecl]<int, nint, nint> Signal;
        public static readonly delegate* unmanaged[Cdecl]<int, void*, void*, int> Sigprocmask;

        public static readonly bool Loaded = true;

        static Libc()
        {
            var libc = LoadFirst(
                "libc.so.6", "libSystem.dylib", "/usr/lib/libSystem.B.dylib", "libc.so.7",
                "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc");
            var util = nint.Zero;
            if (!NativeLibrary.TryGetExport(libc, "forkpty", out _))
                util = LoadFirst("libutil.so.1", "libutil.so", "libutil");

            Forkpty = (delegate* unmanaged[Cdecl]<int*, byte*, void*, WinSize*, int>)Export(util != nint.Zero ? util : libc, "forkpty");
            Read = (delegate* unmanaged[Cdecl]<int, byte*, nuint, nint>)Export(libc, "read");
            Write = (delegate* unmanaged[Cdecl]<int, byte*, nuint, nint>)Export(libc, "write");
            Close = (delegate* unmanaged[Cdecl]<int, int>)Export(libc, "close");
            Kill = (delegate* unmanaged[Cdecl]<int, int, int>)Export(libc, "kill");
            Waitpid = (delegate* unmanaged[Cdecl]<int, int*, int, int>)Export(libc, "waitpid");
            Ioctl = (delegate* unmanaged[Cdecl]<int, nuint, void*, int>)Export(libc, "ioctl");
            Tcgetpgrp = (delegate* unmanaged[Cdecl]<int, int>)Export(libc, "tcgetpgrp");
            Chdir = (delegate* unmanaged[Cdecl]<byte*, int>)Export(libc, "chdir");
            Execve = (delegate* unmanaged[Cdecl]<byte*, byte**, byte**, int>)Export(libc, "execve");
            Getpid = (delegate* unmanaged[Cdecl]<int>)Export(libc, "getpid");
            Signal = (delegate* unmanaged[Cdecl]<int, nint, nint>)Export(libc, "signal");
            Sigprocmask = (delegate* unmanaged[Cdecl]<int, void*, void*, int>)Export(libc, "sigprocmask");
        }

        private static nint LoadFirst(params string[] names)
        {
            foreach (var name in names)
                if (NativeLibrary.TryLoad(name, out var handle))
                    return handle;
            throw new DllNotFoundException("Cannot load the C library (tried " + string.Join(", ", names) + ").");
        }

        private static void* Export(nint library, string name) => (void*)NativeLibrary.GetExport(library, name);
    }
}
