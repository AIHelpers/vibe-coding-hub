using System.Linq;
using AiCodeAgent.Core.Terminal;
using Xunit;

namespace AiCodeAgent.Core.Tests.Terminal;

public class TerminalEmulatorTests
{
    private const string Esc = "\u001b";

    private static TerminalEmulator New(int cols = 40, int rows = 10, int scrollback = 1000) => new(cols, rows, scrollback);

    private static TermCell Cell(TerminalEmulator t, int row, int col) => t.Buffer.ScreenLine(row).Cells[col];

    [Fact]
    public void PlainText_AndNewlines()
    {
        var t = New();
        t.Write("hello\r\nworld");
        Assert.Equal("hello\nworld", t.GetScreenText());
        Assert.Equal((1, 5), t.CursorPosition);
    }

    [Fact]
    public void LineFeedAlone_KeepsTheColumn_LikeARealTerminal()
    {
        var t = New();
        t.Write("ab\ncd");
        Assert.Equal("ab\n  cd", t.GetScreenText());
    }

    [Fact]
    public void CarriageReturn_Overwrites_AndBackspaceMovesLeft()
    {
        var t = New();
        t.Write("hello\rJ");
        Assert.Equal("Jello", t.GetScreenText());
        t.Write("\b\bXY"); // backspace stops at the left edge
        Assert.Equal("XYllo", t.GetScreenText());
    }

    [Fact]
    public void Tab_JumpsToTheNextMultipleOfEight()
    {
        var t = New();
        t.Write("a\tb");
        Assert.Equal((0, 9), t.CursorPosition);
    }

    [Fact]
    public void Sgr_BasicAndBrightColours()
    {
        var t = New();
        t.Write(Esc + "[31mR" + Esc + "[92mG" + Esc + "[0mN");
        Assert.Equal(TermColor.Indexed(1), Cell(t, 0, 0).Fg);
        Assert.Equal(TermColor.Indexed(10), Cell(t, 0, 1).Fg);
        Assert.True(Cell(t, 0, 2).Fg.IsDefault);
    }

    [Fact]
    public void Sgr_256AndTrueColour_BothSyntaxes()
    {
        var t = New();
        t.Write(Esc + "[38;5;208mA" + Esc + "[48;2;10;20;30mB" + Esc + "[38:2::1:2:3mC" + Esc + "[38:5:99mD");
        Assert.Equal(TermColor.Indexed(208), Cell(t, 0, 0).Fg);
        Assert.Equal(TermColor.Rgb(10, 20, 30), Cell(t, 0, 1).Bg);
        Assert.Equal(TermColor.Rgb(1, 2, 3), Cell(t, 0, 2).Fg);
        Assert.Equal(TermColor.Indexed(99), Cell(t, 0, 3).Fg);
    }

    [Fact]
    public void Sgr_Attributes_SetAndClear()
    {
        var t = New();
        t.Write(Esc + "[1;3;4mA" + Esc + "[22;23;24mB" + Esc + "[7mC");
        Assert.Equal(CellAttr.Bold | CellAttr.Italic | CellAttr.Underline, Cell(t, 0, 0).Attr);
        Assert.Equal(CellAttr.None, Cell(t, 0, 1).Attr);
        Assert.Equal(CellAttr.Inverse, Cell(t, 0, 2).Attr);
    }

    [Fact]
    public void Sgr_EmptyParameterMeansReset()
    {
        var t = New();
        t.Write(Esc + "[31mA" + Esc + "[mB");
        Assert.True(Cell(t, 0, 1).Fg.IsDefault);
    }

    [Fact]
    public void CursorPosition_AndClamping()
    {
        var t = New();
        t.Write(Esc + "[3;5Hx");
        Assert.Equal('x', Cell(t, 2, 4).DisplayChar);
        t.Write(Esc + "[999;999H");
        Assert.Equal((9, 39), t.CursorPosition);
        t.Write(Esc + "[H");
        Assert.Equal((0, 0), t.CursorPosition);
    }

    [Fact]
    public void CursorMoves_Relative()
    {
        var t = New();
        t.Write(Esc + "[5;5H" + Esc + "[2A" + Esc + "[3C" + Esc + "[1B" + Esc + "[2D");
        Assert.Equal((3, 5), t.CursorPosition);
    }

    [Fact]
    public void EraseInLine_AndDisplay()
    {
        var t = New();
        t.Write("abcdef\r\nghijkl");
        t.Write(Esc + "[1;3H" + Esc + "[K");
        Assert.Equal("ab\nghijkl", t.GetScreenText());
        t.Write(Esc + "[2J");
        Assert.Equal(string.Empty, t.GetScreenText());
    }

    [Fact]
    public void EraseUsesTheCurrentBackground()
    {
        var t = New();
        t.Write(Esc + "[44m" + Esc + "[K");
        Assert.Equal(TermColor.Indexed(4), Cell(t, 0, 20).Bg);
    }

    [Fact]
    public void Wrapping_MarksTheRow_AndTextIsJoinedAgain()
    {
        var t = New(cols: 10, rows: 5);
        t.Write("0123456789ABC");
        Assert.True(t.Buffer.ScreenLine(0).Wrapped);
        Assert.Equal((1, 3), t.CursorPosition);
        Assert.Equal("0123456789ABC", t.GetText(t.Buffer.TrimmedCount + t.Buffer.Top, 0, t.Buffer.TrimmedCount + t.Buffer.Top + 1, 10));
    }

    [Fact]
    public void WritingInTheLastColumn_WrapsOnlyWithTheNextCharacter()
    {
        var t = New(cols: 5, rows: 3);
        t.Write("abcde");
        Assert.Equal((0, 4), t.CursorPosition);
        t.Write("f");
        Assert.Equal((1, 1), t.CursorPosition);
    }

    [Fact]
    public void ScrollbackKeepsTheLinesThatLeaveTheScreen()
    {
        var t = New(cols: 20, rows: 5);
        for (var i = 1; i <= 12; i++) t.Write($"line{i}\r\n");
        Assert.Equal(8, t.Buffer.ScrollbackCount);
        Assert.Contains("line1", t.GetLastLines(50));
        Assert.Equal("line11\nline12", t.GetLastLines(2));
    }

    [Fact]
    public void ScrollbackIsCapped()
    {
        var t = New(cols: 20, rows: 3, scrollback: 10);
        for (var i = 1; i <= 2000; i++) t.Write($"x{i}\r\n");
        Assert.True(t.Buffer.ScrollbackCount <= 10 + 256);
        Assert.True(t.Buffer.TrimmedCount > 0);
    }

    [Fact]
    public void ScrollRegion_ScrollsOnlyInsideTheMargins()
    {
        var t = New(cols: 10, rows: 6);
        t.Write("top\r\n");
        t.Write(Esc + "[2;4r");              // region rows 2..4
        t.Write(Esc + "[2;1Ha\r\nb\r\nc\r\nd");
        Assert.Equal("top\nb\nc\nd", t.GetScreenText());
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void ReverseIndex_AtTheTopScrollsDown()
    {
        var t = New(cols: 10, rows: 3);
        t.Write("a\r\nb\r\nc" + Esc + "[H" + Esc + "M");
        Assert.Equal("\na\nb", t.GetScreenText());
    }

    [Fact]
    public void InsertAndDeleteLines_AndChars()
    {
        var t = New(cols: 10, rows: 5);
        t.Write("1\r\n2\r\n3");
        t.Write(Esc + "[2;1H" + Esc + "[L");
        Assert.Equal("1\n\n2\n3", t.GetScreenText());
        t.Write(Esc + "[M");
        Assert.Equal("1\n2\n3", t.GetScreenText());
        t.Write(Esc + "[1;1Habcdef" + Esc + "[1;2H" + Esc + "[2P");
        Assert.Equal("adef", t.GetScreenText().Split('\n')[0]);
        t.Write(Esc + "[1;2H" + Esc + "[2@");
        Assert.Equal("a  def", t.GetScreenText().Split('\n')[0]);
    }

    [Fact]
    public void AlternateScreen_IsSeparate_AndRestoresTheMainScreen()
    {
        var t = New(cols: 20, rows: 4);
        t.Write("main text");
        t.Write(Esc + "[?1049h");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal(string.Empty, t.GetScreenText());
        t.Write("vim");
        Assert.Equal("vim", t.GetScreenText());
        t.Write(Esc + "[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("main text", t.GetScreenText());
        Assert.Equal((0, 9), t.CursorPosition);
    }

    [Fact]
    public void AlternateScreen_HasNoScrollback()
    {
        var t = New(cols: 10, rows: 3);
        t.Write(Esc + "[?1049h");
        for (var i = 0; i < 20; i++) t.Write($"{i}\r\n");
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void Modes_AreTracked()
    {
        var t = New();
        Assert.True(t.CursorVisible);
        t.Write(Esc + "[?25l" + Esc + "[?1h" + Esc + "[?2004h");
        Assert.False(t.CursorVisible);
        Assert.True(t.AppCursorKeys);
        Assert.True(t.BracketedPaste);
        t.Write(Esc + "[?25h" + Esc + "[?1l" + Esc + "[?2004l");
        Assert.True(t.CursorVisible);
        Assert.False(t.AppCursorKeys);
        Assert.False(t.BracketedPaste);
    }

    [Fact]
    public void AutoWrapOff_OverwritesTheLastColumn()
    {
        var t = New(cols: 5, rows: 2);
        t.Write(Esc + "[?7labcdefg");
        Assert.Equal("abcdg", t.GetScreenText());
    }

    [Fact]
    public void Queries_AreAnswered()
    {
        var t = New();
        var replies = new System.Collections.Generic.List<string>();
        t.Reply += replies.Add;
        t.Write(Esc + "[3;7H" + Esc + "[6n" + Esc + "[5n" + Esc + "[c" + Esc + "[18t");
        Assert.Equal(new[] { Esc + "[3;7R", Esc + "[0n", Esc + "[?1;2c", Esc + "[8;10;40t" }, replies);
    }

    [Fact]
    public void WindowTitle_BothTerminators()
    {
        var t = New();
        var changes = 0;
        t.TitleChanged += () => changes++;
        t.Write(Esc + "]0;First" + "\u0007");
        Assert.Equal("First", t.Title);
        t.Write(Esc + "]2;Second" + Esc + "\\");
        Assert.Equal("Second", t.Title);
        Assert.Equal(2, changes);
        Assert.Equal(string.Empty, t.GetScreenText()); // nothing leaked on screen
    }

    [Fact]
    public void UnknownSequences_AreSwallowed()
    {
        var t = New();
        t.Write(Esc + "(Bhi" + Esc + "[?1000h" + Esc + "[5 q" + Esc + "Pq#0;2;0;0;0" + Esc + "\\!" + Esc + "=");
        Assert.Equal("hi!", t.GetScreenText());
    }

    [Fact]
    public void WideCharacters_TakeTwoCells()
    {
        var t = New(cols: 10, rows: 2);
        t.Write("a日b");
        Assert.Equal(2, Cell(t, 0, 1).Width);
        Assert.Equal(0, Cell(t, 0, 2).Width);
        Assert.Equal('b', Cell(t, 0, 3).DisplayChar);
        Assert.Equal("a日b", t.GetScreenText());
    }

    [Fact]
    public void SurrogatePair_SplitAcrossWrites()
    {
        var t = New(cols: 10, rows: 2);
        var emoji = char.ConvertFromUtf32(0x1F600);
        t.Write("x" + emoji[..1]);
        t.Write(emoji[1..] + "y");
        Assert.Equal("x" + emoji + "y", t.GetScreenText());
    }

    [Fact]
    public void CombiningMarks_TakeNoCell()
    {
        var t = New(cols: 10, rows: 2);
        t.Write("e\u0301x");
        Assert.Equal((0, 2), t.CursorPosition);
    }

    [Fact]
    public void SaveAndRestoreCursor()
    {
        var t = New();
        t.Write(Esc + "[3;4H" + Esc + "7" + Esc + "[1;1H" + Esc + "8");
        Assert.Equal((2, 3), t.CursorPosition);
        t.Write(Esc + "[5;5H" + Esc + "[s" + Esc + "[1;1H" + Esc + "[u");
        Assert.Equal((4, 4), t.CursorPosition);
    }

    [Fact]
    public void FullReset_ClearsEverything()
    {
        var t = New();
        t.Write(Esc + "[31mred\r\nmore" + Esc + "c");
        Assert.Equal(string.Empty, t.GetScreenText());
        Assert.Equal((0, 0), t.CursorPosition);
    }

    // ----- resize -----

    [Fact]
    public void Resize_Narrower_RewrapsTheText_AndKeepsTheCursorAfterIt()
    {
        var t = New(cols: 20, rows: 6);
        t.Write("0123456789ABCDEFGHIJ0123456789"); // 30 chars: one wrapped row + 10
        t.Resize(10, 6);
        Assert.Equal(10, t.Cols);
        Assert.Equal("0123456789ABCDEFGHIJ0123456789", t.GetLastLines(10));
        Assert.Equal((3, 0), t.CursorPosition); // after the last character, which filled the last row
    }

    [Fact]
    public void Resize_Wider_JoinsWrappedRowsAgain()
    {
        var t = New(cols: 10, rows: 6);
        t.Write("0123456789ABCDE");
        Assert.True(t.Buffer.ScreenLine(0).Wrapped);
        t.Resize(40, 6);
        Assert.False(t.Buffer.ScreenLine(0).Wrapped);
        Assert.Equal("0123456789ABCDE", t.GetScreenText());
        Assert.Equal((0, 15), t.CursorPosition);
    }

    [Fact]
    public void Resize_RoundTrip_KeepsTheOutput()
    {
        var t = New(cols: 30, rows: 8);
        t.Write("PS C:\\work> dotnet test\r\n");
        t.Write("  Determining projects to restore... all of them are up to date at this point\r\n");
        t.Write("PS C:\\work> ");
        var before = t.GetLastLines(10);
        var cursor = t.CursorPosition;
        t.Resize(12, 8);
        Assert.Equal(before, t.GetLastLines(10)); // re-wrapped, not changed
        t.Resize(30, 8);
        Assert.Equal(before, t.GetLastLines(10));
        Assert.Equal(cursor, t.CursorPosition);
    }

    [Fact]
    public void Resize_ShorterAndTaller_KeepsTheCursorOnItsLine()
    {
        var t = New(cols: 20, rows: 8);
        t.Write("one\r\ntwo\r\nthree");
        t.Resize(20, 3);
        Assert.Equal("one\ntwo\nthree", t.GetScreenText());
        Assert.Equal(2, t.CursorPosition.Row);
        t.Resize(20, 8);
        Assert.Equal("one\ntwo\nthree", t.GetScreenText());
        Assert.Equal(2, t.CursorPosition.Row);
    }

    [Fact]
    public void Resize_Shorter_PushesTheTopIntoScrollback()
    {
        var t = New(cols: 20, rows: 6);
        t.Write("1\r\n2\r\n3\r\n4\r\n5\r\n6");
        t.Resize(20, 3);
        Assert.Equal("4\n5\n6", t.GetScreenText());
        Assert.Equal(3, t.Buffer.ScrollbackCount);
        t.Resize(20, 6);
        Assert.Equal("1\n2\n3\n4\n5\n6", t.GetScreenText());
    }

    [Fact]
    public void Resize_WhileOnTheAlternateScreen_KeepsBothScreens()
    {
        var t = New(cols: 20, rows: 5);
        t.Write("shell prompt");
        t.Write(Esc + "[?1049h" + "full screen");
        t.Resize(30, 8);
        Assert.Equal("full screen", t.GetScreenText());
        t.Write(Esc + "[?1049l");
        Assert.Equal("shell prompt", t.GetScreenText());
    }

    [Fact]
    public void GetText_JoinsSoftWrapsButKeepsHardNewlines()
    {
        var t = New(cols: 5, rows: 6);
        t.Write("abcdefgh\r\nxyz");
        var top = t.Buffer.TrimmedCount + t.Buffer.Top;
        Assert.Equal("abcdefgh\nxyz", t.GetText(top, 0, top + 2, 5));
    }
}
