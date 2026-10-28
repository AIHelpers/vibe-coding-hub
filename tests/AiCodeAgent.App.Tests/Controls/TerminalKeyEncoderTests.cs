using AiCodeAgent.App.Controls;
using Avalonia.Input;

namespace AiCodeAgent.App.Tests.Controls;

public class TerminalKeyEncoderTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData(Key.Enter, "\r")]
    [InlineData(Key.Tab, "\t")]
    [InlineData(Key.Back, "\u007f")]
    [InlineData(Key.Escape, Esc)]
    [InlineData(Key.Up, Esc + "[A")]
    [InlineData(Key.Down, Esc + "[B")]
    [InlineData(Key.Right, Esc + "[C")]
    [InlineData(Key.Left, Esc + "[D")]
    [InlineData(Key.Home, Esc + "[H")]
    [InlineData(Key.End, Esc + "[F")]
    [InlineData(Key.Delete, Esc + "[3~")]
    [InlineData(Key.Insert, Esc + "[2~")]
    [InlineData(Key.PageUp, Esc + "[5~")]
    [InlineData(Key.PageDown, Esc + "[6~")]
    [InlineData(Key.F1, Esc + "OP")]
    [InlineData(Key.F4, Esc + "OS")]
    [InlineData(Key.F5, Esc + "[15~")]
    [InlineData(Key.F12, Esc + "[24~")]
    public void PlainKeys_SendTheirSequence(Key key, string expected)
    {
        Assert.Equal(expected, TerminalKeyEncoder.Encode(key, KeyModifiers.None, false));
    }

    [Fact]
    public void ApplicationCursorKeys_UseSs3()
    {
        Assert.Equal(Esc + "OA", TerminalKeyEncoder.Encode(Key.Up, KeyModifiers.None, true));
        Assert.Equal(Esc + "OH", TerminalKeyEncoder.Encode(Key.Home, KeyModifiers.None, true));
        // with a modifier the CSI form is always used
        Assert.Equal(Esc + "[1;5A", TerminalKeyEncoder.Encode(Key.Up, KeyModifiers.Control, true));
    }

    [Theory]
    [InlineData(Key.Left, KeyModifiers.Control, Esc + "[1;5D")]
    [InlineData(Key.Right, KeyModifiers.Shift, Esc + "[1;2C")]
    [InlineData(Key.Right, KeyModifiers.Alt, Esc + "[1;3C")]
    [InlineData(Key.Delete, KeyModifiers.Control, Esc + "[3;5~")]
    [InlineData(Key.F5, KeyModifiers.Shift, Esc + "[15;2~")]
    [InlineData(Key.F1, KeyModifiers.Control, Esc + "[1;5P")]
    public void Modifiers_UseTheXtermParameter(Key key, KeyModifiers modifiers, string expected)
    {
        Assert.Equal(expected, TerminalKeyEncoder.Encode(key, modifiers, false));
    }

    [Theory]
    [InlineData(Key.C, "\u0003")]
    [InlineData(Key.D, "\u0004")]
    [InlineData(Key.A, "\u0001")]
    [InlineData(Key.Z, "\u001a")]
    [InlineData(Key.L, "\u000c")]
    public void CtrlLetter_IsAControlCharacter(Key key, string expected)
    {
        Assert.Equal(expected, TerminalKeyEncoder.Encode(key, KeyModifiers.Control, false));
    }

    [Fact]
    public void CtrlShiftLetter_IsLeftToTheApp()
    {
        Assert.Null(TerminalKeyEncoder.Encode(Key.P, KeyModifiers.Control | KeyModifiers.Shift, false));
        Assert.Null(TerminalKeyEncoder.Encode(Key.C, KeyModifiers.Control | KeyModifiers.Shift, false));
    }

    [Fact]
    public void AltGr_IsNotAControlCombination()
    {
        // AltGr arrives as Ctrl+Alt on Windows and types characters such as @ or {.
        Assert.Null(TerminalKeyEncoder.Encode(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, false));
    }

    [Fact]
    public void AltLetter_SendsEscapeThenTheLetter()
    {
        Assert.Equal(Esc + "b", TerminalKeyEncoder.Encode(Key.B, KeyModifiers.Alt, false));
        Assert.Equal(Esc + "F", TerminalKeyEncoder.Encode(Key.F, KeyModifiers.Alt | KeyModifiers.Shift, false));
        Assert.Equal(Esc + "1", TerminalKeyEncoder.Encode(Key.D1, KeyModifiers.Alt, false));
    }

    [Fact]
    public void ShiftTab_IsBackTab()
    {
        Assert.Equal(Esc + "[Z", TerminalKeyEncoder.Encode(Key.Tab, KeyModifiers.Shift, false));
    }

    [Fact]
    public void CtrlBackspace_DeletesAWord_AndCtrlSpace_IsNul()
    {
        Assert.Equal("\b", TerminalKeyEncoder.Encode(Key.Back, KeyModifiers.Control, false));
        Assert.Equal("\0", TerminalKeyEncoder.Encode(Key.Space, KeyModifiers.Control, false));
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.D5)]
    [InlineData(Key.Space)]
    [InlineData(Key.OemPeriod)]
    public void PrintableKeys_ComeThroughTextInput(Key key)
    {
        Assert.Null(TerminalKeyEncoder.Encode(key, KeyModifiers.None, false));
        Assert.Null(TerminalKeyEncoder.Encode(key, KeyModifiers.Shift, false));
    }

    [Fact]
    public void MetaCombinations_AreNotSent()
    {
        Assert.Null(TerminalKeyEncoder.Encode(Key.C, KeyModifiers.Meta, false));
    }

    [Fact]
    public void Paste_TurnsLineBreaksIntoEnter()
    {
        Assert.Equal("a\rb\rc", TerminalKeyEncoder.EncodePaste("a\r\nb\nc", false));
    }

    [Fact]
    public void Paste_IsBracketed_WhenTheProgramAsked_AndCannotCloseTheBracketEarly()
    {
        Assert.Equal(Esc + "[200~echo hi" + Esc + "[201~", TerminalKeyEncoder.EncodePaste("echo hi", true));
        var hostile = TerminalKeyEncoder.EncodePaste("x" + Esc + "[201~rm -rf /", true);
        Assert.Equal(1, hostile.Split(Esc + "[201~").Length - 1);
        Assert.EndsWith(Esc + "[201~", hostile);
    }
}
