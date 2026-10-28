using System.Text;

namespace AiCodeAgent.Core.Terminal;

/// <summary>
/// A VT100/xterm terminal emulator: feed it the text a program wrote to its terminal and it keeps the screen, the
/// scrollback and the cursor. Supports what shells, <c>dotnet</c>, <c>git</c>, <c>npm</c>, <c>vim</c>, <c>less</c> and
/// <c>top</c> need: SGR colours (16, 256, true colour), cursor movement, erase/insert/delete, scroll regions, the
/// alternate screen, bracketed paste, window title, and the replies programs ask for (cursor position, device attributes).
/// All public members are safe to call from any thread; readers that walk <see cref="Buffer"/> must hold <see cref="SyncRoot"/>.
/// </summary>
public sealed class TerminalEmulator
{
    public const int DefaultScrollback = 10_000;

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, StringIgnore }

    private TerminalBuffer _primary;
    private TerminalBuffer? _alt;
    private TerminalBuffer _b;

    private int _cols;
    private int _rows;
    private int _scrollTop;
    private int _scrollBottom;

    // the pen
    private TermColor _fg;
    private TermColor _bg;
    private CellAttr _attr;

    // modes
    private bool _insertMode;
    private bool _originMode;

    private struct SavedCursor
    {
        public int Row, Col;
        public TermColor Fg, Bg;
        public CellAttr Attr;
        public bool Origin, Pending;
        public bool Valid;
    }

    private SavedCursor _saved;

    // parser
    private State _state;
    private readonly List<List<int>> _params = new();
    private List<int> _currentParam = new();
    private int _currentValue = -1;
    private char _csiPrefix;
    private char _csiIntermediate;
    private readonly StringBuilder _osc = new();
    private char _highSurrogate;

    public TerminalEmulator(int cols, int rows, int scrollback = DefaultScrollback)
    {
        _cols = Math.Max(2, cols);
        _rows = Math.Max(1, rows);
        _primary = new TerminalBuffer(_cols, _rows, scrollback);
        _b = _primary;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        AutoWrap = true;
        CursorVisible = true;
    }

    /// <summary>Hold this while walking <see cref="Buffer"/>.</summary>
    public object SyncRoot { get; } = new();

    public int Cols => _cols;

    public int Rows => _rows;

    /// <summary>The buffer being shown (the alternate screen while a full-screen program runs).</summary>
    public TerminalBuffer Buffer => _b;

    public bool IsAlternateScreen => _alt != null && ReferenceEquals(_b, _alt);

    public bool AutoWrap { get; private set; }

    public bool CursorVisible { get; private set; }

    /// <summary>The program asked for application cursor keys (DECCKM): arrows are sent as ESC O A instead of ESC [ A.</summary>
    public bool AppCursorKeys { get; private set; }

    public bool BracketedPaste { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>The working directory the shell reported with OSC 7, if it did.</summary>
    public string? ReportedDirectory { get; private set; }

    /// <summary>Changes on every write and resize; the view repaints when it differs from what it drew.</summary>
    public int Version { get; private set; }

    /// <summary>Text the terminal sends back to the program (answers to cursor-position and device-attribute queries).</summary>
    public event Action<string>? Reply;

    public event Action? TitleChanged;

    public event Action? Bell;

    // ===================================================================== public API

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (SyncRoot)
        {
            foreach (var ch in text) Feed(ch);
            Version++;
        }
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        lock (SyncRoot)
        {
            if (cols == _cols && rows == _rows) return;
            _primary.Resize(cols, rows, reflow: true);
            _alt?.Resize(cols, rows, reflow: false);
            _cols = cols;
            _rows = rows;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            Version++;
        }
    }

    /// <summary>The last non-empty lines of the screen and scrollback (for "ask the agent about this error").</summary>
    public string GetLastLines(int maxLines)
    {
        lock (SyncRoot) return _b.GetLastLines(maxLines);
    }

    public string GetText(long startLine, int startCol, long endLine, int endCol)
    {
        lock (SyncRoot) return _b.GetText(startLine, startCol, endLine, endCol);
    }

    /// <summary>Everything on screen as text (rows joined with newlines, trailing blanks dropped); handy for tests.</summary>
    public string GetScreenText()
    {
        lock (SyncRoot)
        {
            var sb = new StringBuilder();
            for (var r = 0; r < _rows; r++)
            {
                if (r > 0) sb.Append('\n');
                var line = _b.ScreenLine(r);
                sb.Append(TerminalBuffer.RowText(line, 0, _cols).TrimEnd(' '));
            }

            return sb.ToString().TrimEnd('\n');
        }
    }

    public (int Row, int Col) CursorPosition
    {
        get { lock (SyncRoot) return (_b.CursorRow, _b.CursorCol); }
    }

    /// <summary>Full reset (ESC c).</summary>
    public void Reset()
    {
        lock (SyncRoot)
        {
            _alt = null;
            _primary = new TerminalBuffer(_cols, _rows, _primary.MaxScrollback);
            _b = _primary;
            _scrollTop = 0;
            _scrollBottom = _rows - 1;
            ResetPen();
            AutoWrap = true;
            CursorVisible = true;
            AppCursorKeys = false;
            BracketedPaste = false;
            _insertMode = false;
            _originMode = false;
            _saved = default;
            _state = State.Ground;
            Version++;
        }
    }

    // ===================================================================== the parser

    private void Feed(char ch)
    {
        int cp = ch;
        if (char.IsHighSurrogate(ch))
        {
            _highSurrogate = ch;
            return;
        }

        if (char.IsLowSurrogate(ch))
        {
            if (_highSurrogate == 0) return;
            cp = char.ConvertToUtf32(_highSurrogate, ch);
            _highSurrogate = '\0';
        }
        else
        {
            _highSurrogate = '\0';
        }

        switch (_state)
        {
            case State.Osc:
                FeedOsc(cp);
                return;
            case State.StringIgnore:
                if (cp == 0x07) _state = State.Ground;
                else if (cp == 0x1B) _state = State.Escape; // ESC \ ends the string
                return;
        }

        // C0 controls act in every state except inside strings.
        if (cp < 0x20 || cp == 0x7F)
        {
            if (cp == 0x1B)
            {
                _state = State.Escape;
                return;
            }

            if (cp is 0x18 or 0x1A)
            {
                _state = State.Ground;
                return;
            }

            Execute(cp);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                Print(cp);
                break;
            case State.Escape:
                EscapeDispatch(cp);
                break;
            case State.EscapeIntermediate:
                if (_csiIntermediate == '#' && cp == '8') FillScreenWithE();
                _state = State.Ground; // charset designations (ESC ( B ...) and the like: the final byte is ignored
                break;
            case State.Csi:
                CsiByte(cp);
                break;
        }
    }

    private void Execute(int cp)
    {
        switch (cp)
        {
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08: // BS
                if (_b.CursorCol > 0) _b.CursorCol--;
                _b.PendingWrap = false;
                break;
            case 0x09: // HT
                TabForward(1);
                break;
            case 0x0A:
            case 0x0B:
            case 0x0C: // LF, VT, FF
                Index();
                break;
            case 0x0D: // CR
                _b.CursorCol = 0;
                _b.PendingWrap = false;
                break;
        }
    }

    private void EscapeDispatch(int cp)
    {
        _state = State.Ground;
        switch (cp)
        {
            case '[':
                BeginCsi();
                break;
            case ']':
                _osc.Clear();
                _state = State.Osc;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = State.StringIgnore;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case '%':
                _csiIntermediate = (char)cp;
                _state = State.EscapeIntermediate;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                Index();
                break;
            case 'E':
                _b.CursorCol = 0;
                Index();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
            case '\\': // ST: nothing to terminate any more
            case '=':
            case '>':
            case 'H':
                break;
        }
    }

    private void BeginCsi()
    {
        _params.Clear();
        _currentParam = new List<int>();
        _currentValue = -1;
        _csiPrefix = '\0';
        _csiIntermediate = '\0';
        _state = State.Csi;
    }

    private void CsiByte(int cp)
    {
        if (cp >= '0' && cp <= '9')
        {
            _currentValue = (_currentValue < 0 ? 0 : _currentValue) * 10 + (cp - '0');
            if (_currentValue > 100_000) _currentValue = 100_000;
        }
        else if (cp == ';')
        {
            _currentParam.Add(_currentValue);
            _params.Add(_currentParam);
            _currentParam = new List<int>();
            _currentValue = -1;
        }
        else if (cp == ':')
        {
            _currentParam.Add(_currentValue);
            _currentValue = -1;
        }
        else if (cp is '?' or '>' or '=' or '<' && _params.Count == 0 && _currentParam.Count == 0 && _currentValue < 0)
        {
            _csiPrefix = (char)cp;
        }
        else if (cp is >= 0x20 and <= 0x2F)
        {
            _csiIntermediate = (char)cp;
        }
        else if (cp >= 0x40 && cp <= 0x7E)
        {
            _currentParam.Add(_currentValue);
            _params.Add(_currentParam);
            _state = State.Ground;
            CsiDispatch((char)cp);
        }
        else
        {
            _state = State.Ground; // malformed: drop it
        }
    }

    private int P(int index, int def)
    {
        if (index >= _params.Count) return def;
        var v = _params[index][0];
        return v <= 0 ? def : v;
    }

    private int Raw(int index) => index < _params.Count ? _params[index][0] : -1;

    private void FeedOsc(int cp)
    {
        if (cp == 0x07)
        {
            FinishOsc();
            return;
        }

        if (cp == 0x1B)
        {
            // ESC \ : the backslash will arrive in the escape state and be ignored.
            FinishOsc();
            _state = State.Escape;
            return;
        }

        if (_osc.Length < 4096) _osc.Append(char.ConvertFromUtf32(cp));
    }

    private void FinishOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        var semi = text.IndexOf(';');
        var code = semi < 0 ? text : text[..semi];
        var arg = semi < 0 ? string.Empty : text[(semi + 1)..];
        switch (code)
        {
            case "0":
            case "2":
                Title = arg;
                TitleChanged?.Invoke();
                break;
            case "7":
                ReportedDirectory = ParseFileUri(arg);
                break;
        }
    }

    private static string? ParseFileUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        return uri.LocalPath;
    }

    // ===================================================================== CSI

    private void CsiDispatch(char final)
    {
        var b = _b;
        if (_csiPrefix == '?')
        {
            if (final is 'h' or 'l') SetPrivateModes(final == 'h');
            else if (final == 'n' && Raw(0) == 6) SendReply($"\u001b[?{ReportRow()};{b.CursorCol + 1}R");
            return;
        }

        if (_csiPrefix == '>')
        {
            if (final == 'c') SendReply("\u001b[>0;0;0c");
            return;
        }

        if (_csiPrefix != '\0') return;

        if (_csiIntermediate == '!' && final == 'p')
        {
            SoftReset();
            return;
        }

        if (_csiIntermediate != '\0') return; // DECSCUSR (cursor shape) and friends

        switch (final)
        {
            case 'A': MoveCursor(-P(0, 1), 0); break;
            case 'B':
            case 'e': MoveCursor(P(0, 1), 0); break;
            case 'C':
            case 'a': MoveCursor(0, P(0, 1)); break;
            case 'D': MoveCursor(0, -P(0, 1)); break;
            case 'E': MoveCursor(P(0, 1), 0); b.CursorCol = 0; break;
            case 'F': MoveCursor(-P(0, 1), 0); b.CursorCol = 0; break;
            case 'G':
            case '`':
                b.CursorCol = Math.Clamp(P(0, 1) - 1, 0, _cols - 1);
                b.PendingWrap = false;
                break;
            case 'H':
            case 'f':
                SetCursor(P(0, 1) - 1, P(1, 1) - 1);
                break;
            case 'd':
                SetCursor(P(0, 1) - 1, b.CursorCol);
                break;
            case 'I': TabForward(P(0, 1)); break;
            case 'Z': TabBackward(P(0, 1)); break;
            case 'J': EraseInDisplay(Raw(0) < 0 ? 0 : Raw(0)); break;
            case 'K': EraseInLine(Raw(0) < 0 ? 0 : Raw(0)); break;
            case 'L': InsertLines(P(0, 1)); break;
            case 'M': DeleteLines(P(0, 1)); break;
            case '@': InsertChars(P(0, 1)); break;
            case 'P': DeleteChars(P(0, 1)); break;
            case 'X': EraseChars(P(0, 1)); break;
            case 'S': ScrollRegionUp(P(0, 1)); break;
            case 'T':
                if (_params.Count <= 1) ScrollRegionDown(P(0, 1));
                break;
            case 'm': Sgr(); break;
            case 'r': SetScrollRegion(); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h':
            case 'l':
                for (var i = 0; i < _params.Count; i++)
                    if (Raw(i) == 4) _insertMode = final == 'h';
                break;
            case 'n':
                if (Raw(0) == 5) SendReply("\u001b[0n");
                else if (Raw(0) == 6) SendReply($"\u001b[{ReportRow()};{b.CursorCol + 1}R");
                break;
            case 'c':
                if (Raw(0) <= 0) SendReply("\u001b[?1;2c");
                break;
            case 't':
                if (Raw(0) == 18) SendReply($"\u001b[8;{_rows};{_cols}t");
                break;
        }
    }

    private int ReportRow() => _b.CursorRow + 1 - (_originMode ? _scrollTop : 0);

    private void SendReply(string text) => Reply?.Invoke(text);

    private void SetPrivateModes(bool on)
    {
        for (var i = 0; i < _params.Count; i++)
        {
            switch (Raw(i))
            {
                case 1: AppCursorKeys = on; break;
                case 6:
                    _originMode = on;
                    SetCursor(0, 0);
                    break;
                case 7: AutoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 47:
                case 1047:
                    if (on) EnterAlternateScreen(false);
                    else LeaveAlternateScreen(false);
                    break;
                case 1049:
                    if (on) EnterAlternateScreen(true);
                    else LeaveAlternateScreen(true);
                    break;
                case 2004: BracketedPaste = on; break;
            }
        }
    }

    private void SoftReset()
    {
        ResetPen();
        AutoWrap = true;
        CursorVisible = true;
        AppCursorKeys = false;
        _insertMode = false;
        _originMode = false;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private void EnterAlternateScreen(bool saveCursor)
    {
        if (IsAlternateScreen) return;
        if (saveCursor) SaveCursor();
        _alt = new TerminalBuffer(_cols, _rows, 0);
        _b = _alt;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private void LeaveAlternateScreen(bool restoreCursor)
    {
        if (!IsAlternateScreen) return;
        _alt = null;
        _b = _primary;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        if (restoreCursor) RestoreCursor();
    }

    private void SaveCursor()
    {
        _saved = new SavedCursor
        {
            Row = _b.CursorRow, Col = _b.CursorCol, Fg = _fg, Bg = _bg, Attr = _attr,
            Origin = _originMode, Pending = _b.PendingWrap, Valid = true
        };
    }

    private void RestoreCursor()
    {
        if (!_saved.Valid)
        {
            SetCursor(0, 0);
            return;
        }

        _b.CursorRow = Math.Clamp(_saved.Row, 0, _rows - 1);
        _b.CursorCol = Math.Clamp(_saved.Col, 0, _cols - 1);
        _fg = _saved.Fg;
        _bg = _saved.Bg;
        _attr = _saved.Attr;
        _originMode = _saved.Origin;
        _b.PendingWrap = _saved.Pending;
    }

    private void ResetPen()
    {
        _fg = default;
        _bg = default;
        _attr = CellAttr.None;
    }

    // ===================================================================== SGR

    private void Sgr()
    {
        if (_params.Count == 1 && _params[0][0] < 0)
        {
            ResetPen();
            return;
        }

        for (var i = 0; i < _params.Count; i++)
        {
            var group = _params[i];
            var code = group[0] < 0 ? 0 : group[0];
            switch (code)
            {
                case 0: ResetPen(); break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Dim; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4:
                    if (group.Count > 1 && group[1] == 0) _attr &= ~CellAttr.Underline;
                    else _attr |= CellAttr.Underline;
                    break;
                case 5:
                case 6: _attr |= CellAttr.Blink; break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 8: _attr |= CellAttr.Hidden; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 21: _attr |= CellAttr.Underline; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 25: _attr &= ~CellAttr.Blink; break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case 28: _attr &= ~CellAttr.Hidden; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case 39: _fg = default; break;
                case 49: _bg = default; break;
                case 38:
                case 48:
                    {
                        var color = ParseExtendedColor(group, ref i);
                        if (color is { } c)
                        {
                            if (code == 38) _fg = c;
                            else _bg = c;
                        }

                        break;
                    }
                default:
                    if (code is >= 30 and <= 37) _fg = TermColor.Indexed(code - 30);
                    else if (code is >= 40 and <= 47) _bg = TermColor.Indexed(code - 40);
                    else if (code is >= 90 and <= 97) _fg = TermColor.Indexed(code - 90 + 8);
                    else if (code is >= 100 and <= 107) _bg = TermColor.Indexed(code - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>38;5;n / 38;2;r;g;b (semicolons) and 38:5:n / 38:2::r:g:b (colons).</summary>
    private TermColor? ParseExtendedColor(List<int> group, ref int i)
    {
        if (group.Count > 1)
        {
            // colon form: everything is inside this group
            var kind = group[1];
            if (kind == 5 && group.Count >= 3) return TermColor.Indexed(Math.Clamp(group[^1], 0, 255));
            if (kind == 2 && group.Count >= 5)
                return TermColor.Rgb((byte)Math.Clamp(group[^3], 0, 255), (byte)Math.Clamp(group[^2], 0, 255), (byte)Math.Clamp(group[^1], 0, 255));
            return null;
        }

        var mode = i + 1 < _params.Count ? _params[i + 1][0] : -1;
        if (mode == 5 && i + 2 < _params.Count)
        {
            var idx = Math.Max(0, _params[i + 2][0]);
            i += 2;
            return TermColor.Indexed(Math.Min(idx, 255));
        }

        if (mode == 2 && i + 4 < _params.Count)
        {
            var r = Math.Clamp(_params[i + 2][0], 0, 255);
            var g = Math.Clamp(_params[i + 3][0], 0, 255);
            var bl = Math.Clamp(_params[i + 4][0], 0, 255);
            i += 4;
            return TermColor.Rgb((byte)r, (byte)g, (byte)bl);
        }

        i = _params.Count; // malformed: swallow the rest
        return null;
    }

    // ===================================================================== cursor

    private void SetCursor(int row, int col)
    {
        if (_originMode) row = Math.Clamp(row + _scrollTop, _scrollTop, _scrollBottom);
        _b.CursorRow = Math.Clamp(row, 0, _rows - 1);
        _b.CursorCol = Math.Clamp(col, 0, _cols - 1);
        _b.PendingWrap = false;
    }

    private void MoveCursor(int dRow, int dCol)
    {
        var b = _b;
        if (dRow != 0)
        {
            var min = 0;
            var max = _rows - 1;
            if (_originMode || (b.CursorRow >= _scrollTop && b.CursorRow <= _scrollBottom))
            {
                min = _scrollTop;
                max = _scrollBottom;
            }

            // moving up from below the region or down from above it stops at the screen edge, not the region edge
            if (dRow < 0 && b.CursorRow < _scrollTop) min = 0;
            if (dRow > 0 && b.CursorRow > _scrollBottom) max = _rows - 1;
            b.CursorRow = Math.Clamp(b.CursorRow + dRow, min, max);
        }

        if (dCol != 0) b.CursorCol = Math.Clamp(b.CursorCol + dCol, 0, _cols - 1);
        b.PendingWrap = false;
    }

    private void Index()
    {
        var b = _b;
        b.PendingWrap = false;
        if (b.CursorRow == _scrollBottom) ScrollRegionUp(1);
        else if (b.CursorRow < _rows - 1) b.CursorRow++;
    }

    private void ReverseIndex()
    {
        var b = _b;
        b.PendingWrap = false;
        if (b.CursorRow == _scrollTop) ScrollRegionDown(1);
        else if (b.CursorRow > 0) b.CursorRow--;
    }

    private void TabForward(int n)
    {
        var b = _b;
        for (var i = 0; i < n; i++) b.CursorCol = Math.Min(_cols - 1, (b.CursorCol / 8 + 1) * 8);
        b.PendingWrap = false;
    }

    private void TabBackward(int n)
    {
        var b = _b;
        for (var i = 0; i < n; i++) b.CursorCol = Math.Max(0, ((b.CursorCol - 1) / 8) * 8);
        b.PendingWrap = false;
    }

    private void SetScrollRegion()
    {
        var top = P(0, 1) - 1;
        var bottom = P(1, _rows) - 1;
        if (top < 0) top = 0;
        if (bottom >= _rows) bottom = _rows - 1;
        if (top >= bottom)
        {
            _scrollTop = 0;
            _scrollBottom = _rows - 1;
        }
        else
        {
            _scrollTop = top;
            _scrollBottom = bottom;
        }

        SetCursor(0, 0);
    }

    // ===================================================================== screen editing

    private TermCell Blank() => new() { Rune = 0, Bg = _bg, Width = 1 };

    private void ScrollRegionUp(int n)
    {
        var whole = _scrollTop == 0 && _scrollBottom == _rows - 1 && !IsAlternateScreen;
        _b.ScrollUp(_scrollTop, _scrollBottom, n, Blank(), whole);
    }

    private void ScrollRegionDown(int n) => _b.ScrollDown(_scrollTop, _scrollBottom, n, Blank());

    private void InsertLines(int n)
    {
        var b = _b;
        if (b.CursorRow < _scrollTop || b.CursorRow > _scrollBottom) return;
        b.ScrollDown(b.CursorRow, _scrollBottom, n, Blank());
        b.CursorCol = 0;
        b.PendingWrap = false;
    }

    private void DeleteLines(int n)
    {
        var b = _b;
        if (b.CursorRow < _scrollTop || b.CursorRow > _scrollBottom) return;
        b.ScrollUp(b.CursorRow, _scrollBottom, n, Blank(), false);
        b.CursorCol = 0;
        b.PendingWrap = false;
    }

    private void InsertChars(int n)
    {
        var b = _b;
        var line = b.ScreenLine(b.CursorRow);
        n = Math.Min(n, _cols - b.CursorCol);
        for (var i = _cols - 1; i >= b.CursorCol + n; i--) line.Cells[i] = line.Cells[i - n];
        line.Clear(b.CursorCol, b.CursorCol + n, Blank());
        FixWideEdges(line);
        b.PendingWrap = false;
    }

    private void DeleteChars(int n)
    {
        var b = _b;
        var line = b.ScreenLine(b.CursorRow);
        n = Math.Min(n, _cols - b.CursorCol);
        for (var i = b.CursorCol; i < _cols - n; i++) line.Cells[i] = line.Cells[i + n];
        line.Clear(_cols - n, _cols, Blank());
        FixWideEdges(line);
        b.PendingWrap = false;
    }

    private void EraseChars(int n)
    {
        var b = _b;
        var line = b.ScreenLine(b.CursorRow);
        line.Clear(b.CursorCol, b.CursorCol + n, Blank());
        FixWideEdges(line);
        b.PendingWrap = false;
    }

    private void EraseInLine(int mode)
    {
        var b = _b;
        var line = b.ScreenLine(b.CursorRow);
        switch (mode)
        {
            case 0:
                line.Clear(b.CursorCol, _cols, Blank());
                line.Wrapped = false;
                break;
            case 1:
                line.Clear(0, b.CursorCol + 1, Blank());
                break;
            case 2:
                line.Clear(0, _cols, Blank());
                line.Wrapped = false;
                break;
        }

        FixWideEdges(line);
    }

    private void EraseInDisplay(int mode)
    {
        var b = _b;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var r = b.CursorRow + 1; r < _rows; r++) ClearRow(r);
                break;
            case 1:
                for (var r = 0; r < b.CursorRow; r++) ClearRow(r);
                EraseInLine(1);
                break;
            case 2:
                for (var r = 0; r < _rows; r++) ClearRow(r);
                break;
            case 3:
                b.ClearScrollback();
                break;
        }
    }

    private void ClearRow(int row)
    {
        var line = _b.ScreenLine(row);
        line.Clear(0, _cols, Blank());
        line.Wrapped = false;
    }

    private void FillScreenWithE()
    {
        for (var r = 0; r < _rows; r++)
        {
            var line = _b.ScreenLine(r);
            for (var c = 0; c < _cols; c++) line.Cells[c] = new TermCell { Rune = 'E', Width = 1 };
            line.Wrapped = false;
        }
    }

    /// <summary>After an edit, a wide character must not be left half there.</summary>
    private static void FixWideEdges(TermLine line)
    {
        var cells = line.Cells;
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i].Width == 2 && (i + 1 >= cells.Length || cells[i + 1].Width != 0))
            {
                cells[i].Rune = 0;
                cells[i].Width = 1;
            }
            else if (cells[i].Width == 0 && (i == 0 || cells[i - 1].Width != 2))
            {
                cells[i].Rune = 0;
                cells[i].Width = 1;
            }
        }
    }

    // ===================================================================== printing

    private void Print(int cp)
    {
        var w = CharWidth.Of(cp);
        if (w == 0) return;
        var b = _b;

        if (b.PendingWrap)
        {
            if (AutoWrap)
            {
                b.ScreenLine(b.CursorRow).Wrapped = true;
                b.CursorCol = 0;
                Index();
            }

            b.PendingWrap = false;
        }

        if (w == 2 && b.CursorCol == _cols - 1)
        {
            if (!AutoWrap) return;
            // a wide character does not fit in the last column: leave it blank and wrap
            b.ScreenLine(b.CursorRow).Cells[b.CursorCol] = Blank();
            b.ScreenLine(b.CursorRow).Wrapped = true;
            b.CursorCol = 0;
            Index();
        }

        if (_insertMode) InsertChars(w);

        var line = b.ScreenLine(b.CursorRow);
        var col = b.CursorCol;
        // overwriting half of a wide character blanks the other half
        if (line.Cells[col].Width == 0 && col > 0) line.Cells[col - 1] = Blank();
        if (line.Cells[col].Width == 2 && col + 1 < _cols) line.Cells[col + 1] = Blank();

        line.Cells[col] = new TermCell { Rune = cp, Fg = _fg, Bg = _bg, Attr = _attr, Width = (byte)w };
        if (w == 2 && col + 1 < _cols)
        {
            if (line.Cells[col + 1].Width == 2 && col + 2 < _cols) line.Cells[col + 2] = Blank();
            line.Cells[col + 1] = new TermCell { Rune = 0, Fg = _fg, Bg = _bg, Attr = _attr, Width = 0 };
        }

        col += w;
        if (col >= _cols)
        {
            b.CursorCol = _cols - 1;
            b.PendingWrap = true;
        }
        else
        {
            b.CursorCol = col;
        }
    }
}
