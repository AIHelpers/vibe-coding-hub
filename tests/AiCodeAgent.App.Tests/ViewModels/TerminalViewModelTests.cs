using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Terminal;

namespace AiCodeAgent.App.Tests.ViewModels;

/// <summary>E4-S4: the terminal panel's tabs, shells and actions, with a fake pseudo-terminal.</summary>
public class TerminalViewModelTests
{
    private static string AnyShell => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
        : "/bin/sh";

    private static (TerminalViewModel Terminal, List<FakePty> Ptys) Create(string? workingDirectory = null)
    {
        var ptys = new List<FakePty>();
        var terminal = new TerminalViewModel(options =>
        {
            var pty = new FakePty(options);
            ptys.Add(pty);
            return pty;
        }, action => action());
        terminal.Configure(() => AnyShell, () => workingDirectory ?? Path.GetTempPath());
        return (terminal, ptys);
    }

    [Fact]
    public void Starts_WithOnlyTheAgentOutputTab_AndNoShell()
    {
        var (terminal, ptys) = Create();
        Assert.Single(terminal.Tabs);
        Assert.Equal("Agent output", terminal.Tabs[0].Title);
        Assert.True(terminal.IsAgentSelected);
        Assert.Empty(ptys);
        Assert.False(terminal.IsVisible);
    }

    [Fact]
    public void OpeningThePanel_StartsAShellInTheWorkingDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("term-vm").FullName;
        try
        {
            var (terminal, ptys) = Create(dir);
            terminal.ToggleVisibilityCommand.Execute(null);

            Assert.True(terminal.IsVisible);
            var pty = Assert.Single(ptys);
            Assert.Equal(dir, pty.Options.WorkingDirectory);
            Assert.Equal(AnyShell, pty.Options.FileName);
            Assert.NotNull(terminal.SelectedShell);
            Assert.True(terminal.IsShellSelected);
            Assert.False(terminal.IsAgentSelected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HidingThePanel_KeepsTheShellRunning_AndShowingItAgainDoesNotStartAnother()
    {
        var (terminal, ptys) = Create();
        terminal.ToggleVisibilityCommand.Execute(null);
        var shell = terminal.SelectedShell!;
        ptys[0].Emit("still here\r\n");

        terminal.ToggleVisibilityCommand.Execute(null);
        Assert.False(terminal.IsVisible);
        Assert.False(ptys[0].Disposed);
        ptys[0].Emit("while hidden\r\n");

        terminal.ToggleVisibilityCommand.Execute(null);
        Assert.Single(ptys);
        Assert.Same(shell, terminal.SelectedShell);
        Assert.Contains("while hidden", shell.Emulator.GetScreenText());
    }

    [Fact]
    public void SeveralShells_AreSeparateTabs_BeforeTheAgentOutputTab()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        terminal.NewShellCommand.Execute(null);

        Assert.Equal(2, ptys.Count);
        Assert.Equal(3, terminal.Tabs.Count);
        Assert.IsType<ShellTabViewModel>(terminal.Tabs[0]);
        Assert.IsType<ShellTabViewModel>(terminal.Tabs[1]);
        Assert.IsType<AgentOutputTabViewModel>(terminal.Tabs[2]);
        Assert.NotEqual(terminal.Tabs[0].Title, terminal.Tabs[1].Title);
        Assert.Same(terminal.Tabs[1], terminal.SelectedTab);

        ptys[0].Emit("first");
        ptys[1].Emit("second");
        Assert.Contains("first", ((ShellTabViewModel)terminal.Tabs[0]).Emulator.GetScreenText());
        Assert.DoesNotContain("second", ((ShellTabViewModel)terminal.Tabs[0]).Emulator.GetScreenText());
        Assert.Contains("second", ((ShellTabViewModel)terminal.Tabs[1]).Emulator.GetScreenText());
    }

    [Fact]
    public void ClosingATab_EndsItsShell_AndSelectsANeighbour_AndTheAgentTabCannotBeClosed()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        terminal.NewShellCommand.Execute(null);
        var first = terminal.Tabs[0];
        var second = terminal.Tabs[1];

        terminal.CloseTabCommand.Execute(second);
        Assert.True(ptys[1].Disposed);
        Assert.False(ptys[0].Disposed);
        Assert.Same(first, terminal.SelectedTab);

        terminal.CloseTabCommand.Execute(terminal.AgentTab);
        Assert.Contains(terminal.AgentTab, terminal.Tabs);

        terminal.CloseTabCommand.Execute(first);
        Assert.True(ptys[0].Disposed);
        Assert.Same(terminal.AgentTab, terminal.SelectedTab);
        Assert.Single(terminal.Tabs);
    }

    [Fact]
    public void ShellOutput_IsInterpreted_ColoursIncluded()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        var shell = terminal.SelectedShell!;
        var changed = 0;
        shell.Changed += () => changed++;

        ptys[0].Emit("\u001b[32mgreen\u001b[0m text");

        Assert.True(changed > 0);
        Assert.Contains("green text", shell.Emulator.GetScreenText());
        lock (shell.Emulator.SyncRoot)
        {
            var cell = shell.Emulator.Buffer.ScreenLine(0).Cells[0];
            Assert.True(cell.Fg.IsIndexed);
            Assert.Equal(2, cell.Fg.Index);
        }
    }

    [Fact]
    public void OutputSplitInsideACharacter_IsDecodedOnce()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        var bytes = System.Text.Encoding.UTF8.GetBytes("é→");
        foreach (var b in bytes) ptys[0].EmitBytes(b);
        Assert.Contains("é→", terminal.SelectedShell!.Emulator.GetScreenText());
    }

    [Fact]
    public void TerminalQueries_AreAnsweredToTheProgram()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        ptys[0].Emit("abc\u001b[6n");
        Assert.Contains("\u001b[1;4R", ptys[0].AllWritten);
    }

    [Fact]
    public void Typing_GoesToTheShell_NotToAnExitedOne()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        var shell = terminal.SelectedShell!;
        shell.SendInput("dotnet test\r");
        Assert.Equal("dotnet test\r", ptys[0].AllWritten);

        ptys[0].Exit(0);
        shell.SendInput("ignored");
        Assert.Equal("dotnet test\r", ptys[0].AllWritten);
    }

    [Fact]
    public void WhenTheProgramExits_TheTabSaysSo()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        var shell = terminal.SelectedShell!;
        var title = shell.Title;

        ptys[0].Exit(3);

        Assert.True(shell.IsExited);
        Assert.Equal(3, shell.ExitCode);
        Assert.Contains("exited", shell.Title);
        Assert.StartsWith(title, shell.Title);
        Assert.Contains("Process exited with code 3", shell.Emulator.GetScreenText());
    }

    [Fact]
    public void ResizingTheShell_ReachesTheEmulatorAndThePty()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        var shell = terminal.SelectedShell!;

        shell.Resize(132, 40);

        Assert.Equal(132, shell.Emulator.Cols);
        Assert.Equal(40, shell.Emulator.Rows);
        Assert.Equal((132, 40), ptys[0].LastResize);
    }

    [Fact]
    public void ATitleSetByTheProgram_BecomesTheTabTitle()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        ptys[0].Emit("\u001b]0;my-project - build\u0007");
        Assert.Equal("my-project - build", terminal.SelectedShell!.Title);
    }

    [Fact]
    public void AShellThatCannotStart_ExplainsItInsideTheTab()
    {
        var terminal = new TerminalViewModel(_ => throw new FileNotFoundException("no such shell"), action => action());
        terminal.Configure(() => AnyShell, () => Path.GetTempPath());

        terminal.NewShellCommand.Execute(null);

        var shell = terminal.SelectedShell!;
        Assert.True(shell.IsExited);
        Assert.Contains("Cannot start", shell.Emulator.GetScreenText());
        Assert.Contains("no such shell", shell.Emulator.GetScreenText());
    }

    [Fact]
    public void ConfiguredShell_IsUsed_WithItsArguments()
    {
        var (terminal, ptys) = Create();
        terminal.Configure(() => "\"" + AnyShell + "\" -x", () => Path.GetTempPath());
        terminal.NewShellCommand.Execute(null);
        Assert.Equal(AnyShell, ptys[0].Options.FileName);
        Assert.Equal(new[] { "-x" }, ptys[0].Options.Arguments);
    }

    [Fact]
    public void SendSelectionToChat_RaisesTheEvent_AndIgnoresEmptyText()
    {
        var (terminal, _) = Create();
        var received = new List<string>();
        terminal.SendToChatRequested += received.Add;

        terminal.SendSelectionToChat("error CS1002: ; expected\n");
        terminal.SendSelectionToChat("   ");

        Assert.Equal(new[] { "error CS1002: ; expected" }, received);
    }

    [Fact]
    public void AskAgent_UsesTheSelection_OrTheLastOutputOfTheShell()
    {
        var (terminal, ptys) = Create();
        var received = new List<string>();
        terminal.AskAgentRequested += received.Add;
        terminal.NewShellCommand.Execute(null);
        ptys[0].Emit("Build started\r\nerror CS0103: The name 'x' does not exist\r\n");

        terminal.AskAgentAbout("picked text");
        terminal.AskAgentAbout(null);

        Assert.Equal("picked text", received[0]);
        Assert.Contains("error CS0103", received[1]);
        Assert.Contains("Build started", received[1]);
    }

    [Fact]
    public void ClearOnTheAgentTab_EmptiesTheLog_OnAShellTab_TheScreen()
    {
        var (terminal, ptys) = Create();
        terminal.AppendCommand("dotnet build");
        terminal.AppendOutput("done");
        Assert.True(terminal.Entries.Count >= 3);
        terminal.ClearCommand.Execute(null);
        Assert.DoesNotContain(terminal.Entries, e => e.Text.Contains("dotnet build"));

        terminal.NewShellCommand.Execute(null);
        ptys[0].Emit("noise\r\n");
        terminal.ClearCommand.Execute(null);
        Assert.DoesNotContain("noise", terminal.SelectedShell!.Emulator.GetScreenText());
    }

    [Fact]
    public void ResizeBy_StaysWithinLimits()
    {
        var (terminal, _) = Create();
        terminal.TerminalHeight = 300;
        terminal.ResizeBy(100);
        Assert.Equal(400, terminal.TerminalHeight);
        terminal.ResizeBy(-10_000);
        Assert.True(terminal.TerminalHeight >= 100);
        terminal.ResizeBy(10_000);
        Assert.True(terminal.TerminalHeight <= 1000);
    }

    [Fact]
    public void Dispose_EndsEveryShell()
    {
        var (terminal, ptys) = Create();
        terminal.NewShellCommand.Execute(null);
        terminal.NewShellCommand.Execute(null);

        terminal.Dispose();

        Assert.All(ptys, p => Assert.True(p.Disposed));
    }

    [Fact]
    public void FocusIsRequested_WhenTheShellBecomesVisible()
    {
        var (terminal, _) = Create();
        var requests = 0;
        terminal.FocusRequested += () => requests++;
        terminal.ToggleVisibilityCommand.Execute(null);
        Assert.True(requests > 0);
    }
}

public class TerminalChatTextTests
{
    [Fact]
    public void ShortText_IsKept_AndCodeFencesInsideAreDefused()
    {
        Assert.Equal("error CS1002", AiCodeAgent.App.ViewModels.MainViewModel.TerminalTextForChat("error CS1002\n\n"));
        Assert.DoesNotContain("```", AiCodeAgent.App.ViewModels.MainViewModel.TerminalTextForChat("a ``` b"));
    }

    [Fact]
    public void LongText_KeepsTheEnd()
    {
        var text = new string('x', 20_000) + "THE-ERROR";
        var result = AiCodeAgent.App.ViewModels.MainViewModel.TerminalTextForChat(text);
        Assert.True(result.Length < 8100);
        Assert.EndsWith("THE-ERROR", result);
    }
}
