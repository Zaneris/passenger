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
/// combining acute, a flag's two letters) is kept by its line (<see cref="TerminalLine.TextOf"/>), so a cell holds no
/// reference for the collector to track through the millions a flood of output writes.</summary>
public struct Cell
{
    /// <summary>The grapheme's first code point; 0 for a blank.</summary>
    public int Rune;

    private int fore;
    private int back;
    private CellFlags flags;

    public byte Width;

    /// <summary>Which of its line's graphemes the cell shows, from 1; 0 when <see cref="Rune"/> is all of it.</summary>
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

    /// <summary>Whether the cell's grapheme is more than its first code point.</summary>
    public readonly bool IsJoined => Joined != 0;
}

/// <summary>A row of cells, and whether the text went on to the next row by wrapping (so a copy joins them).</summary>
public sealed class TerminalLine
{
    private List<string>? graphemes;

    public TerminalLine(int columns, CellStyle style)
    {
        Cells = new Cell[columns];
        Clear(0, columns, style);
    }

    internal TerminalLine(Cell[] cells, bool wrapped)
    {
        Cells = cells;
        Wrapped = wrapped;
    }

    public Cell[] Cells { get; private set; }

    public bool Wrapped { get; set; }

    /// <summary>The graphemes the line's joined cells show, by their <see cref="Cell.Joined"/> less one.</summary>
    internal List<string>? Graphemes => graphemes;

    public void Clear(int from, int to, CellStyle style)
    {
        int start = Math.Max(0, from);
        int end = Math.Min(to, Cells.Length);
        if (start < end)
        {
            Cells.AsSpan(start, end - start).Fill(Cell.Blank(style));
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
    }

    /// <summary>What the cell at a column shows: its whole grapheme, a space for a blank.</summary>
    public string TextOf(int column) => TextOf(Cells[column]);

    /// <summary>Makes a cell show a grapheme of more than one code point.</summary>
    internal void SetGrapheme(int column, string grapheme)
    {
        if (graphemes is null)
        {
            graphemes = [];
        }
        else if (graphemes.Count >= Math.Min(ushort.MaxValue, (Cells.Length * 2) + 16))
        {
            Compact();
        }
        graphemes.Add(grapheme);
        Cells[column].Joined = (ushort)graphemes.Count;
    }

    /// <summary>The row's text, trailing blanks and spaces left out.</summary>
    public string Text(int from = 0, int to = int.MaxValue)
    {
        System.Text.StringBuilder text = new();
        int end = Math.Min(to, Cells.Length);
        int last = end - 1;
        while (last >= from && Cells[last] is { Rune: 0 or ' ', Joined: 0 })
        {
            last--;
        }
        for (int i = Math.Max(0, from); i <= last; i++)
        {
            Cell cell = Cells[i];
            if (cell.Width == 0)
            {
                continue;
            }
            text.Append(TextOf(cell));
        }
        return text.ToString();
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

    private string TextOf(Cell cell) =>
        cell.Joined != 0 && graphemes is not null ? graphemes[cell.Joined - 1] : cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune);

    /// <summary>Keeps only the graphemes cells still show: one written over stays in the list until it fills.</summary>
    private void Compact()
    {
        List<string> kept = [];
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
