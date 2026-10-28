namespace AiCodeAgent.Core.Terminal;

/// <summary>Text attributes of a terminal cell (SGR).</summary>
[Flags]
public enum CellAttr : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strike = 32,
    Hidden = 64,
    Blink = 128
}

/// <summary>
/// A colour as a program set it: the terminal default, an index into the 256-colour palette (so the theme decides the
/// actual RGB of the first 16) or a true-colour RGB value.
/// </summary>
public readonly record struct TermColor(uint Value)
{
    public static TermColor Default => default;

    public static TermColor Indexed(int index) => new(0x01000000u | (uint)(index & 0xFF));

    public static TermColor Rgb(byte r, byte g, byte b) => new(0x02000000u | ((uint)r << 16) | ((uint)g << 8) | b);

    public bool IsDefault => Value == 0;

    public bool IsIndexed => (Value >> 24) == 1;

    public bool IsRgb => (Value >> 24) == 2;

    public int Index => (int)(Value & 0xFF);

    public byte R => (byte)(Value >> 16);

    public byte G => (byte)(Value >> 8);

    public byte B => (byte)Value;
}

/// <summary>One character cell. <see cref="Rune"/> 0 is an empty cell; <see cref="Width"/> is 2 for the head of a wide character and 0 for its tail.</summary>
public struct TermCell
{
    public int Rune;
    public TermColor Fg;
    public TermColor Bg;
    public CellAttr Attr;
    public byte Width;

    public readonly bool IsBlank => (Rune == 0 || Rune == ' ') && Bg.IsDefault && (Attr & (CellAttr.Inverse | CellAttr.Underline | CellAttr.Strike)) == 0 && Width <= 1;

    public readonly char DisplayChar => Rune == 0 ? ' ' : (Rune < 0x10000 ? (char)Rune : '�');
}

/// <summary>One row of the terminal. <see cref="Wrapped"/> is true when the text continues on the next row (a soft wrap, not a newline).</summary>
public sealed class TermLine
{
    public TermCell[] Cells;
    public bool Wrapped;

    public TermLine(int cols, TermCell fill = default)
    {
        Cells = new TermCell[cols];
        if (fill.Bg.IsDefault && fill.Attr == CellAttr.None && fill.Rune == 0)
        {
            for (var i = 0; i < cols; i++) Cells[i].Width = 1;
        }
        else
        {
            fill.Width = 1;
            Array.Fill(Cells, fill);
        }
    }

    public bool IsBlank
    {
        get
        {
            foreach (var c in Cells)
                if (!c.IsBlank) return false;
            return true;
        }
    }

    /// <summary>Number of cells up to and including the last one that is not blank.</summary>
    public int UsedLength()
    {
        for (var i = Cells.Length - 1; i >= 0; i--)
            if (!Cells[i].IsBlank) return i + 1;
        return 0;
    }

    public void Clear(int from, int to, TermCell fill)
    {
        fill.Rune = 0;
        fill.Width = 1;
        fill.Attr = CellAttr.None;
        for (var i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++) Cells[i] = fill;
    }
}

/// <summary>Display width of a code point in terminal cells (0 = combining / zero width, 2 = wide).</summary>
public static class CharWidth
{
    public static int Of(int cp)
    {
        if (cp < 0x300) return cp == 0 ? 0 : 1;
        if (cp is >= 0x300 and <= 0x36F or >= 0x200B and <= 0x200F or >= 0xFE00 and <= 0xFE0F or 0x2060 or 0xFEFF or >= 0x20D0 and <= 0x20FF)
            return 0;
        if (cp is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x20000 and <= 0x3FFFD)
            return 2;
        return 1;
    }
}
