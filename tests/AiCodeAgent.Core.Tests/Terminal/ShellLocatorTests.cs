using System.Linq;
using AiCodeAgent.Core.Terminal;
using Xunit;

namespace AiCodeAgent.Core.Tests.Terminal;

public class ShellLocatorTests
{
    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var map = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    private static Func<string, bool> Exists(params string[] files) => path => files.Contains(path);

    [Fact]
    public void Windows_PrefersPwsh_ThenWindowsPowerShell_ThenCmd()
    {
        var env = Env(("PATH", @"C:\Tools;C:\PS7"), ("SystemRoot", @"C:\Windows"), ("ComSpec", @"C:\Windows\System32\cmd.exe"));
        var pwsh = ShellLocator.Resolve(null, true, false, env, Exists(@"C:\PS7\pwsh.exe", @"C:\Windows\System32\cmd.exe"));
        Assert.Equal(@"C:\PS7\pwsh.exe", pwsh.FileName);
        Assert.Equal(new[] { "-NoLogo" }, pwsh.Arguments);

        var ps = ShellLocator.Resolve(null, true, false, env,
            Exists(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"));
        Assert.EndsWith("powershell.exe", ps.FileName);

        var cmd = ShellLocator.Resolve(null, true, false, env, Exists(@"C:\Windows\System32\cmd.exe"));
        Assert.Equal(@"C:\Windows\System32\cmd.exe", cmd.FileName);
        Assert.Empty(cmd.Arguments);
    }

    [Fact]
    public void Unix_UsesTheShellVariable_ThenBash_ThenSh()
    {
        var zsh = ShellLocator.Resolve(null, false, false, Env(("SHELL", "/usr/bin/zsh")), Exists("/usr/bin/zsh", "/bin/bash"));
        Assert.Equal("/usr/bin/zsh", zsh.FileName);
        Assert.Empty(zsh.Arguments);

        var bash = ShellLocator.Resolve(null, false, false, Env(), Exists("/bin/bash", "/bin/sh"));
        Assert.Equal("/bin/bash", bash.FileName);

        var sh = ShellLocator.Resolve(null, false, false, Env(("SHELL", "/gone/fish")), Exists("/bin/sh"));
        Assert.Equal("/bin/sh", sh.FileName);
    }

    [Fact]
    public void MacOs_StartsALoginShell()
    {
        var zsh = ShellLocator.Resolve(null, false, true, Env(("SHELL", "/bin/zsh")), Exists("/bin/zsh"));
        Assert.Equal(new[] { "-l" }, zsh.Arguments);
        var fish = ShellLocator.Resolve(null, false, true, Env(("SHELL", "/opt/homebrew/bin/fish")), Exists("/opt/homebrew/bin/fish"));
        Assert.Empty(fish.Arguments);
    }

    [Fact]
    public void ConfiguredCommandLine_WinsWhenTheProgramExists()
    {
        var exists = Exists("/usr/bin/fish", "/bin/bash");
        var r = ShellLocator.Resolve("/usr/bin/fish --private", false, false, Env(), exists);
        Assert.Equal("/usr/bin/fish", r.FileName);
        Assert.Equal(new[] { "--private" }, r.Arguments);

        var fallback = ShellLocator.Resolve("/missing/shell", false, false, Env(), exists);
        Assert.Equal("/bin/bash", fallback.FileName);
    }

    [Fact]
    public void ConfiguredName_IsFoundOnPath()
    {
        var r = ShellLocator.Resolve("nu", false, false, Env(("PATH", "/a:/b")), Exists("/b/nu"));
        Assert.Equal("/b/nu", r.FileName);
    }

    [Fact]
    public void Find_UsesPathExt_OnWindows()
    {
        var env = Env(("PATH", @"C:\x"), ("PATHEXT", ".COM;.EXE;.CMD"));
        Assert.Equal(@"C:\x\tool.CMD", ShellLocator.Find("tool", true, env, Exists(@"C:\x\tool.CMD")));
        Assert.Null(ShellLocator.Find("nothing", true, env, Exists()));
        Assert.Equal(@"C:\y\a.exe", ShellLocator.Find(@"C:\y\a.exe", true, env, Exists(@"C:\y\a.exe")));
    }

    [Fact]
    public void SplitCommandLine_HandlesQuotes()
    {
        Assert.Equal(new[] { "a", "b c", "d" }, ShellLocator.SplitCommandLine("a \"b c\"  d"));
        Assert.Equal(new[] { @"C:\Program Files\PowerShell\7\pwsh.exe", "-NoLogo" }, ShellLocator.SplitCommandLine("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo"));
        Assert.Equal(new[] { "x", "it is" }, ShellLocator.SplitCommandLine("x 'it is'"));
        Assert.Equal(new[] { "" }, ShellLocator.SplitCommandLine("\"\""));
        Assert.Empty(ShellLocator.SplitCommandLine("   "));
    }

    [Fact]
    public void Display_ShowsTheWholeCommand()
    {
        Assert.Equal("pwsh -NoLogo", new ShellCommand("pwsh", new[] { "-NoLogo" }).Display);
        Assert.Equal("bash", new ShellCommand("bash", System.Array.Empty<string>()).Display);
    }
}
