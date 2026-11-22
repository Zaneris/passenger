using System.Text;
using Passenger.Text;

namespace Passenger.Terminal;

/// <summary>The cursor's shape, as the person chooses it or a program sets it (DECSCUSR): a block, an underline or a bar.</summary>
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
/// C0 controls, and ESC, CSI, OSC and DCS sequences through the one reader of them (<see cref="SequenceReader"/>), covering what
/// shells, editors and full-screen programs use (cursor movement, erasing, scroll regions, the alternate screen, 16,
/// 256 and 24-bit colours, the line-drawing set, bracketed paste, mouse reports, the kitty keyboard protocol's flags,
/// OSC 8's links).
/// Answers to a program's questions (where the cursor is, what the terminal is, the keyboard flags in force) come out of
/// <see cref="Reply"/>. No UI: the view draws it.</summary>
public sealed class TerminalScreen
{
    /// <summary>The most lines kept above the screen.</summary>
    public const int ScrollbackLimit = 10_000;

    /// <summary>The longest grapheme a cell holds, in UTF-16 units: a letter's marks beyond it are dropped.</summary>
    private const int MostJoined = 32;

    private readonly LineRing scrollback = new(ScrollbackLimit);
    private readonly Decoder utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly SequenceReader sequences = new();
    private char[] decoded = new char[4096];
    private TerminalLine[] main;
    private TerminalLine[]? alternate;
    private bool[] tabStops;
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

    /// <summary>The last character written, its whole grapheme where it is more than its first code point, and whether
    /// it came just before what the output has now, so REP has it to repeat.</summary>
    private int lastRune = ' ';
    private string? lastGrapheme;
    private bool repeatable;

    private Saved saved;
    private Saved savedMain;

    /// <summary>The kitty keyboard protocol's flags each screen's programs pushed, kept apart as the protocol asks.</summary>
    private readonly KeyboardStack mainKeyboard = new();
    private readonly KeyboardStack alternateKeyboard = new();

    public TerminalScreen(int columns = 80, int rows = 24)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        main = NewLines(Rows);
        Lines = main;
        bottom = Rows - 1;
        tabStops = Stops(Columns);
    }

    /// <summary>An answer for the program, to be written to its input: a cursor position, the terminal's identity.</summary>
    public event Action<string>? Reply;

    /// <summary>The program rang the bell.</summary>
    public event Action? Bell;

    /// <summary>A command the shell marked as run ended, with its exit status (OSC 133's D after its C).</summary>
    public event Action<ShellCommand>? CommandEnded;

    /// <summary>The command at the shell's latest prompt (OSC 133's A), and the one before it; null before any.</summary>
    public ShellCommand? Command { get; private set; }

    public ShellCommand? CommandBefore { get; private set; }

    /// <summary>Whether the shell marks a command's run (OSC 133's C), so a prompt's command ran only where it says so.</summary>
    public bool MarksRuns { get; private set; }

    /// <summary>The line the cursor stood on as Enter last went to the program on the main screen, typed, pasted or run;
    /// null before any, or once a reflow numbered the lines again.</summary>
    public long? EnteredAt { get; private set; }

    /// <summary>Enter went to the program: on the main screen, its line and its prompt's command are taken as entered.</summary>
    public void Entered()
    {
        if (IsAlternate)
        {
            return;
        }
        EnteredAt = ScreenTop + CursorRow;
        if (Command is { } command && command.Prompt <= EnteredAt)
        {
            command.Entered = true;
        }
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    /// <summary>The cursor's row and column on the screen, from zero.</summary>
    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;

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

    /// <summary>The kitty keyboard protocol's enhancements in force on the screen shown, as a program set them (CSI &gt;
    /// flags u, CSI &lt; n u, CSI = flags ; mode u), only those <see cref="TerminalKeys.Followed"/> kept: 0 until one asks.</summary>
    public int KeyboardFlags => Keyboard.Current;

    private KeyboardStack Keyboard => IsAlternate ? alternateKeyboard : mainKeyboard;

    /// <summary>The window title the program set (OSC 0 or 2).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The folder the shell said it is in (OSC 7), or null.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>What the last OSC 7 said, as it said it.</summary>
    private string? place;

    /// <summary>The address OSC 8 links what the program writes to, until it ends the link; null outside one.</summary>
    private string? hyperlink;

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
            if (sequences.InText)
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
            // The lines are numbered again, so what was marked by number is forgotten.
            (Command, CommandBefore, EnteredAt) = (null, null, null);
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
        List<CellExtra> carried = [];
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
            List<CellExtra>? graphemes = null;
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
    private static (int Row, int Column, bool Pending)? Wrap(ReadOnlySpan<Cell> text, List<CellExtra>? graphemes, int columns,
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
                        row.SetExtra(column, graphemes[cells[column].Joined - 1]);
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

    /// <summary>A character through the reader of control sequences, which tells the screen what it is or what it ends.</summary>
    private void Process(int rune) => sequences.Take(rune, new Reading(this));

    /// <summary>What the reader finds, acted on by the screen. Anything but a character written leaves REP nothing to
    /// repeat, as what precedes it is then no character written.</summary>
    private readonly struct Reading(TerminalScreen screen) : ISequenceActions
    {
        public void Print(int rune) => screen.Print(rune);

        public void Control(int rune)
        {
            screen.repeatable = false;
            screen.Control(rune);
        }

        public void Escape(SequenceReader sequence, char final)
        {
            screen.repeatable = false;
            screen.Escape(final);
        }

        public void Csi(SequenceReader sequence, char final)
        {
            screen.Csi(final);
            screen.repeatable = false;
        }

        public void Osc(ReadOnlySpan<char> text)
        {
            screen.repeatable = false;
            screen.Osc(text);
        }
    }

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
        int width = WideCharacters.Cells(rune);
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
        if (hyperlink is not null)
        {
            Lines[CursorRow].SetLink(CursorColumn, hyperlink);
        }
        if (width == 2)
        {
            Overwrite(cells, CursorColumn + 1);
            cells[CursorColumn + 1] = new Cell { Rune = 0, Style = style, Width = 0 };
        }
        (lastRune, lastGrapheme, repeatable) = (rune, null, true);
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
            if (hyperlink is not null)
            {
                for (int i = 0; i < take; i++)
                {
                    Lines[CursorRow].SetLink(column + i, hyperlink);
                }
            }
            (lastRune, lastGrapheme, repeatable) = (written.Rune, null, true);
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
            repeatable = false;
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
        (lastRune, lastGrapheme, repeatable) = (line.Cells[column].Rune, joined, true);
    }

    /// <summary>REP: the last character written, its whole grapheme, written again as many times, where it is what came
    /// just before (ECMA-48 leaves REP after anything else undefined, and it repeats nothing then).</summary>
    private void Repeat(int count)
    {
        if (!repeatable)
        {
            return;
        }
        (int rune, string? grapheme) = (lastRune, lastGrapheme);
        for (int i = 0; i < count; i++)
        {
            Print(rune);
            if (grapheme is not null && Previous() is int column and >= 0 && Lines[CursorRow].Cells[column].Rune == rune)
            {
                Lines[CursorRow].SetGrapheme(column, grapheme);
            }
        }
        lastGrapheme = grapheme;
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
    private bool JoinsFlag(int rune)
    {
        if (rune is not (>= 0x1F1E6 and <= 0x1F1FF) || Previous() is not (int column and >= 0))
        {
            return false;
        }
        // A linked letter stands alone too.
        return Lines[CursorRow].Cells[column].Rune is >= 0x1F1E6 and <= 0x1F1FF && Lines[CursorRow].IsOneCodePoint(column);
    }

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
        if (sequences.Only('('))
        {
            lineDrawing = final == '0';
            return;
        }
        if (sequences.Only('#') && final == '8')
        {
            // DECALN: the screen filled with Es, for aligning a display; the margins go to the screen's edges, the cursor home.
            foreach (TerminalLine line in Lines)
            {
                Cell[] cells = line.Writing();
                for (int i = 0; i < Columns; i++)
                {
                    cells[i] = new Cell { Rune = 'E', Style = style, Width = 1 };
                }
                line.Wrapped = false;
            }
            (top, bottom) = (0, Rows - 1);
            MoveTo(0, 0);
            return;
        }
        if (sequences.Intermediates > 0)
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
        SequenceParameters groups = sequences.Parameters;
        char privateMarker = sequences.PrivateMarker;
        int intermediates = sequences.Intermediates;
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
                case 'u' when intermediates == 0:
                    Reply?.Invoke($"\x1b[?{KeyboardFlags}u");
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
            else if (final == 'u' && intermediates == 0)
            {
                Keyboard.Push(Raw(0) & TerminalKeys.Followed);
            }
            return;
        }
        if (privateMarker == '<' && final == 'u' && intermediates == 0)
        {
            Keyboard.Pop(Get(0, 1));
            return;
        }
        if (privateMarker == '=' && final == 'u' && intermediates == 0)
        {
            Keyboard.Set(Raw(0) & TerminalKeys.Followed, Get(1, 1));
            return;
        }
        if (privateMarker != '\0')
        {
            return;
        }
        if (sequences.Only(' ') && final == 'q')
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
        if (sequences.Only('!') && final == 'p')
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
            case 'B':
                MoveTo(row + Get(0, 1), column, clampToRegion: true);
                break;
            case 'e':
                // VPR moves as VPA does, past the scroll region's bottom where it is not origin mode's.
                MoveTo(row + Get(0, 1), column);
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
                Repeat(Math.Min(Get(0, 1), Columns * Rows));
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
                style = GraphicRendition.Apply(style, groups);
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
                // A bottom past the screen's last row is its last row: the region is at most the screen.
                int first = Get(0, 1) - 1;
                int last = Math.Min(Get(1, Rows), Rows) - 1;
                if (last > first)
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
            if (!IsAlternate)
            {
                // A program coming to the alternate screen asks for its keys afresh, whatever one left there asked.
                alternateKeyboard.Clear();
            }
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

    /// <summary>An OSC's text read where it was kept: a string is made only for a title or a folder that is new.</summary>
    private void Osc(ReadOnlySpan<char> text)
    {
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
        else if (code is "8")
        {
            // OSC 8 ; params ; address: an empty address ends the link.
            int split = value.IndexOf(';');
            ReadOnlySpan<char> address = split < 0 ? [] : value[(split + 1)..];
            hyperlink = address.IsEmpty ? null : address.SequenceEqual(hyperlink) ? hyperlink : address.ToString();
        }
        else if (code is "133" && !value.IsEmpty && !IsAlternate)
        {
            Mark(value[0], value[1..]);
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

    /// <summary>A shell's mark (OSC 133): A a prompt begins at the cursor's line, C its command runs, D it ended with the
    /// status after it (0 where none is given). A second A on a prompt's line before its command ran is the same prompt, and
    /// a second D for one command, or one for a command never marked as run, is passed over.</summary>
    private void Mark(char kind, ReadOnlySpan<char> rest)
    {
        long line = ScreenTop + CursorRow;
        switch (kind)
        {
            case 'A':
                if (Command is not { Ran: false, Status: null } same || same.Prompt != line)
                {
                    CommandBefore = Command;
                    Command = new ShellCommand(line);
                }
                break;
            case 'C':
                Command ??= new ShellCommand(line);
                Command.Ran = true;
                MarksRuns = true;
                break;
            case 'D' when Command is { Ran: true, Status: null } ended:
                ReadOnlySpan<char> status = rest.StartsWith(";") ? rest[1..] : [];
                int end = status.IndexOf(';');
                ended.Status = int.TryParse(end < 0 ? status : status[..end], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int code) ? code : 0;
                CommandEnded?.Invoke(ended);
                break;
        }
    }

    /// <summary>The cursor put at a row and column, within the scroll region in origin mode. A move <paramref
    /// name="clampToRegion"/> (CUU, CUD, CNL, CPL) up stops at the region's top unless it starts above it, and one down
    /// at its bottom unless it starts below it.</summary>
    private void MoveTo(int row, int column, bool clampToRegion = false)
    {
        int low = originMode || (clampToRegion && CursorRow >= top) ? top : 0;
        int high = originMode || (clampToRegion && CursorRow <= bottom) ? bottom : Rows - 1;
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
        repeatable = false;
        CursorRow = 0;
        CursorColumn = 0;
        wrapPending = false;
        BracketedPaste = false;
        MouseTracking = MouseTracking.None;
        SgrMouse = false;
        mainKeyboard.Clear();
        alternateKeyboard.Clear();
        tabStops = Stops(Columns);
        Title = string.Empty;
        hyperlink = null;
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

    /// <summary>The DEC special graphics set from 0x5F, the box-drawing characters full-screen programs draw with.</summary>
    private static readonly int[] LineDrawing =
    [
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C,
        0x2514, 0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265,
        0x03C0, 0x2260, 0x00A3, 0x00B7,
    ];

    private readonly record struct Saved(int Row, int Column, CellStyle Style, bool Origin, bool WrapPending, bool LineDrawing);

    /// <summary>A screen's kitty keyboard flags: those set with nothing pushed, under at most <see cref="Most"/> pushed
    /// over them, a push past which drops the oldest; a pop that empties the stack resets every flag.</summary>
    private sealed class KeyboardStack
    {
        private const int Most = 16;

        /// <summary>The flags with nothing pushed, then each push's, the last in force.</summary>
        private readonly int[] entries = new int[Most + 1];
        private int depth;

        public int Current => entries[depth];

        public void Push(int flags)
        {
            if (depth == Most)
            {
                Array.Copy(entries, 1, entries, 0, Most);
            }
            else
            {
                depth++;
            }
            entries[depth] = flags;
        }

        public void Pop(int count)
        {
            if (count >= depth)
            {
                Clear();
            }
            else
            {
                depth -= count;
            }
        }

        /// <summary>The flags in force set (mode 1), added to (2) or taken from (3); another mode changes nothing.</summary>
        public void Set(int flags, int mode)
        {
            entries[depth] = mode switch
            {
                1 => flags,
                2 => entries[depth] | flags,
                3 => entries[depth] & ~flags,
                _ => entries[depth],
            };
        }

        public void Clear()
        {
            depth = 0;
            entries[0] = 0;
        }
    }
}

/// <summary>A command as the shell marks it (OSC 133): the line its prompt began on, whether the shell said it ran, whether
/// Enter went to the shell at it, and how it ended once it did.</summary>
public sealed class ShellCommand(long prompt)
{
    public long Prompt { get; } = prompt;

    public bool Ran { get; internal set; }

    public bool Entered { get; internal set; }

    /// <summary>Its exit status, once the shell said it ended; null before.</summary>
    public int? Status { get; internal set; }
}
