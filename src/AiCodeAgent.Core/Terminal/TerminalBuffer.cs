namespace AiCodeAgent.Core.Terminal;

/// <summary>
/// The grid of a terminal: <see cref="Rows"/> screen rows at the bottom of one list of lines, the scrollback above them.
/// The cursor lives here too, so the primary and the alternate screen each keep their own.
/// Not thread-safe: <see cref="TerminalEmulator"/> guards it.
/// </summary>
public sealed class TerminalBuffer
{
    private readonly List<TermLine> _lines = new();

    public TerminalBuffer(int cols, int rows, int maxScrollback)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        MaxScrollback = Math.Max(0, maxScrollback);
        for (var i = 0; i < Rows; i++) _lines.Add(new TermLine(Cols));
    }

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    /// <summary>How many lines may scroll off the top and be kept (0 on the alternate screen).</summary>
    public int MaxScrollback { get; }

    /// <summary>How many lines were dropped from the top so far; <c>TrimmedCount + index</c> is a line number that stays valid.</summary>
    public long TrimmedCount { get; private set; }

    public int CursorRow;
    public int CursorCol;

    /// <summary>The cursor sits after the last column; the next printable character wraps first.</summary>
    public bool PendingWrap;

    public int LineCount => _lines.Count;

    /// <summary>Index of the first screen row in the list of lines (= number of scrollback lines).</summary>
    public int Top => _lines.Count - Rows;

    public int ScrollbackCount => Top;

    public TermLine Line(int index) => _lines[index];

    public TermLine ScreenLine(int row) => _lines[Top + row];

    // ----- scrolling -----

    /// <summary>Scrolls the rows <paramref name="top"/>..<paramref name="bottom"/> up by <paramref name="n"/>; with <paramref name="toScrollback"/> the lines that leave the top are kept.</summary>
    public void ScrollUp(int top, int bottom, int n, TermCell fill, bool toScrollback)
    {
        n = Math.Min(n, bottom - top + 1);
        if (n <= 0) return;
        if (toScrollback && MaxScrollback > 0 && top == 0 && bottom == Rows - 1)
        {
            for (var k = 0; k < n; k++) _lines.Add(new TermLine(Cols, fill));
            TrimHead();
            return;
        }

        var t = Top;
        for (var k = 0; k < n; k++)
        {
            _lines.RemoveAt(t + top);
            _lines.Insert(t + bottom, new TermLine(Cols, fill));
        }
    }

    public void ScrollDown(int top, int bottom, int n, TermCell fill)
    {
        n = Math.Min(n, bottom - top + 1);
        if (n <= 0) return;
        var t = Top;
        for (var k = 0; k < n; k++)
        {
            _lines.RemoveAt(t + bottom);
            _lines.Insert(t + top, new TermLine(Cols, fill));
        }
    }

    private void TrimHead()
    {
        var excess = _lines.Count - Rows - MaxScrollback;
        if (excess <= 0) return;
        // Trim in batches: removing from the front of a list is a memmove.
        if (MaxScrollback > 0 && excess < 256) return;
        _lines.RemoveRange(0, excess);
        TrimmedCount += excess;
    }

    /// <summary>Drops the scrollback (ED 3).</summary>
    public void ClearScrollback()
    {
        var n = Top;
        if (n <= 0) return;
        _lines.RemoveRange(0, n);
        TrimmedCount += n;
    }

    // ----- resizing -----

    /// <summary>Changes the size. Columns: the text of the primary screen is re-wrapped (<paramref name="reflow"/>), the alternate screen is cut or padded.</summary>
    public void Resize(int newCols, int newRows, bool reflow)
    {
        newCols = Math.Max(1, newCols);
        newRows = Math.Max(1, newRows);
        if (newCols == Cols && newRows == Rows) return;

        if (newCols != Cols)
        {
            if (reflow) ReflowColumns(newCols);
            else ResizeColumnsHard(newCols);
        }

        if (newRows != Rows) ResizeRows(newRows);

        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
        PendingWrap = false;
    }

    private void ResizeColumnsHard(int newCols)
    {
        foreach (var line in _lines)
        {
            var old = line.Cells.Length;
            Array.Resize(ref line.Cells, newCols);
            for (var i = old; i < newCols; i++) line.Cells[i].Width = 1;
            line.Wrapped = false;
        }

        Cols = newCols;
    }

    private void ResizeRows(int newRows)
    {
        if (newRows > Rows)
        {
            var add = newRows - Rows;
            var pull = MaxScrollback > 0 ? Math.Min(add, ScrollbackCount) : 0;
            CursorRow += pull; // the scrollback lines that come back sit above the cursor
            for (var i = 0; i < add - pull; i++) _lines.Add(new TermLine(Cols));
            Rows = newRows;
            return;
        }

        var remove = Rows - newRows;
        var removedBottom = 0;
        // First give up the empty rows below the cursor, then push the top rows into the scrollback.
        while (removedBottom < remove && Rows - 1 - removedBottom > CursorRow && _lines[_lines.Count - 1 - removedBottom].IsBlank)
            removedBottom++;
        if (removedBottom > 0) _lines.RemoveRange(_lines.Count - removedBottom, removedBottom);
        var rest = remove - removedBottom;
        CursorRow = Math.Max(0, CursorRow - rest);
        Rows = newRows;
        TrimHead(); // on the alternate screen the rows pushed above the top are simply dropped
    }

    /// <summary>Re-wraps all lines to a new width, keeping the cursor on the same character.</summary>
    private void ReflowColumns(int newCols)
    {
        var oldCols = Cols;
        var cursorAbs = Top + CursorRow;

        // 1. logical lines: rows joined while they are soft-wrapped
        var logical = new List<List<TermCell>>();
        var current = new List<TermCell>();
        var cursorLogical = -1;
        var cursorOffset = 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var len = line.Wrapped ? oldCols : line.UsedLength();
            if (i == cursorAbs)
            {
                cursorLogical = logical.Count;
                cursorOffset = current.Count + CursorCol;
                if (!line.Wrapped) len = Math.Max(len, CursorCol);
            }

            for (var c = 0; c < len; c++) current.Add(line.Cells[c]);
            if (!line.Wrapped)
            {
                logical.Add(current);
                current = new List<TermCell>();
            }
        }

        if (current.Count > 0) logical.Add(current);

        // 2. wrap them again
        var result = new List<TermLine>();
        var newCursorLine = 0;
        var newCursorCol = 0;
        for (var li = 0; li < logical.Count; li++)
        {
            var cells = logical[li];
            var line = new TermLine(newCols);
            var col = 0;
            for (var k = 0; k < cells.Count; k++)
            {
                var cell = cells[k];
                if (cell.Width == 0) continue; // the tail of a wide character: recreated with its head
                var w = Math.Max(1, (int)cell.Width);
                if (w > newCols) w = 1;
                if (col + w > newCols)
                {
                    line.Wrapped = true;
                    result.Add(line);
                    line = new TermLine(newCols);
                    col = 0;
                }

                if (li == cursorLogical && k == cursorOffset)
                {
                    newCursorLine = result.Count;
                    newCursorCol = col;
                }

                line.Cells[col] = cell;
                if (w == 2)
                {
                    var tail = cell;
                    tail.Rune = 0;
                    tail.Width = 0;
                    line.Cells[col + 1] = tail;
                }
                else
                {
                    line.Cells[col].Width = 1;
                }

                col += w;
            }

            if (li == cursorLogical && cursorOffset >= cells.Count)
            {
                if (col >= newCols)
                {
                    line.Wrapped = true;
                    result.Add(line);
                    line = new TermLine(newCols);
                    col = 0;
                }

                newCursorLine = result.Count;
                newCursorCol = col;
            }

            result.Add(line);
        }

        if (cursorLogical < 0)
        {
            // The cursor was below the last used line.
            newCursorLine = result.Count;
            newCursorCol = Math.Min(CursorCol, newCols - 1);
            result.Add(new TermLine(newCols));
        }

        // 3. the empty rows below the cursor are not content
        while (result.Count - 1 > newCursorLine && result[^1].IsBlank && !result[^1].Wrapped)
            result.RemoveAt(result.Count - 1);
        while (result.Count < Rows) result.Add(new TermLine(newCols));

        // 4. the screen is the last Rows lines, moved up if that would hide the cursor
        var top = Math.Max(0, result.Count - Rows);
        if (newCursorLine < top) top = newCursorLine;
        var keep = top + Rows;
        if (result.Count > keep) result.RemoveRange(keep, result.Count - keep);
        while (result.Count < keep) result.Add(new TermLine(newCols));

        _lines.Clear();
        _lines.AddRange(result);
        Cols = newCols;
        CursorRow = newCursorLine - top;
        CursorCol = Math.Min(newCursorCol, newCols - 1);
        PendingWrap = false;
        TrimHead();
    }

    // ----- text -----

    /// <summary>The text between two positions (line numbers as in <see cref="TrimmedCount"/> + index, end exclusive); soft-wrapped rows are joined, trailing blanks dropped.</summary>
    public string GetText(long startLine, int startCol, long endLine, int endCol)
    {
        var sb = new System.Text.StringBuilder();
        var first = (int)Math.Max(0, startLine - TrimmedCount);
        var last = (int)Math.Min(_lines.Count - 1, endLine - TrimmedCount);
        for (var i = first; i <= last; i++)
        {
            var line = _lines[i];
            var from = i == first ? Math.Clamp(startCol, 0, Cols) : 0;
            var to = i == last ? Math.Clamp(endCol, 0, Cols) : Cols;
            var text = RowText(line, from, to);
            if (!line.Wrapped || i == last) text = text.TrimEnd(' ');
            sb.Append(text);
            if (i != last && !line.Wrapped) sb.Append('\n');
        }

        return sb.ToString();
    }

    public static string RowText(TermLine line, int from, int to)
    {
        var sb = new System.Text.StringBuilder();
        for (var c = from; c < to && c < line.Cells.Length; c++)
        {
            var cell = line.Cells[c];
            if (cell.Width == 0) continue;
            if (cell.Rune == 0) sb.Append(' ');
            else sb.Append(char.ConvertFromUtf32(cell.Rune <= 0x10FFFF && !(cell.Rune is >= 0xD800 and <= 0xDFFF) ? cell.Rune : 0xFFFD));
        }

        return sb.ToString();
    }

    /// <summary>The text of the last <paramref name="maxLines"/> lines that are not empty (screen and scrollback).</summary>
    public string GetLastLines(int maxLines)
    {
        var lines = new List<string>();
        var end = _lines.Count - 1;
        while (end >= 0 && _lines[end].IsBlank) end--;
        var start = end;
        var count = 0;
        // Walk back over rows, counting a soft-wrapped group as one line.
        while (start >= 0 && count < maxLines)
        {
            var groupStart = start;
            while (groupStart > 0 && _lines[groupStart - 1].Wrapped) groupStart--;
            lines.Add(GetText(TrimmedCount + groupStart, 0, TrimmedCount + start, Cols));
            start = groupStart - 1;
            count++;
        }

        lines.Reverse();
        return string.Join("\n", lines);
    }
}
