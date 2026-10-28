using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Terminal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiCodeAgent.App.Controls;

/// <summary>
/// Draws a <see cref="TerminalEmulator"/> and turns keyboard, mouse wheel and selection into terminal input.
/// Cells are drawn in runs (one text run per stretch of equal colours); the grid size follows the control's size, and the
/// session is told about every change so the shell reflows its output.
/// </summary>
public class TerminalControl : Control
{
    private const double Pad = 6;

    public static readonly StyledProperty<ITerminalSession?> SessionProperty =
        AvaloniaProperty.Register<TerminalControl, ITerminalSession?>(nameof(Session));

    public static readonly StyledProperty<IBrush?> DefaultForegroundProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(DefaultForeground));

    public static readonly StyledProperty<IBrush?> DefaultBackgroundProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(DefaultBackground));

    public static readonly StyledProperty<FontFamily> TerminalFontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(TerminalFontFamily),
            new FontFamily("Cascadia Mono, Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, monospace"));

    public static readonly StyledProperty<double> TerminalFontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(TerminalFontSize), 13d);

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly Typeface[] _typefaces = new Typeface[4];
    private double _cellW;
    private double _cellH;
    private string? _metricsKey;
    private int _cols;
    private int _rows;
    private long? _pinnedTopAbs;
    private (long Abs, int Col)? _selAnchor;
    private (long Abs, int Col)? _selCaret;
    private bool _selecting;
    private bool _subscribed;
    private int _invalidatePending;

    static TerminalControl()
    {
        FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
        AffectsRender<TerminalControl>(DefaultForegroundProperty, DefaultBackgroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TerminalControl>(true);
    }

    public ITerminalSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public IBrush? DefaultForeground
    {
        get => GetValue(DefaultForegroundProperty);
        set => SetValue(DefaultForegroundProperty, value);
    }

    public IBrush? DefaultBackground
    {
        get => GetValue(DefaultBackgroundProperty);
        set => SetValue(DefaultBackgroundProperty, value);
    }

    public FontFamily TerminalFontFamily
    {
        get => GetValue(TerminalFontFamilyProperty);
        set => SetValue(TerminalFontFamilyProperty, value);
    }

    public double TerminalFontSize
    {
        get => GetValue(TerminalFontSizeProperty);
        set => SetValue(TerminalFontSizeProperty, value);
    }

    /// <summary>Columns and rows that fit the control right now.</summary>
    public (int Cols, int Rows) GridSize => (_cols, _rows);

    public bool HasSelection => _selAnchor != null && _selCaret != null && _selAnchor != _selCaret;

    /// <summary>The selected text (empty when nothing is selected).</summary>
    public string SelectedText
    {
        get
        {
            var session = Session;
            if (session == null || !HasSelection) return string.Empty;
            var (start, end) = SelectionRange();
            lock (session.Emulator.SyncRoot)
                return session.Emulator.Buffer.GetText(start.Abs, start.Col, end.Abs, end.Col + 1);
        }
    }

    // ----- session wiring -----

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            if (change.OldValue is ITerminalSession old) Unsubscribe(old);
            _pinnedTopAbs = null;
            _selAnchor = _selCaret = null;
            if (change.NewValue is ITerminalSession session && VisualRoot != null) Subscribe(session);
            ApplyGrid(force: true);
            InvalidateVisual();
        }
        else if (change.Property == TerminalFontFamilyProperty || change.Property == TerminalFontSizeProperty)
        {
            _metricsKey = null;
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Session is { } session) Subscribe(session);
        ApplyGrid(force: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Session is { } session) Unsubscribe(session);
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe(ITerminalSession session)
    {
        if (_subscribed) return;
        session.Changed += OnSessionChanged;
        _subscribed = true;
    }

    private void Unsubscribe(ITerminalSession session)
    {
        if (!_subscribed) return;
        session.Changed -= OnSessionChanged;
        _subscribed = false;
    }

    private void OnSessionChanged()
    {
        // The shell can print thousands of times a second: one repaint per UI-thread turn is enough.
        if (Interlocked.Exchange(ref _invalidatePending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _invalidatePending, 0);
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    // ----- size -----

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        ApplyGrid(force: false, size);
        return size;
    }

    private void EnsureMetrics()
    {
        var key = TerminalFontFamily.ToString() + "|" + TerminalFontSize.ToString(CultureInfo.InvariantCulture);
        if (key == _metricsKey) return;
        _metricsKey = key;
        var family = TerminalFontFamily;
        _typefaces[0] = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        _typefaces[1] = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _typefaces[2] = new Typeface(family, FontStyle.Italic, FontWeight.Normal);
        _typefaces[3] = new Typeface(family, FontStyle.Italic, FontWeight.Bold);
        var sample = new FormattedText(new string('M', 40), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typefaces[0], TerminalFontSize, Brushes.White);
        _cellW = Math.Max(1, sample.Width / 40);
        _cellH = Math.Max(1, Math.Ceiling(sample.Height));
    }

    private void ApplyGrid(bool force, Size? size = null)
    {
        EnsureMetrics();
        var s = size ?? Bounds.Size;
        if (s.Width < _cellW * 2 || s.Height < _cellH * 2) return;
        var cols = Math.Max(2, (int)Math.Floor((s.Width - Pad * 2) / _cellW));
        var rows = Math.Max(2, (int)Math.Floor((s.Height - Pad * 2) / _cellH));
        if (!force && cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        Session?.Resize(cols, rows);
        InvalidateVisual();
    }

    // ----- drawing -----

    private static readonly uint[] DarkPalette =
    {
        0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF
    };

    private static readonly uint[] LightPalette =
    {
        0x000000, 0xCD3131, 0x00BC00, 0x949800, 0x0451A5, 0xBC05BC, 0x0598BC, 0x555555,
        0x666666, 0xCD3131, 0x14CE14, 0xB5BA00, 0x0451A5, 0xBC05BC, 0x0598BC, 0xA5A5A5
    };

    private static readonly byte[] CubeLevels = { 0, 95, 135, 175, 215, 255 };

    private static Color FromRgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color PaletteColor(int index, bool dark)
    {
        if (index < 16) return FromRgb((dark ? DarkPalette : LightPalette)[index]);
        if (index < 232)
        {
            var i = index - 16;
            return Color.FromRgb(CubeLevels[i / 36], CubeLevels[i / 6 % 6], CubeLevels[i % 6]);
        }

        var gray = (byte)(8 + 10 * (index - 232));
        return Color.FromRgb(gray, gray, gray);
    }

    private SolidColorBrush SolidBrush(Color color)
    {
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        if (_brushes.Count > 600) _brushes.Clear();
        brush = new SolidColorBrush(color);
        _brushes[key] = brush;
        return brush;
    }

    private static Color BrushColor(IBrush? brush, Color fallback) => brush is ISolidColorBrush solid ? solid.Color : fallback;

    private long ViewTopAbs(TerminalBuffer buffer)
    {
        var bottomTop = buffer.TrimmedCount + Math.Max(0, buffer.LineCount - _rows);
        if (_pinnedTopAbs is not { } pinned) return bottomTop;
        if (pinned >= bottomTop)
        {
            _pinnedTopAbs = null;
            return bottomTop;
        }

        return Math.Max(buffer.TrimmedCount, pinned);
    }

    public override void Render(DrawingContext context)
    {
        var fgDefault = BrushColor(DefaultForeground, Color.FromRgb(0xD4, 0xD4, 0xD4));
        var bgDefault = BrushColor(DefaultBackground, Color.FromRgb(0x1E, 0x1E, 0x2E));
        context.FillRectangle(SolidBrush(bgDefault), new Rect(Bounds.Size));

        var session = Session;
        if (session == null) return;
        EnsureMetrics();
        if (_cols == 0 || _rows == 0) return;

        var dark = Luminance(bgDefault) < 0.5;
        var emulator = session.Emulator;
        lock (emulator.SyncRoot)
        {
            var buffer = emulator.Buffer;
            var firstAbs = ViewTopAbs(buffer);
            var cols = Math.Min(_cols, buffer.Cols);

            for (var r = 0; r < _rows; r++)
            {
                var abs = firstAbs + r;
                var index = (int)(abs - buffer.TrimmedCount);
                if (index < 0 || index >= buffer.LineCount) continue;
                DrawRow(context, buffer.Line(index), abs, r, cols, fgDefault, bgDefault, dark);
            }

            // cursor (only when following the live screen)
            if (_pinnedTopAbs == null && emulator.CursorVisible)
            {
                var (cursorRow, cursorCol) = emulator.CursorPosition;
                var row = buffer.TrimmedCount + buffer.Top + cursorRow - firstAbs;
                if (row >= 0 && row < _rows && cursorCol < _cols)
                    DrawCursor(context, buffer, (int)(buffer.Top + cursorRow), cursorCol, (int)row, fgDefault, bgDefault);
            }

            DrawScrollIndicator(context, buffer, firstAbs, fgDefault);
        }
    }

    private void DrawRow(DrawingContext context, TermLine line, long abs, int row, int cols, Color fgDefault, Color bgDefault, bool dark)
    {
        var y = Pad + row * _cellH;
        var cells = line.Cells;
        var n = Math.Min(cols, cells.Length);

        // 1. backgrounds, merged over equal neighbours
        var col = 0;
        while (col < n)
        {
            var (_, bg, _) = Resolve(cells[col], fgDefault, bgDefault, dark);
            var end = col + 1;
            while (end < n && Resolve(cells[end], fgDefault, bgDefault, dark).Bg == bg) end++;
            if (bg != bgDefault)
                context.FillRectangle(SolidBrush(bg), new Rect(Pad + col * _cellW, y, (end - col) * _cellW, _cellH));
            col = end;
        }

        // 2. selection
        if (HasSelection)
        {
            var (start, end) = SelectionRange();
            if (abs >= start.Abs && abs <= end.Abs)
            {
                var from = abs == start.Abs ? start.Col : 0;
                var to = abs == end.Abs ? end.Col : n - 1;
                from = Math.Clamp(from, 0, n - 1);
                to = Math.Clamp(to, 0, n - 1);
                if (to >= from)
                    context.FillRectangle(SolidBrush(Color.FromArgb(0x66, 0x4A, 0x9E, 0xFF)),
                        new Rect(Pad + from * _cellW, y, (to - from + 1) * _cellW, _cellH));
            }
        }

        // 3. text, in runs of equal colour and style (blanks inside a run are kept so the columns stay aligned)
        var run = new StringBuilder();
        var runStart = 0;
        var runNext = 0;
        Color runFg = default;
        CellAttr runStyle = default;

        void Flush()
        {
            if (run.Length == 0) return;
            var text = run.ToString();
            var cellsUsed = runNext - runStart;
            // trailing blanks carry nothing unless they are underlined or struck through
            if ((runStyle & (CellAttr.Underline | CellAttr.Strike)) == 0)
            {
                var trimmed = text.TrimEnd(' ');
                cellsUsed -= text.Length - trimmed.Length;
                text = trimmed;
            }

            if (text.Length > 0) DrawText(context, text, Pad + runStart * _cellW, y, runFg, runStyle, cellsUsed);
            run.Clear();
        }

        for (var c = 0; c < n; c++)
        {
            var cell = cells[c];
            if (cell.Width == 0) continue; // tail of a wide character
            var (fg, _, attr) = Resolve(cell, fgDefault, bgDefault, dark);
            var style = attr & (CellAttr.Bold | CellAttr.Italic | CellAttr.Underline | CellAttr.Strike);
            var blank = cell.Rune == 0 || cell.Rune == ' ' || (attr & CellAttr.Hidden) != 0;

            if (blank)
            {
                if (run.Length > 0 && c == runNext && (style & (CellAttr.Underline | CellAttr.Strike)) == (runStyle & (CellAttr.Underline | CellAttr.Strike)))
                {
                    run.Append(' ');
                    runNext = c + 1;
                }
                else
                {
                    Flush();
                    if ((style & (CellAttr.Underline | CellAttr.Strike)) != 0)
                    {
                        run.Append(' ');
                        runStart = c;
                        runNext = c + 1;
                        runFg = fg;
                        runStyle = style;
                    }
                }

                continue;
            }

            if (cell.Width == 2)
            {
                Flush();
                DrawText(context, RuneText(cell.Rune), Pad + c * _cellW, y, fg, style, 2);
                continue;
            }

            if (run.Length > 0 && (runFg != fg || runStyle != style || c != runNext)) Flush();
            if (run.Length == 0)
            {
                runStart = c;
                runFg = fg;
                runStyle = style;
            }

            run.Append(RuneText(cell.Rune));
            runNext = c + 1;
        }

        Flush();
    }

    private static string RuneText(int rune)
    {
        if (rune == 0) return " ";
        if (rune > 0x10FFFF || rune is >= 0xD800 and <= 0xDFFF) return "�";
        return char.ConvertFromUtf32(rune);
    }

    private void DrawText(DrawingContext context, string text, double x, double y, Color fg, CellAttr attr, int cells)
    {
        var typeface = _typefaces[((attr & CellAttr.Bold) != 0 ? 1 : 0) + ((attr & CellAttr.Italic) != 0 ? 2 : 0)];
        var brush = SolidBrush(fg);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, TerminalFontSize, brush);
        context.DrawText(formatted, new Point(x, y));
        var width = cells * _cellW;
        if ((attr & CellAttr.Underline) != 0)
            context.DrawLine(new Pen(brush, 1), new Point(x, y + _cellH - 2), new Point(x + width, y + _cellH - 2));
        if ((attr & CellAttr.Strike) != 0)
            context.DrawLine(new Pen(brush, 1), new Point(x, y + _cellH / 2), new Point(x + width, y + _cellH / 2));
    }

    private (Color Fg, Color Bg, CellAttr Attr) Resolve(TermCell cell, Color fgDefault, Color bgDefault, bool dark)
    {
        var fg = cell.Fg.IsDefault ? fgDefault : cell.Fg.IsRgb ? Color.FromRgb(cell.Fg.R, cell.Fg.G, cell.Fg.B) : PaletteColor(cell.Fg.Index, dark);
        var bg = cell.Bg.IsDefault ? bgDefault : cell.Bg.IsRgb ? Color.FromRgb(cell.Bg.R, cell.Bg.G, cell.Bg.B) : PaletteColor(cell.Bg.Index, dark);
        if ((cell.Attr & CellAttr.Inverse) != 0) (fg, bg) = (bg, fg);
        if ((cell.Attr & CellAttr.Dim) != 0) fg = Blend(fg, bg, 0.5);
        return (fg, bg, cell.Attr);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void DrawCursor(DrawingContext context, TerminalBuffer buffer, int lineIndex, int col, int row, Color fgDefault, Color bgDefault)
    {
        var x = Pad + col * _cellW;
        var y = Pad + row * _cellH;
        var width = _cellW;
        if (lineIndex >= 0 && lineIndex < buffer.LineCount)
        {
            var cells = buffer.Line(lineIndex).Cells;
            if (col < cells.Length && cells[col].Width == 2) width = _cellW * 2;
        }

        var rect = new Rect(x, y, width, _cellH);
        if (IsFocused)
        {
            context.FillRectangle(SolidBrush(Color.FromArgb(0xB0, fgDefault.R, fgDefault.G, fgDefault.B)), rect);
            if (lineIndex >= 0 && lineIndex < buffer.LineCount && col < buffer.Line(lineIndex).Cells.Length)
            {
                var cell = buffer.Line(lineIndex).Cells[col];
                if (cell.Rune > ' ' && (cell.Attr & CellAttr.Hidden) == 0)
                    DrawText(context, RuneText(cell.Rune), x, y, bgDefault, CellAttr.None, 1);
            }
        }
        else
        {
            context.DrawRectangle(null, new Pen(SolidBrush(fgDefault), 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
        }
    }

    private void DrawScrollIndicator(DrawingContext context, TerminalBuffer buffer, long firstAbs, Color fg)
    {
        if (_pinnedTopAbs == null || buffer.LineCount <= _rows) return;
        var track = Bounds.Height - Pad * 2;
        var total = (double)buffer.LineCount;
        var thumbHeight = Math.Max(16, track * _rows / total);
        var offset = (firstAbs - buffer.TrimmedCount) / total * track;
        context.FillRectangle(SolidBrush(Color.FromArgb(0x70, fg.R, fg.G, fg.B)),
            new Rect(Bounds.Width - 5, Pad + offset, 3, Math.Min(thumbHeight, track - offset)));
    }

    // ----- selection -----

    private ((long Abs, int Col) Start, (long Abs, int Col) End) SelectionRange()
    {
        var a = _selAnchor ?? default;
        var b = _selCaret ?? default;
        var swap = a.Abs > b.Abs || (a.Abs == b.Abs && a.Col > b.Col);
        return swap ? (b, a) : (a, b);
    }

    private (long Abs, int Col) CellAt(Point point)
    {
        var session = Session;
        var col = Math.Clamp((int)Math.Floor((point.X - Pad) / _cellW), 0, Math.Max(0, _cols - 1));
        var row = Math.Clamp((int)Math.Floor((point.Y - Pad) / _cellH), 0, Math.Max(0, _rows - 1));
        if (session == null) return (0, col);
        lock (session.Emulator.SyncRoot)
            return (ViewTopAbs(session.Emulator.Buffer) + row, col);
    }

    public void ClearSelection()
    {
        if (_selAnchor == null && _selCaret == null) return;
        _selAnchor = _selCaret = null;
        InvalidateVisual();
    }

    private void SelectWord((long Abs, int Col) cell)
    {
        var session = Session;
        if (session == null) return;
        lock (session.Emulator.SyncRoot)
        {
            var buffer = session.Emulator.Buffer;
            var index = (int)(cell.Abs - buffer.TrimmedCount);
            if (index < 0 || index >= buffer.LineCount) return;
            var cells = buffer.Line(index).Cells;
            bool IsWordChar(int c) => c < cells.Length && cells[c].Rune > ' ' && "()[]{}<>\"'`,;|".IndexOf((char)Math.Min(cells[c].Rune, 0xFFFF)) < 0;
            if (cell.Col >= cells.Length || !IsWordChar(cell.Col)) return;
            var from = cell.Col;
            var to = cell.Col;
            while (from > 0 && IsWordChar(from - 1)) from--;
            while (to + 1 < cells.Length && IsWordChar(to + 1)) to++;
            _selAnchor = (cell.Abs, from);
            _selCaret = (cell.Abs, to);
        }

        InvalidateVisual();
    }

    private void SelectLine((long Abs, int Col) cell)
    {
        _selAnchor = (cell.Abs, 0);
        _selCaret = (cell.Abs, Math.Max(0, _cols - 1));
        InvalidateVisual();
    }

    // ----- scrolling -----

    /// <summary>Scrolls the view; positive lines move towards older output.</summary>
    public void ScrollBy(int lines)
    {
        var session = Session;
        if (session == null) return;
        lock (session.Emulator.SyncRoot)
        {
            var buffer = session.Emulator.Buffer;
            var bottomTop = buffer.TrimmedCount + Math.Max(0, buffer.LineCount - _rows);
            var current = _pinnedTopAbs ?? bottomTop;
            var next = Math.Clamp(current - lines, buffer.TrimmedCount, bottomTop);
            _pinnedTopAbs = next >= bottomTop ? null : next;
        }

        InvalidateVisual();
    }

    public void ScrollToBottom()
    {
        if (_pinnedTopAbs == null) return;
        _pinnedTopAbs = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var session = Session;
        if (session == null) return;
        var lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0) lines = e.Delta.Y > 0 ? 1 : -1;
        if (session.Emulator.IsAlternateScreen)
        {
            // Full-screen programs (less, vim) have no scrollback of their own here: the wheel moves their cursor.
            var arrow = lines > 0 ? (session.Emulator.AppCursorKeys ? "\u001bOA" : "\u001b[A") : (session.Emulator.AppCursorKeys ? "\u001bOB" : "\u001b[B");
            session.SendInput(string.Concat(Enumerable.Repeat(arrow, Math.Abs(lines))));
        }
        else
        {
            ScrollBy(lines);
        }

        e.Handled = true;
    }

    // ----- mouse -----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (HasSelection)
            {
                _ = CopySelectionAsync();
                ClearSelection();
            }
            else
            {
                _ = PasteAsync();
            }

            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;
        var cell = CellAt(point.Position);
        if (e.ClickCount == 2)
        {
            SelectWord(cell);
        }
        else if (e.ClickCount >= 3)
        {
            SelectLine(cell);
        }
        else
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selAnchor != null)
            {
                _selCaret = cell;
            }
            else
            {
                _selAnchor = cell;
                _selCaret = cell;
            }

            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var position = e.GetPosition(this);
        // dragging above or below the text scrolls it
        if (position.Y < Pad) ScrollBy(1);
        else if (position.Y > Bounds.Height - Pad) ScrollBy(-1);
        _selCaret = CellAt(position);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
        if (_selAnchor == _selCaret) ClearSelection();
    }

    // ----- keyboard -----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var session = Session;
        if (session == null)
        {
            base.OnKeyDown(e);
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // copy / paste
        if ((ctrl && shift && e.Key == Key.C) || (ctrl && !shift && e.Key == Key.Insert) ||
            (ctrl && !shift && !alt && e.Key == Key.C && HasSelection))
        {
            _ = CopySelectionAsync();
            if (HasSelection) ClearSelection();
            e.Handled = true;
            return;
        }

        if ((ctrl && shift && e.Key == Key.V) || (shift && !ctrl && e.Key == Key.Insert) ||
            (OperatingSystem.IsWindows() && ctrl && !shift && !alt && e.Key == Key.V))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        // scrollback with the keyboard
        if (shift && !ctrl && !alt && e.Key == Key.PageUp)
        {
            ScrollBy(Math.Max(1, _rows - 1));
            e.Handled = true;
            return;
        }

        if (shift && !ctrl && !alt && e.Key == Key.PageDown)
        {
            ScrollBy(-Math.Max(1, _rows - 1));
            e.Handled = true;
            return;
        }

        var text = TerminalKeyEncoder.Encode(e.Key, e.KeyModifiers, session.Emulator.AppCursorKeys);
        if (text != null)
        {
            ClearSelection();
            ScrollToBottom();
            session.SendInput(text);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        var session = Session;
        var text = e.Text;
        if (session == null || string.IsNullOrEmpty(text)) return;
        // Control characters arrive through OnKeyDown; here only typed text counts.
        if (text.All(ch => ch < 0x20 || ch == 0x7F)) return;
        ClearSelection();
        ScrollToBottom();
        session.SendInput(text);
        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    // ----- clipboard -----

    private async Task CopySelectionAsync()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    private async Task PasteAsync()
    {
        var session = Session;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (session == null || clipboard == null) return;
        var text = await clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text)) return;
        ClearSelection();
        ScrollToBottom();
        session.SendInput(TerminalKeyEncoder.EncodePaste(text, session.Emulator.BracketedPaste));
    }
}
