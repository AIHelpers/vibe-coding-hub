using System.Text;
using AiCodeAgent.Core.Terminal;
using Xunit;

namespace AiCodeAgent.Core.Tests.Terminal;

/// <summary>Runs real programs on a real pseudo-terminal (ConPTY on Windows, forkpty on Linux/macOS).</summary>
public class PtySessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private sealed class Session : IDisposable
    {
        public readonly IPtyProcess Pty;
        public readonly TerminalEmulator Term;
        private readonly ManualResetEventSlim _exited = new(false);

        public Session(string file, string[] args, int cols = 80, int rows = 24)
        {
            Term = new TerminalEmulator(cols, rows, 1000);
            Term.Reply += text => Pty?.Write(text);
            Pty = PtyProcess.Start(new PtyStartOptions { FileName = file, Arguments = args, Cols = cols, Rows = rows, WorkingDirectory = Path.GetTempPath() });
            Pty.DataReceived += bytes => { lock (_decoder) { _raw.Append(BitConverter.ToString(bytes.Take(64).ToArray())).Append(" | "); Term.Write(Decode(bytes)); } };
            Pty.Exited += _ => _exited.Set();
        }

        private readonly StringBuilder _raw = new();

        /// <summary>What a failed assertion should show: the screen and the first bytes of each chunk received.</summary>
        public string Describe() => "screen=[" + Screen + "] exited=" + Pty.HasExited + " code=" + Pty.ExitCode + " raw=" + _raw;

        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

        private string Decode(byte[] bytes)
        {
            var chars = new char[_decoder.GetCharCount(bytes, 0, bytes.Length)];
            var n = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
            return new string(chars, 0, n);
        }

        public string Screen { get { lock (Term.SyncRoot) return Term.GetScreenText(); } }

        public bool WaitForText(string text)
        {
            var until = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < until)
            {
                if (Screen.Contains(text)) return true;
                Thread.Sleep(25);
            }

            return false;
        }

        public bool WaitForExit() => _exited.Wait(Timeout);

        public void Dispose() => Pty.Dispose();
    }

    private static Session Shell(string unixScript, string windowsScript = "") =>
        OperatingSystem.IsWindows()
            ? new Session("cmd.exe", new[] { "/c", windowsScript })
            : new Session("/bin/sh", new[] { "-c", unixScript });

    [Fact]
    public void Start_Echo_AndExitCode()
    {
        using var s = Shell("echo hello-pty; exit 3", "echo hello-pty & exit /b 3");
        Assert.True(s.WaitForExit(), "the program did not exit");
        Assert.Equal(3, s.Pty.ExitCode);
        Assert.True(s.Pty.HasExited);
        Assert.True(s.WaitForText("hello-pty"), s.Describe());
    }

    [Fact]
    public void WaitForExitAsync_ReportsTheExitCode()
    {
        using var s = Shell("exit 7", "exit /b 7");
        Assert.Equal(7, s.Pty.WaitForExitAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void Interactive_ShellEchoesWhatIsTyped()
    {
        using var s = OperatingSystem.IsWindows()
            ? new Session("cmd.exe", new[] { "/q" })
            : new Session("/bin/sh", Array.Empty<string>());
        s.Pty.Write("echo from-the-keyboard\r");
        Assert.True(s.WaitForText("from-the-keyboard"), s.Describe());
        s.Pty.Write("exit 5\r");
        Assert.True(s.WaitForExit(), "the shell did not exit");
        Assert.Equal(5, s.Pty.ExitCode);
    }

    [Fact]
    public void Colours_ArriveAsSgrAndAreInterpreted()
    {
        if (OperatingSystem.IsWindows()) return; // printf differs; ConPTY colour passthrough is covered by the shell tests
        using var s = new Session("/bin/sh", new[] { "-c", "printf '\\033[31mred\\033[0m plain'" });
        Assert.True(s.WaitForExit());
        Assert.True(s.WaitForText("red plain"));
        lock (s.Term.SyncRoot)
        {
            var cell = s.Term.Buffer.ScreenLine(0).Cells[0];
            Assert.Equal((int)'r', cell.Rune);
            Assert.True(cell.Fg.IsIndexed);
            Assert.Equal(1, cell.Fg.Index);
        }
    }

    [Fact]
    public void CtrlC_InterruptsARunningProgram()
    {
        if (OperatingSystem.IsWindows())
        {
            // ping's text is localised, so the test watches for markers it types itself: %OS% is only expanded in the output.
            using var w = new Session("cmd.exe", new[] { "/q" });
            w.Pty.Write("echo ready-%OS%\r");
            Assert.True(w.WaitForText("ready-Windows_NT"), w.Describe());
            w.Pty.Write("ping -n 60 127.0.0.1\r");
            Thread.Sleep(1500);
            w.Pty.Write(new byte[] { 0x03 });
            Thread.Sleep(500);
            w.Pty.Write("echo after-%OS%\r");
            Assert.True(w.WaitForText("after-Windows_NT"), w.Describe());
            return;
        }

        using var s = new Session("/bin/sh", Array.Empty<string>());
        s.Pty.Write("echo ready; sleep 30; echo after-sleep\r");
        Assert.True(s.WaitForText("ready"));
        Thread.Sleep(300);
        s.Pty.Write(new byte[] { 0x03 }); // ETX: the line discipline turns it into SIGINT for the foreground job
        s.Pty.Write("echo status=$?\r");
        Assert.True(s.WaitForText("status=130"));
        Assert.DoesNotContain("after-sleep", s.Screen.Replace("echo ready; sleep 30; echo after-sleep", ""));
    }

    [Fact]
    public void Resize_IsSeenByTheProgram()
    {
        if (!OperatingSystem.IsLinux()) return; // the macOS ioctl needs a variadic call that is not available through a plain function pointer
        using var s = new Session("/bin/sh", Array.Empty<string>(), 80, 24);
        s.Pty.Write("stty size\r");
        Assert.True(s.WaitForText("24 80"));
        s.Pty.Resize(100, 30);
        Thread.Sleep(200);
        s.Pty.Write("stty size\r");
        Assert.True(s.WaitForText("30 100"));
    }

    [Fact]
    public void WorkingDirectory_IsUsed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var s = new Session("/bin/sh", new[] { "-c", "pwd -P" });
        Assert.True(s.WaitForExit());
        var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd('/');
        Assert.True(s.WaitForText(Path.GetFileName(expected)), s.Screen);
    }

    [Fact]
    public void Kill_EndsAProgramThatIgnoresNothing()
    {
        using var s = OperatingSystem.IsWindows()
            ? new Session("cmd.exe", new[] { "/c", "ping -n 60 127.0.0.1" })
            : new Session("/bin/sh", new[] { "-c", "sleep 60" });
        Thread.Sleep(300);
        s.Pty.Kill();
        Assert.True(s.WaitForExit(), "the program survived Kill");
        Assert.True(s.Pty.HasExited);
    }

    [Fact]
    public void Start_UnknownProgram_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => PtyProcess.Start(new PtyStartOptions { FileName = "definitely-not-a-program-xyz" }));
    }

    [Fact]
    public void Dispose_KillsTheShell()
    {
        var s = OperatingSystem.IsWindows()
            ? new Session("cmd.exe", new[] { "/q" })
            : new Session("/bin/sh", Array.Empty<string>());
        var pty = s.Pty;
        s.Dispose();
        Assert.True(pty.WaitForExitAsync().Wait(Timeout));
    }
}
