using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace sutty.UI.Helpers;

/// <summary>
/// A bounded, text-only VT screen model. It interprets cursor addressing, scrolling,
/// erasure and alternate-screen control sequences instead of appending stripped text.
/// SGR attributes are intentionally consumed without styling; the WinUI surface uses the
/// selected terminal foreground colour for every cell.
/// </summary>
public sealed class VtScreenBuffer
{
    private const int MaxEscapeLength = 128;
    private const char CursorUnderline = '\u0332';
    private const char BlankCursor = '\u2581';
    private readonly int _maxScrollback;
    private readonly List<string> _scrollback = [];
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _csi = new();
    private Screen _main;
    private Screen _alternate;
    private ParserState _parserState;
    private bool _usingAlternate;
    private bool _csiPrivate;
    private bool _cursorVisible = true;

    public VtScreenBuffer(int columns = 120, int rows = 40, int maxScrollback = 2_000)
    {
        Columns = Math.Clamp(columns, 20, 500);
        Rows = Math.Clamp(rows, 5, 200);
        _maxScrollback = Math.Clamp(maxScrollback, 0, 20_000);
        _main = new Screen(Columns, Rows);
        _alternate = new Screen(Columns, Rows);
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public bool IsAlternateScreen => _usingAlternate;
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>Raised for VT status/device queries that require a terminal reply.</summary>
    public event Action<string>? ResponseRequested;

    private Screen Active => _usingAlternate ? _alternate : _main;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, flush: false);
        for (var i = 0; i < count; i++)
            Process(chars[i]);
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        if (columns == Columns && rows == Rows)
            return;

        Columns = columns;
        Rows = rows;
        _main = _main.Resize(columns, rows);
        _alternate = _alternate.Resize(columns, rows);
    }

    public void Reset()
    {
        _decoder.Reset();
        _parserState = ParserState.Ground;
        _csi.Clear();
        _csiPrivate = false;
        _cursorVisible = true;
        ApplicationCursorKeys = false;
        _usingAlternate = false;
        _scrollback.Clear();
        _main = new Screen(Columns, Rows);
        _alternate = new Screen(Columns, Rows);
    }

    public string Render()
    {
        var screen = Active;
        var lines = new List<string>(Rows + (_usingAlternate ? 0 : _scrollback.Count));
        if (!_usingAlternate)
            lines.AddRange(_scrollback);

        for (var row = 0; row < screen.Rows; row++)
        {
            var cells = screen.GetRow(row);
            lines.Add(_cursorVisible && row == screen.CursorRow
                ? RenderCursorRow(cells, screen.CursorColumn)
                : new string(cells).TrimEnd(' '));
        }

        return string.Join('\n', lines);
    }

    private static string RenderCursorRow(char[] cells, int cursorColumn)
    {
        cursorColumn = Math.Clamp(cursorColumn, 0, cells.Length - 1);
        var lastContent = Array.FindLastIndex(cells, value => value != ' ');
        var lastColumn = Math.Max(lastContent, cursorColumn);
        var result = new StringBuilder(lastColumn + 2);

        for (var column = 0; column <= lastColumn; column++)
        {
            var value = cells[column];
            if (column != cursorColumn)
            {
                result.Append(value);
                continue;
            }

            // A block cursor replaced the cell and hid the character underneath it.
            // Keep occupied cells intact and add a zero-width underline; use a thin
            // lower bar only when the cursor is sitting on an empty cell.
            if (value == ' ')
                result.Append(BlankCursor);
            else
                result.Append(value).Append(CursorUnderline);
        }

        return result.ToString().TrimEnd(' ');
    }

    private void Process(char value)
    {
        switch (_parserState)
        {
            case ParserState.Ground:
                ProcessGround(value);
                break;
            case ParserState.Escape:
                ProcessEscape(value);
                break;
            case ParserState.Csi:
                ProcessCsi(value);
                break;
            case ParserState.Osc:
                if (value == '\a')
                    _parserState = ParserState.Ground;
                else if (value == '\x1b')
                    _parserState = ParserState.OscEscape;
                break;
            case ParserState.OscEscape:
                _parserState = value == '\\' ? ParserState.Ground : ParserState.Osc;
                break;
            case ParserState.Charset:
                _parserState = ParserState.Ground;
                break;
        }
    }

    private void ProcessGround(char value)
    {
        if (value == '\x1b')
        {
            _parserState = ParserState.Escape;
            return;
        }

        switch (value)
        {
            case '\0':
            case '\a':
                return;
            case '\r':
                Active.CarriageReturn();
                return;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return;
            case '\b':
                Active.Backspace();
                return;
            case '\t':
                Active.Tab();
                return;
        }

        if (!char.IsControl(value))
            Write(value);
    }

    private void ProcessEscape(char value)
    {
        _parserState = ParserState.Ground;
        switch (value)
        {
            case '[':
                _csi.Clear();
                _csiPrivate = false;
                _parserState = ParserState.Csi;
                break;
            case ']':
                _parserState = ParserState.Osc;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
                _parserState = ParserState.Charset;
                break;
            case '7':
                Active.SaveCursor();
                break;
            case '8':
                Active.RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                Active.CarriageReturn();
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'Z':
                ResponseRequested?.Invoke("\x1b[?1;2c");
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void ProcessCsi(char value)
    {
        if (_csi.Length == 0 && value is '?' or '>' or '!')
        {
            _csiPrivate = value == '?';
            return;
        }

        if (value is >= '@' and <= '~')
        {
            var parameters = ParseParameters();
            HandleCsi(value, parameters, _csiPrivate);
            _csi.Clear();
            _csiPrivate = false;
            _parserState = ParserState.Ground;
            return;
        }

        if ((char.IsDigit(value) || value is ';' or ':') && _csi.Length < MaxEscapeLength)
        {
            _csi.Append(value);
            return;
        }

        if (_csi.Length >= MaxEscapeLength || value == '\x1b')
        {
            _csi.Clear();
            _csiPrivate = false;
            _parserState = value == '\x1b' ? ParserState.Escape : ParserState.Ground;
        }
        // CSI intermediate bytes (space, quotes, etc.) are intentionally ignored.
    }

    private int[] ParseParameters()
    {
        if (_csi.Length == 0)
            return [];

        return _csi.ToString()
            .Split(';')
            .Select(part =>
            {
                var primary = part.Split(':')[0];
                return int.TryParse(primary, out var value) ? value : 0;
            })
            .ToArray();
    }

    private void HandleCsi(char command, int[] p, bool privateMode)
    {
        var screen = Active;
        var first = Param(p, 0, 1);

        switch (command)
        {
            case 'A': screen.MoveCursor(-first, 0); break;
            case 'B': screen.MoveCursor(first, 0); break;
            case 'C': screen.MoveCursor(0, first); break;
            case 'D': screen.MoveCursor(0, -first); break;
            case 'E': screen.MoveCursor(first, 0); screen.CarriageReturn(); break;
            case 'F': screen.MoveCursor(-first, 0); screen.CarriageReturn(); break;
            case 'G':
            case '`': screen.SetCursor(screen.CursorRow, first - 1); break;
            case 'd': screen.SetCursor(first - 1, screen.CursorColumn); break;
            case 'H':
            case 'f': screen.SetCursor(Param(p, 0, 1) - 1, Param(p, 1, 1) - 1); break;
            case 'J': EraseDisplay(p.Length == 0 ? 0 : p[0]); break;
            case 'K': EraseLine(p.Length == 0 ? 0 : p[0]); break;
            case 'm': break; // SGR is consumed but this text renderer intentionally has one style.
            case 's': screen.SaveCursor(); break;
            case 'u': screen.RestoreCursor(); break;
            case 'r': SetScrollRegion(p); break;
            case 'S': ScrollUp(first); break;
            case 'T': ScrollDown(first); break;
            case 'L': InsertLines(first); break;
            case 'M': DeleteLines(first); break;
            case '@': screen.InsertCharacters(first); break;
            case 'P': screen.DeleteCharacters(first); break;
            case 'X': screen.EraseCharacters(first); break;
            case 'h': SetMode(p, privateMode, enabled: true); break;
            case 'l': SetMode(p, privateMode, enabled: false); break;
            case 'n': ReportStatus(p, privateMode); break;
            case 'c': ResponseRequested?.Invoke("\x1b[?1;2c"); break;
        }
    }

    private static int Param(int[] parameters, int index, int defaultValue)
        => index >= parameters.Length || parameters[index] == 0
            ? defaultValue
            : Math.Max(1, parameters[index]);

    private void Write(char value)
    {
        var screen = Active;
        if (screen.WrapPending)
        {
            screen.CarriageReturn();
            LineFeed();
        }

        screen.Cells[screen.CursorRow, screen.CursorColumn] = value;
        if (screen.CursorColumn == screen.Columns - 1)
            screen.WrapPending = true;
        else
            screen.CursorColumn++;
    }

    private void LineFeed()
    {
        var screen = Active;
        screen.WrapPending = false;
        if (screen.CursorRow == screen.ScrollBottom)
        {
            ScrollUp(1);
            return;
        }

        screen.CursorRow = Math.Min(screen.Rows - 1, screen.CursorRow + 1);
    }

    private void ReverseIndex()
    {
        var screen = Active;
        screen.WrapPending = false;
        if (screen.CursorRow == screen.ScrollTop)
            ScrollDown(1);
        else
            screen.CursorRow = Math.Max(0, screen.CursorRow - 1);
    }

    private void ScrollUp(int count)
    {
        var screen = Active;
        count = Math.Clamp(count, 1, screen.ScrollBottom - screen.ScrollTop + 1);
        for (var n = 0; n < count; n++)
        {
            if (!_usingAlternate && screen.ScrollTop == 0 && screen.ScrollBottom == screen.Rows - 1)
                AddScrollback(new string(screen.GetRow(0)).TrimEnd(' '));

            for (var row = screen.ScrollTop; row < screen.ScrollBottom; row++)
                screen.CopyRow(row + 1, row);
            screen.ClearRow(screen.ScrollBottom);
        }
    }

    private void ScrollDown(int count)
    {
        var screen = Active;
        count = Math.Clamp(count, 1, screen.ScrollBottom - screen.ScrollTop + 1);
        for (var n = 0; n < count; n++)
        {
            for (var row = screen.ScrollBottom; row > screen.ScrollTop; row--)
                screen.CopyRow(row - 1, row);
            screen.ClearRow(screen.ScrollTop);
        }
    }

    private void AddScrollback(string line)
    {
        if (_maxScrollback == 0)
            return;
        _scrollback.Add(line);
        if (_scrollback.Count > _maxScrollback)
            _scrollback.RemoveRange(0, _scrollback.Count - _maxScrollback);
    }

    private void EraseDisplay(int mode)
    {
        var screen = Active;
        switch (mode)
        {
            case 0:
                screen.ClearRange(screen.CursorRow, screen.CursorColumn, screen.Columns - 1);
                for (var row = screen.CursorRow + 1; row < screen.Rows; row++)
                    screen.ClearRow(row);
                break;
            case 1:
                for (var row = 0; row < screen.CursorRow; row++)
                    screen.ClearRow(row);
                screen.ClearRange(screen.CursorRow, 0, screen.CursorColumn);
                break;
            case 2:
                screen.Clear();
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var screen = Active;
        switch (mode)
        {
            case 0: screen.ClearRange(screen.CursorRow, screen.CursorColumn, screen.Columns - 1); break;
            case 1: screen.ClearRange(screen.CursorRow, 0, screen.CursorColumn); break;
            case 2: screen.ClearRow(screen.CursorRow); break;
        }
    }

    private void SetScrollRegion(int[] parameters)
    {
        var screen = Active;
        var top = Param(parameters, 0, 1) - 1;
        var bottom = Param(parameters, 1, screen.Rows) - 1;
        if (top >= 0 && bottom < screen.Rows && top < bottom)
        {
            screen.ScrollTop = top;
            screen.ScrollBottom = bottom;
            screen.SetCursor(0, 0);
        }
    }

    private void InsertLines(int count)
    {
        var screen = Active;
        if (screen.CursorRow < screen.ScrollTop || screen.CursorRow > screen.ScrollBottom)
            return;
        count = Math.Clamp(count, 1, screen.ScrollBottom - screen.CursorRow + 1);
        for (var row = screen.ScrollBottom; row >= screen.CursorRow + count; row--)
            screen.CopyRow(row - count, row);
        for (var row = screen.CursorRow; row < screen.CursorRow + count; row++)
            screen.ClearRow(row);
    }

    private void DeleteLines(int count)
    {
        var screen = Active;
        if (screen.CursorRow < screen.ScrollTop || screen.CursorRow > screen.ScrollBottom)
            return;
        count = Math.Clamp(count, 1, screen.ScrollBottom - screen.CursorRow + 1);
        for (var row = screen.CursorRow; row <= screen.ScrollBottom - count; row++)
            screen.CopyRow(row + count, row);
        for (var row = screen.ScrollBottom - count + 1; row <= screen.ScrollBottom; row++)
            screen.ClearRow(row);
    }

    private void SetMode(int[] parameters, bool privateMode, bool enabled)
    {
        if (!privateMode)
            return;

        foreach (var mode in parameters)
        {
            switch (mode)
            {
                case 1:
                    ApplicationCursorKeys = enabled;
                    break;
                case 25:
                    _cursorVisible = enabled;
                    break;
                case 47:
                case 1047:
                case 1049:
                    if (enabled)
                        EnterAlternateScreen(clear: mode == 1049);
                    else
                        LeaveAlternateScreen();
                    break;
            }
        }
    }

    private void EnterAlternateScreen(bool clear)
    {
        if (_usingAlternate)
            return;
        _main.SaveCursor();
        if (clear)
            _alternate.Clear();
        _usingAlternate = true;
    }

    private void LeaveAlternateScreen()
    {
        if (!_usingAlternate)
            return;
        _usingAlternate = false;
        _main.RestoreCursor();
    }

    private void ReportStatus(int[] parameters, bool privateMode)
    {
        var code = parameters.Length == 0 ? 0 : parameters[0];
        if (code == 5)
        {
            ResponseRequested?.Invoke("\x1b[0n");
        }
        else if (code == 6)
        {
            var prefix = privateMode ? "?" : string.Empty;
            ResponseRequested?.Invoke(
                $"\x1b[{prefix}{Active.CursorRow + 1};{Active.CursorColumn + 1}R");
        }
    }

    private enum ParserState
    {
        Ground,
        Escape,
        Csi,
        Osc,
        OscEscape,
        Charset,
    }

    private sealed class Screen
    {
        public Screen(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            Cells = new char[rows, columns];
            ScrollBottom = rows - 1;
            Clear();
        }

        public int Columns { get; }
        public int Rows { get; }
        public char[,] Cells { get; }
        public int CursorRow { get; set; }
        public int CursorColumn { get; set; }
        public int SavedRow { get; set; }
        public int SavedColumn { get; set; }
        public int ScrollTop { get; set; }
        public int ScrollBottom { get; set; }
        public bool WrapPending { get; set; }

        public void Clear()
        {
            for (var row = 0; row < Rows; row++)
                ClearRow(row);
            CursorRow = 0;
            CursorColumn = 0;
            ScrollTop = 0;
            ScrollBottom = Rows - 1;
            WrapPending = false;
        }

        public void ClearRow(int row) => ClearRange(row, 0, Columns - 1);

        public void ClearRange(int row, int start, int end)
        {
            if (row < 0 || row >= Rows)
                return;
            start = Math.Clamp(start, 0, Columns - 1);
            end = Math.Clamp(end, 0, Columns - 1);
            for (var column = start; column <= end; column++)
                Cells[row, column] = ' ';
            WrapPending = false;
        }

        public char[] GetRow(int row)
        {
            var result = new char[Columns];
            for (var column = 0; column < Columns; column++)
                result[column] = Cells[row, column];
            return result;
        }

        public void CopyRow(int source, int destination)
        {
            for (var column = 0; column < Columns; column++)
                Cells[destination, column] = Cells[source, column];
        }

        public void SetCursor(int row, int column)
        {
            CursorRow = Math.Clamp(row, 0, Rows - 1);
            CursorColumn = Math.Clamp(column, 0, Columns - 1);
            WrapPending = false;
        }

        public void MoveCursor(int rowDelta, int columnDelta)
            => SetCursor(CursorRow + rowDelta, CursorColumn + columnDelta);

        public void CarriageReturn()
        {
            CursorColumn = 0;
            WrapPending = false;
        }

        public void Backspace()
        {
            CursorColumn = Math.Max(0, CursorColumn - 1);
            WrapPending = false;
        }

        public void Tab()
        {
            CursorColumn = Math.Min(Columns - 1, ((CursorColumn / 8) + 1) * 8);
            WrapPending = false;
        }

        public void SaveCursor()
        {
            SavedRow = CursorRow;
            SavedColumn = CursorColumn;
        }

        public void RestoreCursor() => SetCursor(SavedRow, SavedColumn);

        public void InsertCharacters(int count)
        {
            count = Math.Clamp(count, 1, Columns - CursorColumn);
            for (var column = Columns - 1; column >= CursorColumn + count; column--)
                Cells[CursorRow, column] = Cells[CursorRow, column - count];
            ClearRange(CursorRow, CursorColumn, CursorColumn + count - 1);
        }

        public void DeleteCharacters(int count)
        {
            count = Math.Clamp(count, 1, Columns - CursorColumn);
            for (var column = CursorColumn; column < Columns - count; column++)
                Cells[CursorRow, column] = Cells[CursorRow, column + count];
            ClearRange(CursorRow, Columns - count, Columns - 1);
        }

        public void EraseCharacters(int count)
            => ClearRange(CursorRow, CursorColumn, CursorColumn + Math.Max(1, count) - 1);

        public Screen Resize(int columns, int rows)
        {
            var resized = new Screen(columns, rows);
            var copyRows = Math.Min(Rows, rows);
            var copyColumns = Math.Min(Columns, columns);
            for (var row = 0; row < copyRows; row++)
            for (var column = 0; column < copyColumns; column++)
                resized.Cells[row, column] = Cells[row, column];
            resized.SetCursor(CursorRow, CursorColumn);
            resized.SavedRow = Math.Clamp(SavedRow, 0, rows - 1);
            resized.SavedColumn = Math.Clamp(SavedColumn, 0, columns - 1);
            return resized;
        }
    }
}
