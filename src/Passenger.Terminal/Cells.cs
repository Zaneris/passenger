namespace Passenger.Terminal;

/// <summary>How a cell's text is drawn, beyond its colours.</summary>
[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strike = 32,
    Hidden = 64,
}

/// <summary>A cell's colours and flags. A colour is <see cref="Default"/>, a palette index (0 to 255, the first sixteen
/// the theme's), or a 24-bit colour marked by <see cref="Rgb"/>.</summary>
public readonly record struct CellStyle(int Fore, int Back, CellFlags Flags)
{
    public const int Default = -1;
    public const int Rgb = 1 << 24;

    public static readonly CellStyle Plain = new(Default, Default, CellFlags.None);

    public static int FromRgb(int r, int g, int b) => Rgb | (r << 16) | (g << 8) | b;
}

/// <summary>One cell of the screen: its grapheme's first code point (0 for a blank), its style, and how many columns it
/// takes: 2 for a wide character, whose second column is a cell of width 0. A grapheme of more code points (e and a
/// combining acute, a flag's two letters), and the address a program linked the cell to, are kept by its line
/// (<see cref="TerminalLine.TextOf"/>, <see cref="TerminalLine.LinkOf"/>), so a cell holds no reference for the collector
/// to track through the millions a flood of output writes.</summary>
public struct Cell
{
    /// <summary>The grapheme's first code point; 0 for a blank.</summary>
    public int Rune;

    private int fore;
    private int back;
    private CellFlags flags;

    public byte Width;

    /// <summary>Which of its line's extras the cell shows (<see cref="CellExtra"/>), from 1; 0 when <see cref="Rune"/> is all
    /// of it and it is linked to nothing.</summary>
    internal ushort Joined;

    public CellStyle Style
    {
        readonly get => new(fore, back, flags);
        set
        {
            fore = value.Fore;
            back = value.Back;
            flags = value.Flags;
        }
    }

    public static Cell Blank(CellStyle style) => new() { Rune = 0, Style = style with { Flags = CellFlags.None }, Width = 1 };

    public readonly bool IsBlank => Rune == 0;

    /// <summary>Whether the cell's grapheme is more than its first code point, or it is linked.</summary>
    public readonly bool IsJoined => Joined != 0;

    /// <summary>Whether another cell shows what this one does: its code point, colours, flags and width, neither one's
    /// grapheme more than that code point.</summary>
    internal readonly bool Shows(Cell other) =>
        Rune == other.Rune && fore == other.fore && back == other.back && flags == other.flags && Width == other.Width && Joined == 0 && other.Joined == 0;
}

/// <summary>What a cell shows beyond its first code point: its whole grapheme, and the address a program linked it to with
/// OSC 8, or null.</summary>
internal readonly record struct CellExtra(string Text, string? Link);

/// <summary>A row of cells, and whether the text went on to the next row by wrapping (so a copy joins them).</summary>
public sealed class TerminalLine
{
    private List<CellExtra>? graphemes;
    private bool wrapped;

    public TerminalLine(int columns, CellStyle style)
    {
        Cells = new Cell[columns];
        Clear(0, columns, style);
    }

    internal TerminalLine(Cell[] cells, bool wrapped)
    {
        Cells = cells;
        this.wrapped = wrapped;
    }

    public Cell[] Cells { get; private set; }

    public bool Wrapped
    {
        get => wrapped;
        set
        {
            if (wrapped != value)
            {
                wrapped = value;
                Writes++;
            }
        }
    }

    /// <summary>How many times the line has been written (its cells, graphemes or wrap): what a view drew of it still
    /// shows it while this stays the same.</summary>
    internal long Writes { get; private set; }

    /// <summary>The cells, to be written: the write counted.</summary>
    internal Cell[] Writing()
    {
        Writes++;
        return Cells;
    }

    /// <summary>What the line's joined cells show, by their <see cref="Cell.Joined"/> less one.</summary>
    internal List<CellExtra>? Graphemes => graphemes;

    /// <summary>Whether a program linked any of its cells since it was last blank: none is, where it did not.</summary>
    internal bool Linked { get; private set; }

    public void Clear(int from, int to, CellStyle style)
    {
        int start = Math.Max(0, from);
        int end = Math.Min(to, Cells.Length);
        if (start < end)
        {
            Cells.AsSpan(start, end - start).Fill(Cell.Blank(style));
            Writes++;
        }
    }

    /// <summary>The line blank again at a width, to come onto the screen as a new row once it has left for good.</summary>
    internal TerminalLine Reuse(int columns, CellStyle style)
    {
        if (Cells.Length != columns)
        {
            Cells = new Cell[columns];
        }
        Clear(0, columns, style);
        Wrapped = false;
        graphemes?.Clear();
        Linked = false;
        Writes++;
        return this;
    }

    public void Resize(int columns, CellStyle style)
    {
        if (columns == Cells.Length)
        {
            return;
        }
        Cell[] cells = new Cell[columns];
        Array.Copy(Cells, cells, Math.Min(columns, Cells.Length));
        for (int i = Cells.Length; i < columns; i++)
        {
            cells[i] = Cell.Blank(style);
        }
        Cells = cells;
        Writes++;
    }

    /// <summary>What the cell at a column shows: its whole grapheme, a space for a blank.</summary>
    public string TextOf(int column) => TextOf(Cells[column]);

    /// <summary>Makes a cell show a grapheme of more than one code point, linked as it was.</summary>
    internal void SetGrapheme(int column, string grapheme) => SetExtra(column, new CellExtra(grapheme, LinkOf(column)));

    /// <summary>Links a cell to an address, as a program's OSC 8 does the text it writes.</summary>
    internal void SetLink(int column, string link) => SetExtra(column, new CellExtra(TextOf(column), link));

    /// <summary>Whether the cell at a column shows its first code point alone, linked or not.</summary>
    internal bool IsOneCodePoint(int column) =>
        Cells[column] is not { Joined: not 0 and var joined, Rune: var rune } || graphemes is null || graphemes[joined - 1].Text.Length == (rune > 0xFFFF ? 2 : 1);

    /// <summary>The address a program linked the cell at a column to, or null.</summary>
    public string? LinkOf(int column) => Cells[column].Joined is not 0 and var joined && graphemes is not null ? graphemes[joined - 1].Link : null;

    internal void SetExtra(int column, CellExtra extra)
    {
        if (graphemes is null)
        {
            graphemes = [];
        }
        else if (graphemes.Count >= Math.Min(ushort.MaxValue, (Cells.Length * 2) + 16))
        {
            Compact();
        }
        graphemes.Add(extra);
        Cells[column].Joined = (ushort)graphemes.Count;
        Linked |= extra.Link is not null;
        Writes++;
    }

    /// <summary>The row's text, trailing blanks and spaces left out.</summary>
    public string Text(int from = 0, int to = int.MaxValue)
    {
        System.Text.StringBuilder text = new();
        Span<char> scratch = stackalloc char[2];
        int end = Math.Min(to, Cells.Length);
        int last = end - 1;
        while (last >= from && Cells[last] is { Rune: 0 or ' ', Joined: 0 })
        {
            last--;
        }
        for (int i = Math.Max(0, from); i <= last; i++)
        {
            if (Cells[i].Width != 0)
            {
                text.Append(TextOf(i, scratch));
            }
        }
        return text.ToString();
    }

    /// <summary>How many characters <see cref="Text"/> gives of the whole row, counted with no string made.</summary>
    public int TextLength() => IndexOf(int.MaxValue);

    /// <summary>Where a column's character starts in <see cref="Text"/> of the whole row: a wide character's second cell
    /// its first's place, past the text's end the text's length.</summary>
    public int IndexOf(int column)
    {
        Span<char> scratch = stackalloc char[2];
        int last = Cells.Length - 1;
        while (last >= 0 && Cells[last] is { Rune: 0 or ' ', Joined: 0 })
        {
            last--;
        }
        int to = column;
        while (to > 0 && to < Cells.Length && Cells[to].Width == 0)
        {
            to--;
        }
        int index = 0;
        for (int i = 0; i <= last && i < to; i++)
        {
            if (Cells[i].Width != 0)
            {
                index += TextOf(i, scratch).Length;
            }
        }
        return index;
    }

    /// <summary>The column a character of <see cref="Text"/> of the whole row starts in; past the text's end, the columns
    /// after it, one a character.</summary>
    public int ColumnOf(int index)
    {
        Span<char> scratch = stackalloc char[2];
        int at = 0;
        int column = 0;
        for (; column < Cells.Length; column++)
        {
            if (Cells[column].Width == 0)
            {
                continue;
            }
            int length = TextOf(column, scratch).Length;
            if (index < at + length)
            {
                return column;
            }
            at += length;
        }
        return column + (index - at);
    }

    /// <summary>How many cells the row's text takes, as a reflow keeps it: to the last cell written, but for spaces
    /// drawn as the background (zsh pads every prompt's line with them) and cells only erased.</summary>
    public int Length
    {
        get
        {
            int last = Cells.Length - 1;
            while (last >= 0 && Cells[last] is { Joined: 0 } cell
                && (cell.Rune == 0 || (cell.Rune == ' ' && cell.Style.Back == CellStyle.Default && !cell.Style.Flags.HasFlag(CellFlags.Inverse))))
            {
                last--;
            }
            // A wide character ends where its second half does.
            return last >= 0 && Cells[last].Width == 2 ? Math.Min(last + 2, Cells.Length) : last + 1;
        }
    }

    /// <summary>What the cell at a column shows, as <see cref="TextOf(int)"/> gives it but with no string made: its
    /// grapheme's own characters, or its code point written into <paramref name="scratch"/>, which takes two.</summary>
    internal ReadOnlySpan<char> TextOf(int column, Span<char> scratch)
    {
        Cell cell = Cells[column];
        if (cell.Joined != 0 && graphemes is not null)
        {
            return graphemes[cell.Joined - 1].Text;
        }
        if (cell.Rune == 0)
        {
            scratch[0] = ' ';
            return scratch[..1];
        }
        return scratch[..new System.Text.Rune(cell.Rune).EncodeToUtf16(scratch)];
    }

    private string TextOf(Cell cell) =>
        cell.Joined != 0 && graphemes is not null ? graphemes[cell.Joined - 1].Text : cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune);

    /// <summary>Keeps only the extras cells still show: one written over stays in the list until it fills.</summary>
    private void Compact()
    {
        List<CellExtra> kept = [];
        Dictionary<int, ushort> moved = [];
        for (int i = 0; i < Cells.Length; i++)
        {
            int joined = Cells[i].Joined;
            if (joined == 0)
            {
                continue;
            }
            if (!moved.TryGetValue(joined, out ushort now))
            {
                kept.Add(graphemes![joined - 1]);
                now = (ushort)kept.Count;
                moved[joined] = now;
            }
            Cells[i].Joined = now;
        }
        graphemes = kept;
    }
}
