using System.Globalization;
using System.Text;

namespace Passenger.Terminal;

/// <summary>The cursor's shape, as a program sets it (DECSCUSR): a block, an underline or a bar.</summary>
public enum CursorShape
{
    Block,
    Underline,
    Bar,
}

/// <summary>Which mouse events a program asked to be told of (xterm's 1000, 1002 and 1003).</summary>
public enum MouseTracking
{
    None,
    Press,
    Drag,
    Any,
}

/// <summary>The screen a terminal shows: rows of cells, the cursor, the lines scrolled off the top, and the modes a
/// program sets. Bytes from the program go in through <see cref="Feed"/> and are read as xterm reads them: UTF-8 text,
/// C0 controls, and ESC, CSI, OSC and DCS sequences after the state machine in vt100.net's DEC parser, covering what
/// shells, editors and full-screen programs use (cursor movement, erasing, scroll regions, the alternate screen, 16,
/// 256 and 24-bit colours, the line-drawing set, bracketed paste and mouse reports). Answers to a program's questions
/// (where the cursor is, what the terminal is) come out of <see cref="Reply"/>. No UI: the view draws it.</summary>
public sealed class TerminalScreen
{
    /// <summary>The most lines kept above the screen.</summary>
    public const int ScrollbackLimit = 10_000;

    /// <summary>The longest grapheme a cell holds, in UTF-16 units: a letter's marks beyond it are dropped.</summary>
    private const int MostJoined = 32;

    /// <summary>The longest text an OSC keeps, in UTF-16 units: what a program writes past it is dropped.</summary>
    private const int MostOsc = 8192;

    private readonly LineRing scrollback = new(ScrollbackLimit);
    private readonly Decoder utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly Parameters parameters = new();
    private char[] osc = new char[256];
    private int oscLength;
    private char[] decoded = new char[4096];
    private TerminalLine[] main;
    private TerminalLine[]? alternate;
    private bool[] tabStops;
    private State state;
    private char privateMarker;

    /// <summary>The sequence's first intermediate byte (ESC ( and CSI SP q's), and how many it has.</summary>
    private int intermediate;
    private int intermediates;

    private char highSurrogate;
    private CellStyle style = CellStyle.Plain;
    private int top;
    private int bottom;
    private bool wrapPending;
    private bool autoWrap = true;
    private bool originMode;
    private bool insertMode;
    private bool lineDrawing;

    /// <summary>Whether the decoder may hold the start of a character: the last bytes it took were not ASCII.</summary>
    private bool utf8Open;

    private int lastRune = ' ';
    private Saved saved;
    private Saved savedMain;

    public TerminalScreen(int columns = 80, int rows = 24)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        main = NewLines(Rows);
        Lines = main;
        bottom = Rows - 1;
        tabStops = Stops(Columns);
    }

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        Text,
        TextEscape,
    }

    /// <summary>An answer for the program, to be written to its input: a cursor position, the terminal's identity.</summary>
    public event Action<string>? Reply;

    /// <summary>The program rang the bell.</summary>
    public event Action? Bell;

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    /// <summary>The cursor's row and column on the screen, from zero.</summary>
    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;
    public CursorShape CursorShape => RequestedCursorShape ?? CursorShape.Block;

    /// <summary>The program's cursor requests, or null to use the person's configured defaults.</summary>
    public CursorShape? RequestedCursorShape { get; private set; }
    public bool? RequestedCursorBlinking { get; private set; }

    /// <summary>Whether the arrow keys send their application forms (ESC O A rather than ESC [ A), as vim asks.</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>Whether a paste is to be bracketed, so a shell takes it as text rather than typed commands.</summary>
    public bool BracketedPaste { get; private set; }

    public MouseTracking MouseTracking { get; private set; }

    /// <summary>Whether mouse reports use the SGR form (1006), which has no limit on the column.</summary>
    public bool SgrMouse { get; private set; }

    /// <summary>Whether the alternate screen is up (a full-screen program's), which has no scrollback.</summary>
    public bool IsAlternate => alternate is not null && ReferenceEquals(Lines, alternate);

    /// <summary>The window title the program set (OSC 0 or 2).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The folder the shell said it is in (OSC 7), or null.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>What the last OSC 7 said, as it said it.</summary>
    private string? place;

    /// <summary>Counts every change, so a view knows to draw again.</summary>
    public int Version { get; private set; }

    /// <summary>The screen's rows.</summary>
    public TerminalLine[] Lines { get; private set; }

    /// <summary>The lines scrolled off the top, oldest first.</summary>
    public IReadOnlyList<TerminalLine> Scrollback => scrollback;

    /// <summary>Clear Buffer: the scrollback and the screen go, but for the cursor's line (the prompt waiting),
    /// which moves to the top. A full-screen program's alternate screen is its own, and stays.</summary>
    public void ClearBuffer()
    {
        if (IsAlternate)
        {
            return;
        }
        scrollback.Clear();
        TerminalLine prompt = Lines[CursorRow];
        for (int row = 0; row < Rows; row++)
        {
            Lines[row] = row == 0 ? prompt : new TerminalLine(Columns, CellStyle.Plain);
        }
        CursorRow = 0;
        Version++;
    }

    /// <summary>The number of the oldest line kept. Lines are numbered from the terminal's first, the scrollback's and the
    /// screen's together, and keep their numbers as the oldest go (past <see cref="ScrollbackLimit"/>, or cleared), so a
    /// selection or a read from a line on stays on its text while output streams; a reflow numbers them again.</summary>
    public long FirstLine => scrollback.Dropped;

    /// <summary>The number of the screen's top row: the scrollback's lines are those from <see cref="FirstLine"/> to it.</summary>
    public long ScreenTop => scrollback.Dropped + scrollback.Count;

    /// <summary>A line by its number, from <see cref="FirstLine"/> to the screen's last row.</summary>
    public TerminalLine LineAt(long line)
    {
        long index = line - scrollback.Dropped;
        return index < scrollback.Count ? scrollback[(int)index] : Lines[(int)(index - scrollback.Count)];
    }

    /// <summary>The screen's text, a row a line, trailing blanks left out.</summary>
    public string ScreenText() => string.Join('\n', Lines.Select(l => l.Text()));

    /// <summary>Takes the program's output, as many bytes as arrived; a character split between two calls is kept
    /// until the rest comes. A run of printable ASCII outside any sequence, most of what a log is, goes into its rows a
    /// row's worth at a time (<see cref="PrintText"/>); the rest a character at a time.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }
        int at = 0;
        while (at < bytes.Length)
        {
            ReadOnlySpan<byte> rest = bytes[at..];
            if (utf8Open || rest[0] >= 0x80)
            {
                // Decoded up to and with the next ASCII byte, which ends whatever sequence the decoder holds.
                int ascii = rest.IndexOfAnyInRange((byte)0, (byte)0x7F);
                int take = ascii < 0 ? rest.Length : ascii + 1;
                utf8Open = ascii < 0;
                Decode(rest[..take]);
                at += take;
                continue;
            }
            if (state == State.Ground)
            {
                int run = rest.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
                if (run != 0)
                {
                    int take = run < 0 ? rest.Length : run;
                    highSurrogate = '\0';
                    PrintText(rest[..take]);
                    at += take;
                    continue;
                }
            }
            highSurrogate = '\0';
            Process(rest[0]);
            at++;
        }
        Version++;
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    /// <summary>Bytes through the UTF-8 decoder, each character they make through the state machine; a pair of
    /// surrogates is one character.</summary>
    private void Decode(ReadOnlySpan<byte> bytes)
    {
        int most = utf8.GetCharCount(bytes, flush: false);
        if (decoded.Length < most)
        {
            decoded = new char[Math.Max(most, decoded.Length * 2)];
        }
        int count = utf8.GetChars(bytes, decoded, flush: false);
        for (int i = 0; i < count; i++)
        {
            char c = decoded[i];
            if (char.IsHighSurrogate(c))
            {
                highSurrogate = c;
                continue;
            }
            if (char.IsLowSurrogate(c))
            {
                if (highSurrogate != 0)
                {
                    Process(char.ConvertToUtf32(highSurrogate, c));
                }
                highSurrogate = '\0';
                continue;
            }
            highSurrogate = '\0';
            Process(c);
        }
    }

    /// <summary>A new size. At a new width the main screen and its scrollback reflow: rows the text wrapped across join
    /// and wrap again, so a line a narrower window cut reads whole once it widens, and the cursor stays on its
    /// character. A full-screen program draws its own screen again, so the alternate one is only cut or padded. Rows
    /// given up go to the scrollback (blank ones below the cursor first), rows gained come back from it.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }
        bool shown = IsAlternate;
        // The main screen's cursor is where 1049 saved it while a full-screen program has the alternate one.
        Saved mainCursor = shown ? savedMain : Capture();
        if (columns != Columns)
        {
            main = Reflow(main, columns, ref mainCursor);
        }
        int mainRow = mainCursor.Row;
        main = Fit(main, rows, columns, toScrollback: true, ref mainRow);
        mainCursor = mainCursor with { Row = mainRow };
        int alternateRow = shown ? CursorRow : Rows - 1;
        if (alternate is not null)
        {
            alternate = Fit(alternate, rows, columns, toScrollback: false, ref alternateRow);
        }
        Lines = shown ? alternate! : main;
        Columns = columns;
        Rows = rows;
        top = 0;
        bottom = rows - 1;
        tabStops = Stops(columns);
        if (shown)
        {
            savedMain = mainCursor with { Row = Math.Clamp(mainCursor.Row, 0, rows - 1) };
            CursorRow = Math.Clamp(alternateRow, 0, rows - 1);
            CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
            wrapPending = false;
        }
        else
        {
            CursorRow = Math.Clamp(mainCursor.Row, 0, rows - 1);
            CursorColumn = Math.Clamp(mainCursor.Column, 0, columns - 1);
            wrapPending = mainCursor.WrapPending && CursorColumn == columns - 1;
        }
        Version++;
    }

    /// <param name="cursor">The buffer's cursor row, which moves with its rows.</param>
    private TerminalLine[] Fit(TerminalLine[] lines, int rows, int columns, bool toScrollback, ref int cursor)
    {
        List<TerminalLine> list = [.. lines];
        foreach (TerminalLine line in list)
        {
            line.Resize(columns, CellStyle.Plain);
        }
        while (list.Count > rows)
        {
            // Blank rows under the cursor go first; then the top row, into the scrollback.
            if (list.Count - 1 > cursor && list[^1].Text().Length == 0)
            {
                list.RemoveAt(list.Count - 1);
                continue;
            }
            if (toScrollback)
            {
                AddScrollback(list[0]);
            }
            list.RemoveAt(0);
            cursor--;
        }
        while (list.Count < rows)
        {
            if (toScrollback && scrollback.Count > 0)
            {
                TerminalLine back = scrollback.RemoveLast();
                back.Resize(columns, CellStyle.Plain);
                list.Insert(0, back);
                cursor++;
            }
            else
            {
                list.Add(new TerminalLine(columns, CellStyle.Plain));
            }
        }
        return [.. list];
    }

    /// <summary>The main screen and the scrollback wrapped again at a new width, the screen keeping its row count: the
    /// rows of each line (joined where text wrapped) cut at the new width, a wide character never split. The screen
    /// keeps the cursor on the row it was on, pulling lines back from the scrollback as rows join, but rows a narrower
    /// width adds fill the blank ones under the cursor before any goes up into the scrollback.</summary>
    private TerminalLine[] Reflow(TerminalLine[] screen, int columns, ref Saved cursor)
    {
        // The screen's rows down to the last one written or the cursor's; blank rows below them are only room.
        int used = Math.Clamp(cursor.Row, 0, screen.Length - 1);
        for (int row = screen.Length - 1; row > used; row--)
        {
            if (screen[row].Length > 0 || screen[row].Wrapped)
            {
                used = row;
                break;
            }
        }
        List<TerminalLine> old = scrollback.TakeAll();
        int screenStart = old.Count;
        old.AddRange(screen.Take(used + 1));
        int cursorLine = screenStart + Math.Clamp(cursor.Row, 0, used);
        int cursorColumn = Math.Min(cursor.Column + (cursor.WrapPending ? 1 : 0), Columns);

        List<TerminalLine> rows = new(old.Count);
        Cell[] text = new Cell[Columns * 4];
        List<string> carried = [];
        (int Row, int Column, bool Pending) at = (0, 0, false);
        int screenTop = 0;
        for (int index = 0; index < old.Count;)
        {
            TerminalLine first = old[index];
            if (!first.Wrapped && index != cursorLine && index != screenStart && first.Length <= columns)
            {
                // A row of text that fits the new width stays one row; most do.
                first.Resize(columns, CellStyle.Plain);
                rows.Add(first);
                index++;
                continue;
            }
            // One line of text: rows joined while each wrapped into the next.
            int length = 0;
            int cursorOffset = -1;
            int topOffset = -1;
            ReadOnlySpan<Cell> joined = default;
            List<string>? graphemes = null;
            carried.Clear();
            while (true)
            {
                TerminalLine line = old[index];
                bool continues = line.Wrapped && index + 1 < old.Count;
                int take = continues ? line.Cells.Length : line.Length;
                if (continues && line.Cells[^1].Rune == 0 && old[index + 1].Cells[0].Width == 2)
                {
                    // A wide character too wide for the row's last column went on to the next, leaving a blank that is not text.
                    take--;
                }
                if (index == cursorLine)
                {
                    cursorOffset = length + cursorColumn;
                    take = Math.Max(take, Math.Min(cursorColumn, line.Cells.Length));
                }
                if (index == screenStart)
                {
                    topOffset = length;
                }
                index++;
                if (!continues && length == 0)
                {
                    // A line of one row is cut from its own cells.
                    joined = line.Cells.AsSpan(0, take);
                    graphemes = line.Graphemes;
                    break;
                }
                if (length + take > text.Length)
                {
                    Array.Resize(ref text, Math.Max(text.Length * 2, length + take));
                }
                Array.Copy(line.Cells, 0, text, length, take);
                if (line.Graphemes is { } own)
                {
                    // Each row numbers its own graphemes; joined, they are numbered in one list.
                    for (int i = length; i < length + take; i++)
                    {
                        if (text[i].Joined != 0)
                        {
                            carried.Add(own[text[i].Joined - 1]);
                            text[i].Joined = carried.Count < ushort.MaxValue ? (ushort)carried.Count : (ushort)0;
                        }
                    }
                    graphemes = carried;
                }
                length += take;
                if (!continues)
                {
                    joined = text.AsSpan(0, length);
                    break;
                }
            }
            if (Wrap(joined, graphemes, columns, rows, cursorOffset, topOffset, out int? topAt) is { } found)
            {
                at = found;
            }
            if (topAt is int topRow)
            {
                screenTop = topRow;
            }
        }

        // Keep the cursor's row on the screen; rows added above it take the blank room below before the scrollback.
        int count = rows.Count;
        int top = Math.Max(0, at.Row - cursor.Row);
        int free = Rows - (count - top);
        if (free > 0 && top > screenTop)
        {
            top = Math.Max(screenTop, top - free);
        }
        if (count - top > Rows)
        {
            top = Math.Min(at.Row, count - Rows);
        }
        for (int i = 0; i < top; i++)
        {
            AddScrollback(rows[i]);
        }
        TerminalLine[] lines = new TerminalLine[Rows];
        for (int row = 0; row < Rows; row++)
        {
            lines[row] = top + row < count ? rows[top + row] : new TerminalLine(columns, CellStyle.Plain);
        }
        cursor = cursor with { Row = at.Row - top, Column = at.Column, WrapPending = at.Pending };
        return lines;
    }

    /// <summary>A line of text cut into rows of a width, added to <paramref name="rows"/>, a wide character that would
    /// end a row going on to the next: where the cursor's offset lands (null when it is not in this line), and the row
    /// where the old screen's top began. <paramref name="graphemes"/> are those the text's joined cells number.</summary>
    private static (int Row, int Column, bool Pending)? Wrap(ReadOnlySpan<Cell> text, List<string>? graphemes, int columns,
        List<TerminalLine> rows, int cursorOffset, int topOffset, out int? topAt)
    {
        (int Row, int Column, bool Pending)? cursorAt = null;
        topAt = null;
        int start = 0;
        while (true)
        {
            int take = Math.Min(columns, text.Length - start);
            if (take == columns && text[start + columns - 1].Width == 2)
            {
                take--;
            }
            bool last = start + take >= text.Length;
            Cell[] cells = new Cell[columns];
            text.Slice(start, take).CopyTo(cells);
            cells.AsSpan(take).Fill(Cell.Blank(CellStyle.Plain));
            if (cursorOffset >= start && (cursorOffset < start + take || (last && cursorOffset == start + take)))
            {
                int column = cursorOffset - start;
                // After the text at the row's end, the cursor waits to wrap, as it did after the last column was written.
                cursorAt = column < columns ? (rows.Count, column, false) : (rows.Count, columns - 1, true);
            }
            if (topOffset >= start && (topOffset < start + take || last))
            {
                topAt ??= rows.Count;
            }
            TerminalLine row = new(cells, wrapped: !last);
            if (graphemes is { Count: > 0 })
            {
                for (int column = 0; column < take; column++)
                {
                    if (cells[column].Joined != 0)
                    {
                        row.SetGrapheme(column, graphemes[cells[column].Joined - 1]);
                    }
                }
            }
            rows.Add(row);
            if (last)
            {
                return cursorAt;
            }
            start += take;
        }
    }

    private void Process(int rune)
    {
        switch (state)
        {
            case State.Ground:
                if (rune == 0x1b)
                {
                    Begin(State.Escape);
                }
                else if (rune < 0x20 || rune == 0x7f)
                {
                    Control(rune);
                }
                else
                {
                    Print(rune);
                }
                break;
            case State.Escape:
                if (Interrupts(rune))
                {
                    break;
                }
                switch (rune)
                {
                    case '[':
                        state = State.Csi;
                        break;
                    case ']':
                        oscLength = 0;
                        state = State.Osc;
                        break;
                    case 'P' or 'X' or '^' or '_':
                        state = State.Text;
                        break;
                    case >= 0x20 and <= 0x2f:
                        Intermediate(rune);
                        state = State.EscapeIntermediate;
                        break;
                    default:
                        Escape((char)rune);
                        state = State.Ground;
                        break;
                }
                break;
            case State.EscapeIntermediate:
                if (Interrupts(rune))
                {
                    break;
                }
                if (rune is >= 0x20 and <= 0x2f)
                {
                    Intermediate(rune);
                }
                else
                {
                    Escape((char)rune);
                    state = State.Ground;
                }
                break;
            case State.Csi:
                if (Interrupts(rune))
                {
                    break;
                }
                if (rune is '<' or '=' or '>' or '?' && parameters.IsEmpty && privateMarker == '\0')
                {
                    privateMarker = (char)rune;
                }
                else if (rune is >= 0x30 and <= 0x3f)
                {
                    parameters.Take(rune);
                }
                else if (rune is >= 0x20 and <= 0x2f)
                {
                    Intermediate(rune);
                }
                else if (rune is >= 0x40 and <= 0x7e)
                {
                    parameters.End();
                    Csi((char)rune);
                    state = State.Ground;
                }
                break;
            case State.Osc:
                if (rune == 0x07)
                {
                    Osc();
                    state = State.Ground;
                }
                else if (rune == 0x1b)
                {
                    state = State.OscEscape;
                }
                else if (oscLength < MostOsc)
                {
                    if (osc.Length < oscLength + 2)
                    {
                        Array.Resize(ref osc, Math.Min(osc.Length * 2, MostOsc + 1));
                    }
                    oscLength += new Rune(rune).EncodeToUtf16(osc.AsSpan(oscLength));
                }
                break;
            case State.OscEscape:
                Osc();
                if (rune == '\\')
                {
                    state = State.Ground;
                }
                else
                {
                    Begin(State.Escape);
                    Process(rune);
                }
                break;
            case State.Text:
                // DCS, SOS, PM and APC: nothing here needs their contents, so they are passed over to their end.
                if (rune == 0x1b)
                {
                    state = State.TextEscape;
                }
                else if (rune is 0x07 or 0x18 or 0x1a)
                {
                    state = State.Ground;
                }
                break;
            case State.TextEscape:
                if (rune == '\\')
                {
                    state = State.Ground;
                }
                else
                {
                    Begin(State.Escape);
                    Process(rune);
                }
                break;
        }
    }

    /// <summary>A control inside a sequence: CAN and SUB abandon it, ESC starts another, the rest act as they would
    /// outside it.</summary>
    private bool Interrupts(int rune)
    {
        if (rune is 0x18 or 0x1a)
        {
            state = State.Ground;
            return true;
        }
        if (rune == 0x1b)
        {
            Begin(State.Escape);
            return true;
        }
        if (rune < 0x20 || rune == 0x7f)
        {
            Control(rune);
            return true;
        }
        return false;
    }

    private void Begin(State next)
    {
        parameters.Clear();
        intermediate = 0;
        intermediates = 0;
        privateMarker = '\0';
        state = next;
    }

    private void Intermediate(int rune)
    {
        if (intermediates++ == 0)
        {
            intermediate = rune;
        }
    }

    /// <summary>Whether the sequence's intermediates are just this one byte.</summary>
    private bool Only(char middle) => intermediates == 1 && intermediate == middle;

    private void Control(int rune)
    {
        switch (rune)
        {
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08:
                CursorColumn = Math.Max(0, Math.Min(CursorColumn, Columns - 1) - 1);
                wrapPending = false;
                break;
            case 0x09:
                Tab(1);
                break;
            case 0x0a or 0x0b or 0x0c:
                LineFeed();
                break;
            case 0x0d:
                CursorColumn = 0;
                wrapPending = false;
                break;
            case 0x0e or 0x0f:
                // SO and SI switch G1 and G0; only G0's line-drawing set is kept, so they change nothing.
                break;
        }
    }

    private void Print(int rune)
    {
        if (lineDrawing && rune is >= 0x5f and <= 0x7e)
        {
            rune = LineDrawing[rune - 0x5f];
        }
        int width = Width(rune);
        if (width == 0 || JoinsFlag(rune))
        {
            Join(rune);
            return;
        }
        if (wrapPending && autoWrap)
        {
            Lines[CursorRow].Wrapped = true;
            CursorColumn = 0;
            LineFeed();
        }
        wrapPending = false;
        if (width == 2 && CursorColumn == Columns - 1)
        {
            if (!autoWrap)
            {
                return;
            }
            Lines[CursorRow].Writing()[CursorColumn] = Cell.Blank(style);
            Lines[CursorRow].Wrapped = true;
            CursorColumn = 0;
            LineFeed();
        }
        Cell[] cells = Lines[CursorRow].Writing();
        if (insertMode)
        {
            Array.Copy(cells, CursorColumn, cells, CursorColumn + width, Columns - CursorColumn - width);
        }
        Overwrite(cells, CursorColumn);
        cells[CursorColumn] = new Cell { Rune = rune, Style = style, Width = (byte)width };
        if (width == 2)
        {
            Overwrite(cells, CursorColumn + 1);
            cells[CursorColumn + 1] = new Cell { Rune = 0, Style = style, Width = 0 };
        }
        lastRune = rune;
        if (CursorColumn + width >= Columns)
        {
            CursorColumn = Columns - 1;
            wrapPending = true;
        }
        else
        {
            CursorColumn += width;
        }
    }

    /// <summary>Printable ASCII written as <see cref="Print"/> writes each of its characters, a row's worth at a time: up
    /// to the row's end, where it wraps (or, with wrapping off, writes on over the last column), in insert mode pushing
    /// the row's rest right, and through the line-drawing set when it is chosen, each of whose characters is one column.</summary>
    private void PrintText(ReadOnlySpan<byte> text)
    {
        while (!text.IsEmpty)
        {
            if (wrapPending && autoWrap)
            {
                Lines[CursorRow].Wrapped = true;
                CursorColumn = 0;
                LineFeed();
            }
            wrapPending = false;
            int column = CursorColumn;
            int take = Math.Min(text.Length, Math.Max(1, Columns - column));
            Cell[] cells = Lines[CursorRow].Writing();
            if (insertMode)
            {
                if (!Narrow(cells.AsSpan(column, Columns - column)))
                {
                    // A wide character's half in the way is pushed and blanked a character at a time.
                    foreach (byte c in text[..take])
                    {
                        Print(c);
                    }
                    text = text[take..];
                    continue;
                }
                Array.Copy(cells, column, cells, column + take, Columns - column - take);
            }
            Cell written = new() { Style = style, Width = 1 };
            for (int i = 0; i < take; i++)
            {
                Overwrite(cells, column + i);
                written.Rune = Shown(text[i]);
                cells[column + i] = written;
            }
            lastRune = written.Rune;
            if (column + take >= Columns)
            {
                CursorColumn = Columns - 1;
                wrapPending = true;
            }
            else
            {
                CursorColumn = column + take;
            }
            text = text[take..];
        }
    }

    /// <summary>The character a printable ASCII byte shows: its own, or the line-drawing set's.</summary>
    private int Shown(byte c) => lineDrawing && c >= 0x5f ? LineDrawing[c - 0x5f] : c;

    /// <summary>Whether every cell is one column, with no half of a wide character among them.</summary>
    private static bool Narrow(ReadOnlySpan<Cell> cells)
    {
        foreach (Cell cell in cells)
        {
            if (cell.Width != 1)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A character with no column of its own (a combining mark, a joiner, a variation selector) joins the
    /// grapheme in the cell before the cursor, so é written as e and a combining acute shows and copies whole; at a
    /// row's start there is nothing to join, and it is dropped.</summary>
    private void Join(int rune)
    {
        int column = Previous();
        if (column < 0)
        {
            return;
        }
        TerminalLine line = Lines[CursorRow];
        string joined = line.TextOf(column) + char.ConvertFromUtf32(rune);
        if (joined.Length > MostJoined)
        {
            // A program writing marks without end grows no cell without end.
            return;
        }
        if (line.Cells[column].Rune == 0)
        {
            line.Writing()[column].Rune = ' ';
        }
        line.SetGrapheme(column, joined);
    }

    /// <summary>The cell the last character went into: before the cursor, or under it while a wrap is pending, the
    /// first half of a wide one; -1 at a row's start.</summary>
    private int Previous()
    {
        int column = wrapPending ? CursorColumn : CursorColumn - 1;
        if (column > 0 && Lines[CursorRow].Cells[column].Width == 0)
        {
            column--;
        }
        return column;
    }

    /// <summary>Whether a regional indicator is a flag's second letter: the one before it stands alone, so the two are
    /// one grapheme in the first's two columns, the two columns wcwidth gives the pair.</summary>
    private bool JoinsFlag(int rune) =>
        rune is >= 0x1F1E6 and <= 0x1F1FF && Previous() is int column and >= 0
        && Lines[CursorRow].Cells[column] is { Joined: 0, Rune: >= 0x1F1E6 and <= 0x1F1FF };

    /// <summary>Writing over half of a wide character blanks its other half.</summary>
    private void Overwrite(Cell[] cells, int column)
    {
        if (cells[column].Width == 0 && column > 0)
        {
            cells[column - 1] = Cell.Blank(style);
        }
        else if (cells[column].Width == 2 && column + 1 < cells.Length)
        {
            cells[column + 1] = Cell.Blank(style);
        }
    }

    private void LineFeed()
    {
        wrapPending = false;
        if (CursorRow == bottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    private void ReverseIndex()
    {
        wrapPending = false;
        if (CursorRow == top)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    /// <summary>The scroll region moves up: its top rows leave (into the scrollback when the region is the screen's
    /// top on the main screen) and blank rows come in at its bottom, each made of a line nothing keeps any more where
    /// there is one: the row that left, or the scrollback's oldest, which a full scrollback lets go.</summary>
    private void ScrollUp(int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (int i = 0; i < count; i++)
        {
            TerminalLine? left = Lines[top];
            if (top == 0 && !IsAlternate)
            {
                left = scrollback.Add(left);
            }
            Array.Copy(Lines, top + 1, Lines, top, bottom - top);
            Lines[bottom] = Blank(left);
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (int i = 0; i < count; i++)
        {
            TerminalLine left = Lines[bottom];
            Array.Copy(Lines, top, Lines, top + 1, bottom - top);
            Lines[top] = Blank(left);
        }
    }

    /// <summary>A blank row in the current background: <paramref name="left"/>, a line that has left the screen with
    /// nothing keeping it, cleared, or a new one.</summary>
    private TerminalLine Blank(TerminalLine? left) => left?.Reuse(Columns, Erasing) ?? new TerminalLine(Columns, Erasing);

    private void AddScrollback(TerminalLine line) => scrollback.Add(line);

    /// <summary>What erased cells take: the current background, and nothing else.</summary>
    private CellStyle Erasing => new(CellStyle.Default, style.Back, CellFlags.None);

    private void Escape(char final)
    {
        if (Only('('))
        {
            lineDrawing = final == '0';
            return;
        }
        if (Only('#') && final == '8')
        {
            // DECALN: the screen filled with Es, for aligning a display.
            foreach (TerminalLine line in Lines)
            {
                Cell[] cells = line.Writing();
                for (int i = 0; i < Columns; i++)
                {
                    cells[i] = new Cell { Rune = 'E', Style = CellStyle.Plain, Width = 1 };
                }
            }
            return;
        }
        if (intermediates > 0)
        {
            return;
        }
        switch (final)
        {
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorColumn = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'H':
                tabStops[Math.Min(CursorColumn, Columns - 1)] = true;
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void Csi(char final)
    {
        Parameters groups = parameters;
        int Get(int index, int fallback) => index < groups.Count && groups[index, 0] > 0 ? groups[index, 0] : fallback;
        int Raw(int index) => index < groups.Count ? Math.Max(0, groups[index, 0]) : 0;

        if (privateMarker == '?')
        {
            switch (final)
            {
                case 'h' or 'l':
                    for (int group = 0; group < groups.Count; group++)
                    {
                        SetMode(groups[group, 0], final == 'h');
                    }
                    break;
                case 'n' when Raw(0) == 6:
                    Reply?.Invoke($"\x1b[?{CursorRow + 1};{CursorColumn + 1}R");
                    break;
                case 'J':
                    EraseDisplay(Raw(0));
                    break;
                case 'K':
                    EraseLine(Raw(0));
                    break;
            }
            return;
        }
        if (privateMarker == '>')
        {
            if (final == 'c')
            {
                // A VT220-like terminal, as xterm answers the secondary attributes.
                Reply?.Invoke("\x1b[>1;10;0c");
            }
            return;
        }
        if (privateMarker != '\0')
        {
            return;
        }
        if (Only(' ') && final == 'q')
        {
            int requested = Raw(0);
            if (requested is < 0 or > 6)
            {
                return;
            }
            RequestedCursorShape = requested switch
            {
                0 => null,
                3 or 4 => CursorShape.Underline,
                5 or 6 => CursorShape.Bar,
                _ => CursorShape.Block,
            };
            RequestedCursorBlinking = requested == 0 ? null : requested % 2 == 1;
            return;
        }
        if (Only('!') && final == 'p')
        {
            SoftReset();
            return;
        }
        if (intermediates > 0)
        {
            return;
        }
        int row = CursorRow;
        int column = Math.Min(CursorColumn, Columns - 1);
        switch (final)
        {
            case '@':
                InsertCells(Get(0, 1));
                break;
            case 'A':
                MoveTo(row - Get(0, 1), column, clampToRegion: true);
                break;
            case 'B' or 'e':
                MoveTo(row + Get(0, 1), column, clampToRegion: true);
                break;
            case 'C' or 'a':
                MoveTo(row, column + Get(0, 1));
                break;
            case 'D':
                MoveTo(row, column - Get(0, 1));
                break;
            case 'E':
                MoveTo(row + Get(0, 1), 0, clampToRegion: true);
                break;
            case 'F':
                MoveTo(row - Get(0, 1), 0, clampToRegion: true);
                break;
            case 'G' or '`':
                MoveTo(row, Get(0, 1) - 1);
                break;
            case 'H' or 'f':
                MoveTo((originMode ? top : 0) + Get(0, 1) - 1, Get(1, 1) - 1);
                break;
            case 'I':
                Tab(Get(0, 1));
                break;
            case 'Z':
                Tab(-Get(0, 1));
                break;
            case 'J':
                EraseDisplay(Raw(0));
                break;
            case 'K':
                EraseLine(Raw(0));
                break;
            case 'L':
                InsertLines(Get(0, 1));
                break;
            case 'M':
                DeleteLines(Get(0, 1));
                break;
            case 'P':
                DeleteCells(Get(0, 1));
                break;
            case 'S':
                ScrollUp(Get(0, 1));
                break;
            case 'T' when groups.Count <= 1:
                ScrollDown(Get(0, 1));
                break;
            case 'X':
                Lines[row].Clear(column, column + Get(0, 1), Erasing);
                wrapPending = false;
                break;
            case 'b':
                for (int i = 0, n = Math.Min(Get(0, 1), Columns * Rows); i < n; i++)
                {
                    Print(lastRune);
                }
                break;
            case 'c':
                Reply?.Invoke("\x1b[?62;22c");
                break;
            case 'd':
                MoveTo((originMode ? top : 0) + Get(0, 1) - 1, column);
                break;
            case 'g':
                if (Raw(0) == 3)
                {
                    Array.Fill(tabStops, false);
                }
                else if (Raw(0) == 0)
                {
                    tabStops[column] = false;
                }
                break;
            case 'h' or 'l':
                for (int group = 0; group < groups.Count; group++)
                {
                    if (groups[group, 0] == 4)
                    {
                        insertMode = final == 'h';
                    }
                }
                break;
            case 'm':
                Sgr(groups);
                break;
            case 'n':
                if (Raw(0) == 5)
                {
                    Reply?.Invoke("\x1b[0n");
                }
                else if (Raw(0) == 6)
                {
                    Reply?.Invoke($"\x1b[{CursorRow + 1 - (originMode ? top : 0)};{column + 1}R");
                }
                break;
            case 'r':
                int first = Get(0, 1) - 1;
                int last = Get(1, Rows) - 1;
                if (last > first && last < Rows)
                {
                    top = first;
                    bottom = last;
                    MoveTo(originMode ? top : 0, 0);
                }
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 't' when Raw(0) == 18:
                Reply?.Invoke($"\x1b[8;{Rows};{Columns}t");
                break;
        }
    }

    private void SetMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 6:
                originMode = on;
                MoveTo(on ? top : 0, 0);
                break;
            case 7:
                autoWrap = on;
                break;
            case 12:
                RequestedCursorBlinking = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 47 or 1047:
                Alternate(on, clear: mode == 1047 && on);
                break;
            case 1049:
                if (on)
                {
                    savedMain = Capture();
                    Alternate(true, clear: true);
                }
                else
                {
                    Alternate(false, clear: false);
                    Restore(savedMain);
                }
                break;
            case 1000:
                MouseTracking = on ? MouseTracking.Press : MouseTracking.None;
                break;
            case 1002:
                MouseTracking = on ? MouseTracking.Drag : MouseTracking.None;
                break;
            case 1003:
                MouseTracking = on ? MouseTracking.Any : MouseTracking.None;
                break;
            case 1006:
                SgrMouse = on;
                break;
            case 2004:
                BracketedPaste = on;
                break;
        }
    }

    private void Alternate(bool on, bool clear)
    {
        if (on)
        {
            alternate ??= NewLines(Rows);
            if (clear)
            {
                alternate = NewLines(Rows);
            }
            Lines = alternate;
        }
        else
        {
            Lines = main;
        }
        top = 0;
        bottom = Rows - 1;
        wrapPending = false;
    }

    private void Sgr(Parameters groups)
    {
        if (groups.Count == 0)
        {
            style = CellStyle.Plain;
            return;
        }
        for (int i = 0; i < groups.Count; i++)
        {
            int code = groups[i, 0] >= 0 ? groups[i, 0] : 0;
            CellFlags flags = style.Flags;
            switch (code)
            {
                case 0:
                    style = CellStyle.Plain;
                    continue;
                case 1:
                    flags |= CellFlags.Bold;
                    break;
                case 2:
                    flags |= CellFlags.Dim;
                    break;
                case 3:
                    flags |= CellFlags.Italic;
                    break;
                case 4:
                    flags = groups.Parts(i) > 1 && groups[i, 1] == 0 ? flags & ~CellFlags.Underline : flags | CellFlags.Underline;
                    break;
                case 7:
                    flags |= CellFlags.Inverse;
                    break;
                case 8:
                    flags |= CellFlags.Hidden;
                    break;
                case 9:
                    flags |= CellFlags.Strike;
                    break;
                case 21:
                    flags |= CellFlags.Underline;
                    break;
                case 22:
                    flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    flags &= ~CellFlags.Underline;
                    break;
                case 27:
                    flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    flags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    flags &= ~CellFlags.Strike;
                    break;
                case >= 30 and <= 37:
                    style = style with { Fore = code - 30 };
                    continue;
                case 38:
                    if (Extended(groups, ref i) is int fore)
                    {
                        style = style with { Fore = fore };
                    }
                    continue;
                case 39:
                    style = style with { Fore = CellStyle.Default };
                    continue;
                case >= 40 and <= 47:
                    style = style with { Back = code - 40 };
                    continue;
                case 48:
                    if (Extended(groups, ref i) is int back)
                    {
                        style = style with { Back = back };
                    }
                    continue;
                case 49:
                    style = style with { Back = CellStyle.Default };
                    continue;
                case 58:
                    // The underline's own colour: read past it, since it is not drawn.
                    _ = Extended(groups, ref i);
                    continue;
                case >= 90 and <= 97:
                    style = style with { Fore = code - 90 + 8 };
                    continue;
                case >= 100 and <= 107:
                    style = style with { Back = code - 100 + 8 };
                    continue;
            }
            style = style with { Flags = flags };
        }
    }

    /// <summary>A colour after 38 or 48: 5;N from the palette or 2;R;G;B, in its own group's parts (38:2::R:G:B) or in
    /// the groups after it (38;2;R;G;B), which it then consumes.</summary>
    private static int? Extended(Parameters groups, ref int i)
    {
        int parts = groups.Parts(i);
        if (parts > 1)
        {
            if (groups[i, 1] == 5 && parts > 2)
            {
                return Math.Clamp(groups[i, 2], 0, 255);
            }
            if (groups[i, 1] == 2 && parts >= 5)
            {
                // 38:2:R:G:B, or 38:2:SPACE:R:G:B with a colour space first.
                int first = parts >= 6 ? 3 : 2;
                return CellStyle.FromRgb(Byte(groups[i, first]), Byte(groups[i, first + 1]), Byte(groups[i, first + 2]));
            }
            return null;
        }
        int at = i;
        switch (Next(groups, at + 1))
        {
            case 5:
                int index = Next(groups, at + 2);
                i += 2;
                return index >= 0 ? Math.Min(index, 255) : null;
            case 2:
                int r = Next(groups, at + 2);
                int g = Next(groups, at + 3);
                int b = Next(groups, at + 4);
                i += 4;
                return CellStyle.FromRgb(Byte(r), Byte(g), Byte(b));
            default:
                return null;
        }

        static int Next(Parameters groups, int index) => index < groups.Count ? groups[index, 0] : -1;

        static int Byte(int value) => Math.Clamp(value, 0, 255);
    }

    /// <summary>An OSC's text read where it was kept: a string is made only for a title or a folder that is new.</summary>
    private void Osc()
    {
        ReadOnlySpan<char> text = osc.AsSpan(0, oscLength);
        int semicolon = text.IndexOf(';');
        ReadOnlySpan<char> code = semicolon < 0 ? text : text[..semicolon];
        ReadOnlySpan<char> value = semicolon < 0 ? [] : text[(semicolon + 1)..];
        if (code is "0" or "2")
        {
            if (!value.SequenceEqual(Title))
            {
                Title = value.ToString();
            }
        }
        else if (code is "7" && !value.SequenceEqual(place))
        {
            place = value.ToString();
            if (Uri.TryCreate(place, UriKind.Absolute, out Uri? folder) && folder.IsFile)
            {
                WorkingDirectory = Uri.UnescapeDataString(folder.AbsolutePath);
            }
        }
    }

    private void MoveTo(int row, int column, bool clampToRegion = false)
    {
        int low = originMode || (clampToRegion && CursorRow >= top && CursorRow <= bottom) ? top : 0;
        int high = originMode || (clampToRegion && CursorRow >= top && CursorRow <= bottom) ? bottom : Rows - 1;
        CursorRow = Math.Clamp(row, low, high);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
        wrapPending = false;
    }

    private void Tab(int count)
    {
        int column = Math.Min(CursorColumn, Columns - 1);
        int step = Math.Sign(count);
        for (int n = Math.Abs(count); n > 0; n--)
        {
            do
            {
                column += step;
            }
            while (column > 0 && column < Columns - 1 && !tabStops[column]);
        }
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
        wrapPending = false;
    }

    private void EraseDisplay(int how)
    {
        int column = Math.Min(CursorColumn, Columns - 1);
        switch (how)
        {
            case 0:
                Lines[CursorRow].Clear(column, Columns, Erasing);
                for (int r = CursorRow + 1; r < Rows; r++)
                {
                    Lines[r] = new TerminalLine(Columns, Erasing);
                }
                break;
            case 1:
                for (int r = 0; r < CursorRow; r++)
                {
                    Lines[r] = new TerminalLine(Columns, Erasing);
                }
                Lines[CursorRow].Clear(0, column + 1, Erasing);
                break;
            case 2:
                for (int r = 0; r < Rows; r++)
                {
                    Lines[r] = new TerminalLine(Columns, Erasing);
                }
                break;
            case 3:
                scrollback.Clear();
                break;
        }
        wrapPending = false;
    }

    private void EraseLine(int how)
    {
        int column = Math.Min(CursorColumn, Columns - 1);
        TerminalLine line = Lines[CursorRow];
        switch (how)
        {
            case 0:
                line.Clear(column, Columns, Erasing);
                line.Wrapped = false;
                break;
            case 1:
                line.Clear(0, column + 1, Erasing);
                break;
            case 2:
                line.Clear(0, Columns, Erasing);
                line.Wrapped = false;
                break;
        }
        wrapPending = false;
    }

    private void InsertLines(int count)
    {
        if (CursorRow < top || CursorRow > bottom)
        {
            return;
        }
        int saveTop = top;
        top = CursorRow;
        ScrollDown(count);
        top = saveTop;
        CursorColumn = 0;
        wrapPending = false;
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < top || CursorRow > bottom)
        {
            return;
        }
        int saveTop = top;
        top = CursorRow;
        for (int i = 0; i < Math.Min(count, bottom - top + 1); i++)
        {
            TerminalLine left = Lines[top];
            Array.Copy(Lines, top + 1, Lines, top, bottom - top);
            Lines[bottom] = Blank(left);
        }
        top = saveTop;
        CursorColumn = 0;
        wrapPending = false;
    }

    private void InsertCells(int count)
    {
        int column = Math.Min(CursorColumn, Columns - 1);
        Cell[] cells = Lines[CursorRow].Writing();
        count = Math.Min(count, Columns - column);
        Array.Copy(cells, column, cells, column + count, Columns - column - count);
        Lines[CursorRow].Clear(column, column + count, Erasing);
        wrapPending = false;
    }

    private void DeleteCells(int count)
    {
        int column = Math.Min(CursorColumn, Columns - 1);
        Cell[] cells = Lines[CursorRow].Writing();
        count = Math.Min(count, Columns - column);
        Array.Copy(cells, column + count, cells, column, Columns - column - count);
        Lines[CursorRow].Clear(Columns - count, Columns, Erasing);
        wrapPending = false;
    }

    private void SaveCursor() => saved = Capture();

    private void RestoreCursor() => Restore(saved);

    private Saved Capture() => new(CursorRow, CursorColumn, style, originMode, wrapPending, lineDrawing);

    private void Restore(Saved from)
    {
        CursorRow = Math.Clamp(from.Row, 0, Rows - 1);
        CursorColumn = Math.Clamp(from.Column, 0, Columns - 1);
        style = from.Style;
        originMode = from.Origin;
        wrapPending = from.WrapPending;
        lineDrawing = from.LineDrawing;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        RequestedCursorShape = null;
        RequestedCursorBlinking = null;
        originMode = false;
        autoWrap = true;
        insertMode = false;
        ApplicationCursorKeys = false;
        top = 0;
        bottom = Rows - 1;
        style = CellStyle.Plain;
        lineDrawing = false;
        saved = default;
    }

    private void Reset()
    {
        SoftReset();
        scrollback.Clear();
        main = NewLines(Rows);
        alternate = null;
        Lines = main;
        CursorRow = 0;
        CursorColumn = 0;
        wrapPending = false;
        BracketedPaste = false;
        MouseTracking = MouseTracking.None;
        SgrMouse = false;
        tabStops = Stops(Columns);
        Title = string.Empty;
    }

    private TerminalLine[] NewLines(int rows)
    {
        TerminalLine[] lines = new TerminalLine[rows];
        for (int i = 0; i < rows; i++)
        {
            lines[i] = new TerminalLine(Columns, CellStyle.Plain);
        }
        return lines;
    }

    private static bool[] Stops(int columns)
    {
        bool[] stops = new bool[columns];
        for (int i = 8; i < columns; i += 8)
        {
            stops[i] = true;
        }
        return stops;
    }

    /// <summary>How many columns a character takes, as xterm.js's Unicode 11 widths and wcwidth give it: none for a
    /// combining mark, a format character (a joiner, a direction mark) or a Hangul vowel or final that joins its
    /// syllable; two for East Asian Wide and Fullwidth characters and emoji (<see cref="WideCharacters"/>); one else.</summary>
    public static int Width(int rune)
    {
        if (rune < 0x300)
        {
            return 1;
        }
        if (rune is >= 0x1160 and <= 0x11FF or >= 0xD7B0 and <= 0xD7FF)
        {
            return 0;
        }
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }
        return WideCharacters.Contains(rune) ? 2 : 1;
    }

    /// <summary>The DEC special graphics set from 0x5F, the box-drawing characters full-screen programs draw with.</summary>
    private static readonly int[] LineDrawing =
    [
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C,
        0x2514, 0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265,
        0x03C0, 0x2260, 0x00A3, 0x00B7,
    ];

    private readonly record struct Saved(int Row, int Column, CellStyle Style, bool Origin, bool WrapPending, bool LineDrawing);

    /// <summary>A CSI's parameters, read as their bytes arrive: groups apart at semicolons, each group's parts apart at
    /// colons (SGR's 38:2:R:G:B), each part a number up to 65535, or -1 when it is empty, holds a byte other than a digit
    /// or is past an int's range. Its arrays are kept from one sequence to the next, grown only past sixteen parts.</summary>
    private sealed class Parameters
    {
        private int[] values = new int[16];

        /// <summary>Where each group's parts start among <see cref="values"/>.</summary>
        private int[] starts = new int[16];

        private int count;
        private long number;
        private bool digits;
        private bool bad;

        /// <summary>How many groups the sequence has: none until a parameter byte comes.</summary>
        public int Count { get; private set; }

        /// <summary>Whether no parameter byte has come yet.</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>A group's part; every group has its first.</summary>
        public int this[int group, int part] => values[starts[group] + part];

        public int Parts(int group) => (group + 1 < Count ? starts[group + 1] : count) - starts[group];

        public void Clear()
        {
            Count = 0;
            count = 0;
            number = 0;
            digits = false;
            bad = false;
        }

        /// <summary>Takes a parameter byte, 0x30 to 0x3F.</summary>
        public void Take(int c)
        {
            if (Count == 0)
            {
                Open();
            }
            switch (c)
            {
                case >= '0' and <= '9':
                    digits = true;
                    if (!bad)
                    {
                        number = (number * 10) + (c - '0');
                        bad = number > int.MaxValue;
                    }
                    break;
                case ';':
                    EndPart();
                    Open();
                    break;
                case ':':
                    EndPart();
                    break;
                default:
                    bad = true;
                    break;
            }
        }

        /// <summary>The sequence's final byte has come: its last part ends.</summary>
        public void End()
        {
            if (Count > 0)
            {
                EndPart();
            }
        }

        private void Open()
        {
            if (Count == starts.Length)
            {
                Array.Resize(ref starts, starts.Length * 2);
            }
            starts[Count++] = count;
        }

        private void EndPart()
        {
            if (count == values.Length)
            {
                Array.Resize(ref values, values.Length * 2);
            }
            values[count++] = digits && !bad ? (int)Math.Min(number, 65535) : -1;
            number = 0;
            digits = false;
            bad = false;
        }
    }
}
