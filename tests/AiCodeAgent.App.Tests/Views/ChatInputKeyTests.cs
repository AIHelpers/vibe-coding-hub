using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Controls;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>E2-S4: Enter / Ctrl+Enter send, Shift+Enter inserts a new line; the commit box keeps Ctrl+Enter.</summary>
public class ChatInputKeyTests
{
    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, true)]
    [InlineData(Key.Enter, KeyModifiers.Control, true)]
    [InlineData(Key.Enter, KeyModifiers.Shift, false)]
    [InlineData(Key.Enter, KeyModifiers.Alt, false)]
    [InlineData(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(Key.A, KeyModifiers.None, false)]
    public void IsSubmitGesture(Key key, KeyModifiers modifiers, bool expected)
    {
        Assert.Equal(expected, ChatInput.IsSubmitGesture(key, modifiers));
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.Enter, null);
        window.KeyRelease(key, modifiers, PhysicalKey.Enter, null);
        Pump();
    }

    private static (Window Window, TextBox Box, Func<int> Sent, Func<string?> LastSent) CreateChatBox()
    {
        var sent = 0;
        string? lastSent = null;
        var box = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 300 };
        // Like ChatViewModel.SendCommand: sends what is in the box, then clears it; nothing to send when empty.
        var send = new RelayCommand(() => { sent++; lastSent = box.Text; box.Text = string.Empty; });
        ChatInput.SetSubmitCommand(box, send);

        var window = new Window { Width = 400, Height = 200, Content = box };
        window.Show();
        box.Focus();
        Pump();
        return (window, box, () => sent, () => lastSent);
    }

    [AvaloniaFact]
    public void ShiftEnter_InsertsALineBreak_AndDoesNotSend()
    {
        var (window, box, sent, _) = CreateChatBox();

        window.KeyTextInput("first line");
        Press(window, Key.Enter, RawInputModifiers.Shift);
        window.KeyTextInput("second line");
        Pump();

        Assert.Equal(0, sent());
        var lines = (box.Text ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(new[] { "first line", "second line" }, lines);
    }

    [AvaloniaFact]
    public void Enter_Sends_WithoutAddingALineBreak()
    {
        var (window, box, sent, lastSent) = CreateChatBox();

        window.KeyTextInput("line 1");
        Press(window, Key.Enter, RawInputModifiers.Shift);
        window.KeyTextInput("line 2");
        Press(window, Key.Enter);

        Assert.Equal(1, sent());
        // The multi-line prompt went out exactly as typed: Shift+Enter's break is there, Enter added none.
        var lines = lastSent()!.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(new[] { "line 1", "line 2" }, lines);
        Assert.Equal(string.Empty, box.Text);
    }

    [AvaloniaFact]
    public void CtrlEnter_AlsoSends()
    {
        var (window, _, sent, lastSent) = CreateChatBox();

        window.KeyTextInput("hello");
        Press(window, Key.Enter, RawInputModifiers.Control);

        Assert.Equal(1, sent());
        Assert.Equal("hello", lastSent());
    }

    [AvaloniaFact]
    public void Enter_WhenTheCommandCannotRun_DoesNotInsertALineBreak()
    {
        var box = new TextBox { AcceptsReturn = true, Width = 300 };
        ChatInput.SetSubmitCommand(box, new RelayCommand(() => { }, () => false)); // e.g. the agent is busy
        var window = new Window { Width = 400, Height = 200, Content = box };
        window.Show();
        box.Focus();
        Pump();

        window.KeyTextInput("draft");
        Press(window, Key.Enter);

        Assert.Equal("draft", box.Text);
    }

    [AvaloniaFact]
    public void WithoutASubmitCommand_EnterIsAPlainNewLine()
    {
        var box = new TextBox { AcceptsReturn = true, Width = 300 };
        var window = new Window { Width = 400, Height = 200, Content = box };
        window.Show();
        box.Focus();
        Pump();

        window.KeyTextInput("a");
        Press(window, Key.Enter);
        window.KeyTextInput("b");
        Pump();

        Assert.Equal(2, (box.Text ?? string.Empty).Split('\n').Length);
    }

    // ── Regression: the Source Control commit box keeps Ctrl+Enter = commit ─────────────

    private static (bool Ok, string Output) Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, o);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    [AvaloniaFact]
    public async Task SourceControlCommitBox_CtrlEnterCommits_ShiftEnterAddsALine()
    {
        var repo = Path.Combine(Path.GetTempPath(), "sc-keys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        try
        {
            if (!Git(repo, "init", "-q", "-b", "main").Ok) return; // git not installed: nothing to check
            Git(repo, "config", "user.email", "t@example.com");
            Git(repo, "config", "user.name", "Test");
            Git(repo, "config", "commit.gpgsign", "false");
            Git(repo, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a\nmore\n");

            var explorer = new FileExplorerViewModel { EnableWatching = false, EnableGit = false, RootPath = repo };
            var vm = new SourceControlViewModel(explorer);
            await vm.RefreshAsync();

            var view = new SourceControlView { DataContext = vm };
            var window = new Window { Width = 400, Height = 700, Content = view };
            window.Show();
            Pump();

            var box = view.GetVisualDescendants().OfType<TextBox>()
                .Single(t => t.Watermark?.ToString()?.Contains("Ctrl+Enter", StringComparison.Ordinal) == true);
            box.Focus();
            Pump();

            window.KeyTextInput("Add a");
            Press(window, Key.Enter, RawInputModifiers.Shift);
            window.KeyTextInput("Body line");
            Pump();
            Assert.Equal(2, vm.CommitMessage.Split('\n').Length); // Shift+Enter: new line, no commit
            Assert.Equal("1", Git(repo, "rev-list", "--count", "HEAD").Output.Trim());

            Press(window, Key.Enter, RawInputModifiers.Control);
            if (vm.CommitCommand.ExecutionTask is { } running) await running;
            Pump();

            Assert.Equal("2", Git(repo, "rev-list", "--count", "HEAD").Output.Trim());
            // %B is the raw message: Shift+Enter's line break made it into the commit. (%s would join the
            // first paragraph's lines with a space, since there is no blank line after the subject.)
            var message = Git(repo, "log", "-1", "--format=%B").Output.Replace("\r", string.Empty).Trim();
            Assert.Equal("Add a\nBody line", message);
            Assert.Equal(string.Empty, vm.CommitMessage);
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(repo, true);
            }
            catch { /* best effort */ }
        }
    }
}
