using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Passenger.Terminal;

/// <summary>Rows as the view draws them: each line's runs shaped once and kept with where they go from the row's top,
/// for as long as the line shows the cells they were shaped from (unwritten, or written again alike), the columns chosen
/// and linked on it are the same and the look stands, so a frame shapes only the lines whose cells changed since it last
/// drew them and draws the rest as kept. A log scrolling up the screen shapes the line it brings and no other. A line a
/// frame did not draw lets its runs go.</summary>
public sealed partial class TerminalView
{
    private readonly Drawings drawings = new();
    private readonly Dictionary<IBrush, Pen> pens = new(ReferenceEqualityComparer.Instance);

    /// <summary>The columns links cover on the row being drawn, from the frame's links.</summary>
    private readonly List<(int From, int To)> rowLinks = [];

    /// <summary>The links drawn as links in this frame, and those found around one line.</summary>
    private readonly List<TerminalLink> shownLinks = [];
    private readonly List<TerminalLink> foundLinks = [];

    /// <summary>Links found before, given again while they are where they were rather than made anew.</summary>
    private readonly List<TerminalLink> knownLinks = [];

    /// <summary>A line's text as its links are looked for in it, and the row (from the line's first) and column of
    /// the cell each of its characters came from.</summary>
    private char[] linkText = new char[512];
    private int[] linkRows = new int[512];
    private int[] linkColumns = new int[512];

    private CursorText? cursorText;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (session is null)
        {
            return;
        }
        TerminalScreen screen = session.Screen;
        Typeface plain = new(FontFamily);
        long first = TopLine;
        ((long Line, int Column) Start, (long Line, int Column) End)? range = Ordered();
        FindShownLinks(screen, first);
        for (int row = 0; row < screen.Rows; row++)
        {
            long line = first + row;
            if (line >= screen.ScreenTop + screen.Rows)
            {
                break;
            }
            double y = Pad + (row * cellHeight);
            DrawLine(context, screen.LineAt(line), line, y, plain, range, screen.Columns);
            DrawHiddenLink(context, line, y, screen.Columns);
        }
        drawings.EndFrame();
        if (first == screen.ScreenTop && screen.CursorVisible)
        {
            DrawCursor(context, screen, plain);
        }
    }

    /// <summary>Lets go of every row's runs, for a new look or font.</summary>
    private void ForgetDrawings()
    {
        drawings.Clear();
        cursorText?.Text?.Dispose();
        cursorText = null;
    }

    private void DrawLine(DrawingContext context, TerminalLine line, long index, double y, Typeface plain,
        ((long Line, int Column) Start, (long Line, int Column) End)? range, int columns)
    {
        (int chosenFrom, int chosenTo) = Chosen(range, index, line.Cells.Length);
        LinkedOn(index, columns);
        Drawn drawn = drawings.Of(line);
        if (!drawn.Shows(line, chosenFrom, chosenTo, rowLinks))
        {
            drawn.Reset(line, chosenFrom, chosenTo, rowLinks);
            Shape(drawn, line, plain);
        }
        foreach (Part part in drawn.Parts)
        {
            if (part.Fill is { } fill)
            {
                context.FillRectangle(fill, new Rect(part.Area.X, y, part.Area.Width, part.Area.Height));
            }
            if (part.Text is null)
            {
                continue;
            }
            if (part.Dim)
            {
                using (context.PushOpacity(0.6))
                {
                    DrawInk(context, part, y);
                }
            }
            else
            {
                DrawInk(context, part, y);
            }
        }
    }

    /// <summary>A run's text and the lines over it.</summary>
    private void DrawInk(DrawingContext context, Part part, double y)
    {
        Point at = new(part.At.X, y + part.At.Y);
        if (part.Fitted)
        {
            using (context.PushClip(new Rect(part.Area.X, y, part.Area.Width, part.Area.Height)))
            {
                part.Text!.Draw(context, at);
            }
        }
        else
        {
            part.Text!.Draw(context, at);
        }
        if (part.Under is { } under)
        {
            double line = y + cellHeight - part.UnderBy;
            context.DrawLine(under, new Point(part.Area.X, line), new Point(part.Area.Right, line));
        }
        if (part.Strike is { } strike)
        {
            context.DrawLine(strike, new Point(part.Area.X, y + (cellHeight / 2)), new Point(part.Area.Right, y + (cellHeight / 2)));
        }
    }

    /// <summary>The columns of a line a selection covers, from and to; from past to when it covers none.</summary>
    private static (int From, int To) Chosen(((long Line, int Column) Start, (long Line, int Column) End)? range, long line, int length)
    {
        if (range is not { } r || line < r.Start.Line || line > r.End.Line)
        {
            return (0, -1);
        }
        int from = Math.Max(0, line == r.Start.Line ? r.Start.Column : 0);
        int to = Math.Min(length - 1, line == r.End.Line ? r.End.Column : int.MaxValue);
        return from <= to ? (from, to) : (0, -1);
    }

    /// <summary>A line cut into runs, each shaped: cells drawn alike in the terminal's font, every glyph of which is a
    /// column wide so it stays on the grid; a wide character, a letter with its marks or one another font draws alone,
    /// fitted to its cells.</summary>
    private void Shape(Drawn drawn, TerminalLine line, Typeface plain)
    {
        Cell[] cells = line.Cells;
        int column = 0;
        while (column < cells.Length)
        {
            Cell head = cells[column];
            bool chosen = drawn.IsChosen(column);
            bool isLink = Linked(column);
            int end = column + 1;
            bool alone = !InRun(head);
            if (!alone)
            {
                while (end < cells.Length && InRun(cells[end]) && cells[end].Style == head.Style && drawn.IsChosen(end) == chosen && Linked(end) == isLink)
                {
                    end++;
                }
            }
            else
            {
                if (head.Width == 2 && end < cells.Length)
                {
                    end++;
                }
                // A spacing mark (a Devanagari vowel sign) has a column of its own, but is shaped with the letter before it.
                while (end < cells.Length && cells[end].Width == 1 && CharUnicodeInfo.GetUnicodeCategory(cells[end].Rune) == UnicodeCategory.SpacingCombiningMark
                    && cells[end].Style == head.Style && drawn.IsChosen(end) == chosen)
                {
                    end++;
                }
            }
            ShapeRun(drawn, line, column, end, plain, chosen, alone, isLink);
            column = end;
        }
    }

    private bool Linked(int column)
    {
        foreach ((int from, int to) in rowLinks)
        {
            if (from <= column && column <= to)
            {
                return true;
            }
        }
        return false;
    }

    private void ShapeRun(Drawn drawn, TerminalLine line, int from, int to, Typeface plain, bool chosen, bool alone, bool isLink)
    {
        CellStyle style = line.Cells[from].Style;
        bool inverse = style.Flags.HasFlag(CellFlags.Inverse);
        IBrush fore = isLink && !inverse ? link : Colour(style.Fore, foreground);
        IBrush back = Colour(style.Back, background);
        if (inverse)
        {
            (fore, back) = (back, fore);
        }
        double x = Pad + (from * cellWidth);
        Rect area = new(x, 0, (to - from) * cellWidth, cellHeight);
        IBrush? fill = chosen ? selection : style.Back != CellStyle.Default || inverse ? back : null;
        ReadOnlyMemory<char> text = drawn.Write(line, from, to);
        if (style.Flags.HasFlag(CellFlags.Hidden) || text.Span.Trim().IsEmpty)
        {
            if (fill is not null)
            {
                drawn.Parts.Add(new Part(area, fill));
            }
            return;
        }
        Typeface face = new(plain.FontFamily, style.Flags.HasFlag(CellFlags.Italic) ? FontStyle.Italic : FontStyle.Normal,
            style.Flags.HasFlag(CellFlags.Bold) ? FontWeight.Bold : FontWeight.Normal);
        (TextLine? shaped, Point at) = alone ? Fit(text, area, face, fore) : Placed(Shape(text, face, FontSize, fore), x);
        Pen? under = isLink ? PenOf(linkUnderline) : style.Flags.HasFlag(CellFlags.Underline) ? PenOf(fore) : null;
        drawn.Parts.Add(new Part(area, fill)
        {
            Text = shaped,
            At = at,
            Fitted = alone,
            Dim = style.Flags.HasFlag(CellFlags.Dim),
            Under = under,
            UnderBy = isLink ? 2.5 : 2,
            Strike = style.Flags.HasFlag(CellFlags.Strike) ? PenOf(fore) : null,
        });
    }

    /// <summary>A run shaped at its column, centred in the row's height.</summary>
    private (TextLine? Text, Point At) Placed(TextLine? shaped, double x) => (shaped, new Point(x, shaped is null ? 0 : (cellHeight - shaped.Height) / 2));

    /// <summary>A grapheme shaped to be drawn within its cells: another font's glyph (an emoji's, a Devanagari letter's)
    /// made smaller when it is wider than they are, so it never covers the next, and centred in them.</summary>
    private (TextLine? Text, Point At) Fit(ReadOnlyMemory<char> text, Rect cells, Typeface face, IBrush brush)
    {
        TextLine? shaped = Shape(text, face, FontSize, brush);
        if (shaped is null)
        {
            return (null, default);
        }
        double width = shaped.WidthIncludingTrailingWhitespace;
        if (width > cells.Width + 0.5 || shaped.Height > cells.Height * 1.4)
        {
            double scale = Math.Min(cells.Width / width, cells.Height * 1.25 / shaped.Height);
            shaped.Dispose();
            shaped = Shape(text, face, FontSize * scale, brush);
            if (shaped is null)
            {
                return (null, default);
            }
            width = shaped.WidthIncludingTrailingWhitespace;
        }
        return (shaped, new Point(cells.X + Math.Max(0, (cells.Width - width) / 2), cells.Y + ((cells.Height - shaped.Height) / 2)));
    }

    private static TextLine? Shape(ReadOnlyMemory<char> text, Typeface face, double size, IBrush brush)
    {
        GenericTextRunProperties run = new(face, size, foregroundBrush: brush, cultureInfo: CultureInfo.InvariantCulture);
        return TextFormatter.Current.FormatLine(new RunText(text, run), 0, double.PositiveInfinity, new RowParagraph(run));
    }

    private Pen PenOf(IBrush brush)
    {
        if (!pens.TryGetValue(brush, out Pen? pen))
        {
            pens[brush] = pen = new Pen(brush, 1);
        }
        return pen;
    }

    /// <summary>A hidden link under the pointer with no key held: underlined faintly in the text's colour, so a file
    /// path shows that a Cmd+click opens it.</summary>
    private void DrawHiddenLink(DrawingContext context, long line, double y, int columns)
    {
        if (HoveredLink is not { Hidden: true } hovered || held.HasFlag(LinkKey) || line < hovered.FromLine || line > hovered.ToLine)
        {
            return;
        }
        int from = line == hovered.FromLine ? hovered.FromColumn : 0;
        int to = line == hovered.ToLine ? hovered.ToColumn : columns - 1;
        double under = y + cellHeight - 2.5;
        using (context.PushOpacity(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? 0.4 : 0.5))
        {
            context.DrawLine(PenOf(foreground), new Point(Pad + (from * cellWidth), under), new Point(Pad + ((to + 1) * cellWidth), under));
        }
    }

    private void DrawCursor(DrawingContext context, TerminalScreen screen, Typeface plain)
    {
        int column = Math.Min(screen.CursorColumn, screen.Columns - 1);
        TerminalLine line = screen.Lines[screen.CursorRow];
        Cell cell = line.Cells[column];
        double x = Pad + (column * cellWidth);
        double y = Pad + (screen.CursorRow * cellHeight);
        double width = cellWidth * Math.Max(1, (int)cell.Width);
        if (!IsFocused)
        {
            context.DrawRectangle(null, PenOf(cursor), new Rect(x + 0.5, y + 0.5, width - 1, cellHeight - 1));
            return;
        }
        switch (screen.CursorShape)
        {
            case CursorShape.Bar:
                context.FillRectangle(cursor, new Rect(x, y, 2, cellHeight));
                break;
            case CursorShape.Underline:
                context.FillRectangle(cursor, new Rect(x, y + cellHeight - 2, width, 2));
                break;
            default:
                context.FillRectangle(cursor, new Rect(x, y, width, cellHeight));
                if (cell.Rune != 0)
                {
                    CursorText shown = CursorTextOf(line, column, new Rect(x, 0, width, cellHeight), plain);
                    if (shown.Text is { } text)
                    {
                        using (context.PushClip(new Rect(x, y, width, cellHeight)))
                        {
                            text.Draw(context, new Point(shown.At.X, y + shown.At.Y));
                        }
                    }
                }
                break;
        }
    }

    /// <summary>The character under the block cursor shaped in the background's colour: the one kept while its cell is.</summary>
    private CursorText CursorTextOf(TerminalLine line, int column, Rect cells, Typeface plain)
    {
        if (cursorText is { } kept && ReferenceEquals(kept.Line, line) && kept.Writes == line.Writes && kept.Column == column && kept.Width == cells.Width)
        {
            return kept;
        }
        cursorText?.Text?.Dispose();
        (TextLine? text, Point at) = Fit(line.TextOf(column).AsMemory(), cells, plain, background);
        return cursorText = new CursorText(line, line.Writes, column, cells.Width, text, at);
    }

    /// <summary>The links drawn as links on the rows shown, into <see cref="shownLinks"/>: every one not hidden, and the
    /// hidden one under the pointer while Cmd or Ctrl is held.</summary>
    private void FindShownLinks(TerminalScreen screen, long first)
    {
        shownLinks.Clear();
        if (Links is null)
        {
            return;
        }
        long last = Math.Min(first + screen.Rows, screen.ScreenTop + screen.Rows) - 1;
        for (long line = first; line <= last; line++)
        {
            LinksAround(screen, line, out long end);
            foreach (TerminalLink found in foundLinks)
            {
                if (!found.Hidden || (found == HoveredLink && held.HasFlag(LinkKey)))
                {
                    shownLinks.Add(found);
                }
            }
            line = end;
        }
    }

    /// <summary>The columns of a row that the frame's links cover, into <see cref="rowLinks"/>.</summary>
    private void LinkedOn(long line, int columns)
    {
        rowLinks.Clear();
        foreach (TerminalLink each in shownLinks)
        {
            if (line >= each.FromLine && line <= each.ToLine)
            {
                rowLinks.Add((line == each.FromLine ? each.FromColumn : 0, line == each.ToLine ? each.ToColumn : columns - 1));
            }
        }
    }

    /// <summary>The links on the line a row is part of, into <see cref="foundLinks"/>: its text is read with the rows it
    /// wrapped from and onto (sixteen at most each way), so a long address wrapped across them is one link.
    /// <paramref name="end"/> is that line's last row.</summary>
    private void LinksAround(TerminalScreen screen, long line, out long end)
    {
        const int Rows = 16;
        foundLinks.Clear();
        long last = screen.ScreenTop + screen.Rows - 1;
        long first = line;
        while (first > screen.FirstLine && line - first < Rows && screen.LineAt(first - 1).Wrapped)
        {
            first--;
        }
        end = line;
        while (end < last && end - line < Rows && screen.LineAt(end).Wrapped)
        {
            end++;
        }
        Span<char> scratch = stackalloc char[2];
        int length = 0;
        for (long each = first; each <= end; each++)
        {
            TerminalLine shown = screen.LineAt(each);
            int count = each == end ? shown.Length : shown.Cells.Length;
            for (int c = 0; c < count; c++)
            {
                if (shown.Cells[c].Width == 0)
                {
                    continue;
                }
                ReadOnlySpan<char> text = shown.TextOf(c, scratch);
                if (length + text.Length > linkText.Length)
                {
                    int grown = Math.Max(linkText.Length * 2, length + text.Length);
                    Array.Resize(ref linkText, grown);
                    Array.Resize(ref linkRows, grown);
                    Array.Resize(ref linkColumns, grown);
                }
                text.CopyTo(linkText.AsSpan(length));
                linkRows.AsSpan(length, text.Length).Fill((int)(each - first));
                linkColumns.AsSpan(length, text.Length).Fill(c);
                length += text.Length;
            }
        }
        if (length == 0)
        {
            return;
        }
        IReadOnlyList<(int Start, int Length, object Target, bool Hidden)> found = Links!(linkText.AsSpan(0, length));
        for (int i = 0; i < found.Count; i++)
        {
            (int start, int count, object target, bool hidden) = found[i];
            if (count > 0 && start + count <= length)
            {
                int stop = start + count - 1;
                long toLine = first + linkRows[stop];
                Cell[] cells = screen.LineAt(toLine).Cells;
                int toColumn = Math.Min(linkColumns[stop] + cells[linkColumns[stop]].Width - 1, cells.Length - 1);
                foundLinks.Add(Known(first + linkRows[start], linkColumns[start], toLine, toColumn, target, hidden));
            }
        }
    }

    /// <summary>A link found where one was before: that one, so finding it again in every frame makes nothing.</summary>
    private TerminalLink Known(long fromLine, int fromColumn, long toLine, int toColumn, object target, bool hidden)
    {
        foreach (TerminalLink known in knownLinks)
        {
            if (known.FromLine == fromLine && known.FromColumn == fromColumn && known.ToLine == toLine && known.ToColumn == toColumn
                && known.Hidden == hidden && Equals(known.Target, target))
            {
                return known;
            }
        }
        if (knownLinks.Count >= 256)
        {
            knownLinks.Clear();
        }
        TerminalLink made = new(fromLine, fromColumn, toLine, toColumn, target, hidden);
        knownLinks.Add(made);
        return made;
    }

    /// <summary>A run of a row: its background, and its text shaped (null for blank or hidden text) with where it goes
    /// from the row's top and the lines drawn over it.</summary>
    private readonly record struct Part(Rect Area, IBrush? Fill)
    {
        public TextLine? Text { get; init; }
        public Point At { get; init; }

        /// <summary>Whether the text is a grapheme fitted to its cells, clipped to them.</summary>
        public bool Fitted { get; init; }

        public bool Dim { get; init; }
        public Pen? Under { get; init; }

        /// <summary>How far above the row's foot the underline goes.</summary>
        public double UnderBy { get; init; }

        public Pen? Strike { get; init; }
    }

    private sealed record CursorText(TerminalLine Line, long Writes, int Column, double Width, TextLine? Text, Point At);

    /// <summary>A line's runs as the view shaped them, and what they were shaped from: the line's writes and cells, the
    /// columns chosen on it and those links covered.</summary>
    private sealed class Drawn
    {
        private (int From, int To)[] links = [];
        private char[] text = new char[256];
        private int length;
        private long writes = -1;
        private Cell[] cells = [];
        private int count = -1;
        private int chosenFrom;
        private int chosenTo = -1;

        public List<Part> Parts { get; } = [];

        public bool IsChosen(int column) => chosenFrom <= column && column <= chosenTo;

        /// <summary>Whether the runs show the line as it is, chosen and linked so: unwritten since, or written with what
        /// its cells showed (a program drawing its screen again).</summary>
        public bool Shows(TerminalLine line, int from, int to, List<(int From, int To)> linked)
        {
            if (from != chosenFrom || to != chosenTo || linked.Count != links.Length)
            {
                return false;
            }
            for (int i = 0; i < links.Length; i++)
            {
                if (links[i] != linked[i])
                {
                    return false;
                }
            }
            if (line.Writes == writes)
            {
                return true;
            }
            Cell[] now = line.Cells;
            if (now.Length != count)
            {
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (!now[i].Shows(cells[i]))
                {
                    return false;
                }
            }
            writes = line.Writes;
            return true;
        }

        /// <summary>Lets the runs go, to be shaped again for the line as it is now.</summary>
        public void Reset(TerminalLine line, int from, int to, List<(int From, int To)> linked)
        {
            Forget();
            writes = line.Writes;
            if (cells.Length < line.Cells.Length)
            {
                cells = new Cell[line.Cells.Length];
            }
            line.Cells.CopyTo(cells, 0);
            count = line.Cells.Length;
            chosenFrom = from;
            chosenTo = to;
            links = linked.Count == 0 ? [] : [.. linked];
        }

        public void Forget()
        {
            foreach (Part part in Parts)
            {
                part.Text?.Dispose();
            }
            Parts.Clear();
            length = 0;
            writes = -1;
            count = -1;
        }

        /// <summary>The text of a run's cells, written after the runs' before it: what its shaped text reads.</summary>
        public ReadOnlyMemory<char> Write(TerminalLine line, int from, int to)
        {
            Span<char> scratch = stackalloc char[2];
            int start = length;
            for (int i = from; i < to; i++)
            {
                if (line.Cells[i].Width == 0)
                {
                    continue;
                }
                ReadOnlySpan<char> shown = line.TextOf(i, scratch);
                if (length + shown.Length > text.Length)
                {
                    // Runs shaped before keep the array they read.
                    char[] grown = new char[Math.Max(text.Length * 2, length + shown.Length)];
                    text.AsSpan(start, length - start).CopyTo(grown.AsSpan(start));
                    text = grown;
                }
                shown.CopyTo(text.AsSpan(length));
                length += shown.Length;
            }
            return text.AsMemory(start, length - start);
        }
    }

    /// <summary>The rows drawn in the last frame and in this one, by their lines: a frame keeps those it draws again and
    /// lets the rest go, keeping their room for lines to come.</summary>
    private sealed class Drawings
    {
        private readonly Stack<Drawn> spare = new();
        private Dictionary<TerminalLine, Drawn> previous = [];
        private Dictionary<TerminalLine, Drawn> current = [];

        public Drawn Of(TerminalLine line)
        {
            if (current.TryGetValue(line, out Drawn? drawn))
            {
                return drawn;
            }
            if (!previous.Remove(line, out drawn))
            {
                drawn = spare.Count > 0 ? spare.Pop() : new Drawn();
            }
            current[line] = drawn;
            return drawn;
        }

        public void EndFrame()
        {
            foreach (Drawn stale in previous.Values)
            {
                stale.Forget();
                spare.Push(stale);
            }
            previous.Clear();
            (previous, current) = (current, previous);
        }

        public void Clear()
        {
            EndFrame();
            EndFrame();
        }
    }

    /// <summary>One run's text in one style.</summary>
    private sealed class RunText(ReadOnlyMemory<char> text, TextRunProperties run) : ITextSource
    {
        public TextRun GetTextRun(int textSourceIndex) =>
            textSourceIndex >= text.Length ? new TextEndOfParagraph() : new TextCharacters(text[textSourceIndex..], run);
    }

    /// <summary>A row's run: one line, left to right, unwrapped.</summary>
    private sealed class RowParagraph(TextRunProperties run) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => run;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override double Indent => 0;
    }
}
