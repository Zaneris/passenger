using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Soft wrap, as IntelliJ's: a line wider than the view goes on in rows below, broken after a space (or a comma or
/// semicolon, else before a dot or a bracket, else at the edge), its later rows from its own indent. <see cref="Rows"/> maps
/// lines to rows and back with the folds; a line's wraps come from its graphemes' widths, and after an edit only the lines
/// it touched are wrapped again. A caret where a row wraps stands at the next row's start, or at the end of the row before
/// (after End, a step right onto it, a click past the row's end, typing), as IntelliJ's does.</summary>
public sealed partial class CodeEditor
{
    private bool softWrap;
    private bool wrapIndent = true;
    private WrapMap? wraps;
    private RowMap? rowMap;

    /// <summary>What the wraps were made with besides the text and width (the font, the tab, the hints shown): a change makes
    /// them again.</summary>
    private int look;

    /// <summary>The offsets of carets standing at the end of the row before a wrap there rather than at the start of the
    /// next; any other move or edit of the carets clears them.</summary>
    private HashSet<int> wrapLeaning = [];

    /// <summary>What the next <see cref="SetSelections"/> leaves in <see cref="wrapLeaning"/>.</summary>
    private HashSet<int>? nextWrapLeaning;

    /// <summary>How many characters the editor has read to work out where lines wrap: what a key costs to wrap, for a test to
    /// count rather than time.</summary>
    public long CharactersWrapped { get; private set; }

    /// <summary>Whether lines wider than the view wrap onto rows below (Rider's View, Active Editor, Soft-Wrap). On, nothing
    /// scrolls sideways; the line at the top of the view stays there as it turns on or off.</summary>
    public bool SoftWrap
    {
        get => softWrap;
        set
        {
            if (softWrap == value)
            {
                return;
            }
            KeepTopLine(() =>
            {
                softWrap = value;
                wraps = null;
            });
            scroll = new Vector(0, scroll.Y);
            nextWrapLeaning = null;
            wrapLeaning = [];
            InvalidateArrange();
        }
    }

    /// <summary>Whether a wrapped line's rows after its first start where its text does (Rider's "Use the original line's
    /// indent for wrapped fragments"), or at the left edge.</summary>
    public bool WrapIndent
    {
        get => wrapIndent;
        set
        {
            if (wrapIndent != value)
            {
                wrapIndent = value;
                Rewrap();
            }
        }
    }

    /// <summary>The rows of the text as it stands: its lines less the folded ones, a wrapped line counted for each row.</summary>
    private RowMap Rows
    {
        get
        {
            FoldLines folds = Folding;
            WrapMap? wrapped = Wraps;
            if (rowMap is not { } map || !ReferenceEquals(map.Folds, folds) || !ReferenceEquals(map.Wraps, wrapped))
            {
                rowMap = map = new RowMap(folds, wrapped);
            }
            return map;
        }
    }

    /// <summary>Where the text's lines wrap at the view's width now, or null while soft wrap is off or the view has no width
    /// yet.</summary>
    private WrapMap? Wraps
    {
        get
        {
            if (!softWrap)
            {
                return null;
            }
            double width = WrapWidth;
            if (width <= 0)
            {
                return null;
            }
            TextSnapshot text = document.Snapshot;
            if (wraps is not { } map || !ReferenceEquals(map.Text, text) || map.Width != width || map.Look != look)
            {
                wraps = map = WrapAll(text, width);
            }
            return map;
        }
    }

    /// <summary>The width a row's text may take: the view's, less room for the caret and the wrap's sign at a row's end.</summary>
    private double WrapWidth => Bounds.Width <= 0 ? 0
        : Math.Max(metrics.CharWidth, Bounds.Width - GutterWidth - TextPadLeft - ScrollRailWidth - (2 * metrics.CharWidth));

    /// <summary>How many rows the text shows.</summary>
    private int RowCount => Rows.Count;

    /// <summary>The first row a line shows on; a folded line is on its fold's first line's.</summary>
    private int RowOf(int line) => Rows.RowOf(line);

    /// <summary>A line's top in the content: its first row's.</summary>
    private double TopOf(int line) => RowOf(line) * metrics.LineHeight;

    /// <summary>The bottom of a line's last row in the content.</summary>
    private double BottomOf(int line) => (RowOf(line) + Rows.RowsOf(line)) * metrics.LineHeight;

    /// <summary>The row at a height in the control.</summary>
    private int RowAtY(double y) => (int)Math.Floor((y + scroll.Y) / metrics.LineHeight);

    /// <summary>The line at a height in the control (past the end, a line past the last).</summary>
    private int LineAtY(double y) => Rows.LineOfRow(RowAtY(y)).Line;

    private bool IsHidden(int line) => Folding.IsHidden(line);

    /// <summary>Whether a caret at an offset stands at the end of the row before a wrap there.</summary>
    private bool LeansBack(int offset) => wrapLeaning.Contains(offset);

    /// <summary>A row of a line: its text's span, from a row of the line (from zero) as the wraps now have it.</summary>
    private RowSpan RowSpanOf(TextSnapshot text, int line, int row) => RowSpan.Of(text.GetLine(line), Wraps?.Lines[line], row);

    /// <summary>The row of the text an offset stands on, and the span of it: after a wrap there, or at the end of the row
    /// before it when <paramref name="beforeWrap"/>. An offset in folded lines is on its fold's first row.</summary>
    private (RowSpan Span, int Row) RowOfOffset(TextSnapshot text, int offset, bool beforeWrap)
    {
        int line = text.GetLineFromOffset(offset);
        LineSpan span = text.GetLine(line);
        LineWrap? wrap = Wraps?.Lines[line];
        int sub = SubRowOf(wrap, Math.Min(offset, span.End) - span.Start, beforeWrap);
        return (RowSpan.Of(span, wrap, sub), RowOf(line) + (IsHidden(line) ? 0 : sub));
    }

    /// <summary>Which row of its line a column is on: the one a wrap at it starts, or with <paramref name="beforeWrap"/> the
    /// one it ends.</summary>
    private static int SubRowOf(LineWrap? wrap, int column, bool beforeWrap)
    {
        if (wrap is null)
        {
            return 0;
        }
        int row = UpperBound(wrap.Starts, column);
        return beforeWrap && row > 0 && wrap.Starts[row - 1] == column ? row - 1 : row;
    }

    /// <summary>Where a caret at an offset is drawn: its row, and its x from the text's left edge, on the side of the hints
    /// there it stands.</summary>
    private (int Row, double X) Place(TextSnapshot text, int offset, bool beforeWrap)
    {
        (RowSpan span, int row) = RowOfOffset(text, offset, beforeWrap);
        LineView view = ViewOf(text, span);
        int at = Math.Clamp(offset, span.Start, span.End);
        return (row, span.Indent + XWith(view, GapsOn(span), at - span.Start, StandsAfterHints(at)));
    }

    /// <summary>The offset nearest an x on a row; at a wrapped row's end, the next row's first offset, whose caret then
    /// stands before the wrap (<paramref name="beforeWrap"/>).</summary>
    private int OffsetAtRow(TextSnapshot text, int row, double x, out bool beforeWrap)
    {
        (int line, int sub) = Rows.LineOfRow(Math.Max(0, row));
        if (line >= text.LineCount)
        {
            (line, sub) = (text.LineCount - 1, int.MaxValue);
        }
        RowSpan span = RowSpanOf(text, line, sub);
        LineView view = ViewOf(text, span);
        int column = ColumnWith(view, GapsOn(span), x - span.Indent, span.Length);
        if (column > 0 && column < span.Length && char.IsLowSurrogate(text[span.Start + column]))
        {
            column--;
        }
        beforeWrap = !span.IsLast && column == span.Length;
        return span.Start + column;
    }

    /// <summary>The rows from <paramref name="firstRow"/> to <paramref name="lastRow"/>, in order, stepping over folded lines
    /// rather than visiting them.</summary>
    private List<RowSpan> RowsOnScreen(TextSnapshot text, int firstRow, int lastRow)
    {
        List<RowSpan> shown = [];
        RowMap map = Rows;
        FoldLines folds = map.Folds;
        (int line, int sub) = map.LineOfRow(firstRow);
        if (line >= text.LineCount || folds.IsHidden(line))
        {
            return shown;
        }
        int k = folds.RangeAtOrBefore(line) + 1;
        LineSpan span = text.GetLine(line);
        for (int row = firstRow; row <= lastRow; row++)
        {
            LineWrap? wrap = map.Wraps?.Lines[line];
            shown.Add(RowSpan.Of(span, wrap, sub));
            if (wrap is not null && sub < wrap.Starts.Length)
            {
                sub++;
                continue;
            }
            sub = 0;
            line++;
            if (k < folds.Hidden.Length && folds.Hidden[k].First == line)
            {
                line = folds.Hidden[k].Last + 1;
                k++;
            }
            if (line >= text.LineCount)
            {
                break;
            }
            span = text.GetLine(line);
        }
        return shown;
    }

    /// <summary>The carets that land where a row wraps, which stand after the text before them: where typing put them.</summary>
    private HashSet<int>? CaretsAtWraps(SelectionSet carets)
    {
        if (Wraps is not { } map)
        {
            return null;
        }
        TextSnapshot text = document.Snapshot;
        HashSet<int>? at = null;
        foreach (Selection s in carets.Items)
        {
            int caret = Math.Min(s.Caret, text.Length);
            int line = text.GetLineFromOffset(caret);
            if (map.Lines[line] is { } wrap && Array.BinarySearch(wrap.Starts, caret - text.GetLineStart(line)) >= 0)
            {
                (at ??= []).Add(caret);
            }
        }
        return at;
    }

    /// <summary>Home, as IntelliJ's on a wrapped line: on a row after the first, to the row's start, and from there to the row
    /// before's; on the first row, to the text and then to the line's start, as without wraps.</summary>
    private void MoveToRowStart(bool extend)
    {
        TextSnapshot text = document.Snapshot;
        if (Wraps is not { } map)
        {
            Move(s => TextNavigation.SmartHome(text, s.Caret), extend);
            return;
        }
        document.BreakUndoGroup();
        SetSelections(selections.With(s =>
        {
            int line = text.GetLineFromOffset(s.Caret);
            LineSpan span = text.GetLine(line);
            LineWrap? wrap = map.Lines[line];
            int sub = SubRowOf(wrap, s.Caret - span.Start, LeansBack(s.Caret));
            if (wrap is null || sub == 0)
            {
                return s.MoveTo(TextNavigation.SmartHome(text, s.Caret), extend);
            }
            int start = span.Start + wrap.Starts[sub - 1];
            if (s.Caret > start)
            {
                return s.MoveTo(start, extend);
            }
            return s.MoveTo(sub == 1 ? TextNavigation.IndentEnd(text, span) : span.Start + wrap.Starts[sub - 2], extend);
        }), reveal: true);
    }

    /// <summary>End, as IntelliJ's on a wrapped line: to the end of the caret's row, before the wrap, and from there to the
    /// end of the next row; on a line's last row, to the line's end.</summary>
    private void MoveToRowEnd(bool extend)
    {
        TextSnapshot text = document.Snapshot;
        if (Wraps is not { } map)
        {
            Move(s => TextNavigation.LineEnd(text, s.Caret), extend);
            return;
        }
        document.BreakUndoGroup();
        HashSet<int> lean = [];
        SelectionSet moved = selections.With(s =>
        {
            int line = text.GetLineFromOffset(s.Caret);
            LineSpan span = text.GetLine(line);
            LineWrap? wrap = map.Lines[line];
            bool before = LeansBack(s.Caret);
            int sub = SubRowOf(wrap, s.Caret - span.Start, before);
            if (wrap is null)
            {
                return s.MoveTo(span.End, extend);
            }
            if (sub < wrap.Starts.Length && s.Caret == span.Start + wrap.Starts[sub] && before)
            {
                // At the row's end already: on to the next row's.
                sub++;
            }
            if (sub >= wrap.Starts.Length)
            {
                return s.MoveTo(span.End, extend);
            }
            int end = span.Start + wrap.Starts[sub];
            lean.Add(end);
            return s.MoveTo(end, extend);
        });
        nextWrapLeaning = lean;
        SetSelections(moved, reveal: true);
    }

    /// <summary>Makes the wraps again for what they are made with (the font, the hints, the indent), the view's top line kept
    /// where it is.</summary>
    private void Rewrap()
    {
        look++;
        if (softWrap)
        {
            KeepTopLine(() => wraps = null);
        }
    }

    /// <summary>Changes what the rows are made of, keeping the offset at the view's top on the row at the top, as IntelliJ
    /// keeps the view when its soft wraps change.</summary>
    private void KeepTopLine(Action change)
    {
        if (Bounds.Height <= 0 || rowMap is not { } before || !ReferenceEquals(before.Folds.Text, document.Snapshot))
        {
            change();
            InvalidateVisual();
            return;
        }
        TextSnapshot text = document.Snapshot;
        int top = RowAtY(0);
        (int line, int sub) = before.LineOfRow(top);
        int anchor = line >= text.LineCount ? text.Length : RowSpan.Of(text.GetLine(line), before.Wraps?.Lines[line], sub).Start;
        double within = scroll.Y - (top * metrics.LineHeight);
        change();
        int row = RowOfOffset(text, anchor, beforeWrap: false).Row;
        double bottom = Math.Max(0, (RowCount - 1) * metrics.LineHeight);
        scroll = new Vector(scroll.X, Math.Clamp((row * metrics.LineHeight) + Math.Min(within, metrics.LineHeight - 1), 0, bottom));
        UpdateScrollBars();
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (softWrap && wraps is { } map && map.Width != WrapWidth)
        {
            // The text wraps at the new width; the line at the top stays at the top.
            KeepTopLine(() => wraps = null);
        }
    }

    /// <summary>The wraps carried through an edit: a line it did not touch keeps its rows, the ones it did are wrapped again.</summary>
    private void FollowWraps(DocumentChangedEventArgs change)
    {
        if (wraps is not { } map || !ReferenceEquals(map.Text, change.Before))
        {
            wraps = null;
            return;
        }
        TextSnapshot before = change.Before;
        TextSnapshot after = change.After;
        LineWrap?[] lines = new LineWrap?[after.LineCount];
        Advances measure = Measures();
        int copiedTo = 0;
        int writtenTo = 0;
        int shift = 0;
        int i = 0;
        IReadOnlyList<TextEdit> edits = change.Edits;
        while (i < edits.Count)
        {
            // The edits touching one run of lines, in the text before: the lines from the first one's to the last one's.
            int first = before.GetLineFromOffset(edits[i].Start);
            int last = before.GetLineFromOffset(edits[i].Start + edits[i].Length);
            int added = Breaks(edits[i].Text) - (last - first);
            int j = i + 1;
            while (j < edits.Count && before.GetLineFromOffset(edits[j].Start) <= last)
            {
                int end = before.GetLineFromOffset(edits[j].Start + edits[j].Length);
                added += Breaks(edits[j].Text) - (end - before.GetLineFromOffset(edits[j].Start));
                last = Math.Max(last, end);
                j++;
            }
            Array.Copy(map.Lines, copiedTo, lines, writtenTo, first - copiedTo);
            writtenTo += first - copiedTo;
            int lastAfter = last + shift + added;
            for (int line = first + shift; line <= lastAfter; line++)
            {
                lines[line] = WrapLine(after, after.GetLine(line), map.Width, measure);
            }
            writtenTo = lastAfter + 1;
            copiedTo = last + 1;
            shift += added;
            i = j;
        }
        Array.Copy(map.Lines, copiedTo, lines, writtenTo, map.Lines.Length - copiedTo);
        wraps = new WrapMap(after, map.Width, map.Look, lines);
    }

    /// <summary>The hints changed: the lines that had or have some are wrapped again, since a hint takes room on its row.</summary>
    private void FollowHints(IReadOnlyList<InlayHint> before, IReadOnlyList<InlayHint> after)
    {
        TextSnapshot text = document.Snapshot;
        if (wraps is not { } map || !ReferenceEquals(map.Text, text) || before.SequenceEqual(after))
        {
            return;
        }
        HashSet<int> touched = [];
        foreach (InlayHint hint in before.Concat(after))
        {
            touched.Add(text.GetLineFromOffset(Math.Clamp(hint.Offset, 0, text.Length)));
        }
        LineWrap?[] lines = (LineWrap?[])map.Lines.Clone();
        Advances measure = Measures();
        foreach (int line in touched)
        {
            lines[line] = WrapLine(text, text.GetLine(line), map.Width, measure);
        }
        wraps = new WrapMap(text, map.Width, map.Look, lines);
    }

    private static int Breaks(string text) => text.AsSpan().Count('\n');

    private WrapMap WrapAll(TextSnapshot text, double width)
    {
        LineWrap?[] lines = new LineWrap?[text.LineCount];
        Advances measure = Measures();
        for (int line = 0; line < lines.Length; line++)
        {
            lines[line] = WrapLine(text, text.GetLine(line), width, measure);
        }
        return new WrapMap(text, width, look, lines);
    }

    /// <summary>The graphemes' widths in the editor's font now, measured once each.</summary>
    private Advances Measures()
    {
        double tab = metrics.CharWidth * Indent.TabWidth;
        if (advances is null || advances.Tab != tab)
        {
            advances = new Advances(Measure, tab);
        }
        return advances;
    }

    /// <summary>Where one line wraps at a width, or null for a line that fits. Its graphemes' widths are summed with the hints'
    /// (a hint's room goes on the row its column starts); past the width, the row ends at the last space, comma or semicolon
    /// it holds, else before its last dot or bracket, else before the grapheme that would not fit. Spaces never wrap: they
    /// stay at the end of their row however far they reach, so a row starts with text.</summary>
    private LineWrap? WrapLine(TextSnapshot text, LineSpan line, double width, Advances measure)
    {
        int length = line.Length;
        if (length == 0)
        {
            return null;
        }
        CharactersWrapped += length;
        Gap[] hints = HintsOn(RowSpan.Whole(line));
        char[] buffer = ArrayPool<char>.Shared.Rent(length);
        try
        {
            Span<char> chars = buffer.AsSpan(0, length);
            text.CopyTo(line.Start, chars);
            double uniform = measure.Uniform;
            bool plain = hints.Length == 0 && !double.IsNaN(uniform) && !chars.ContainsAnyExceptInRange(' ', '~');
            if (plain && length * uniform <= width)
            {
                return null;
            }
            int firstText = 0;
            double indent = 0;
            while (firstText < length && chars[firstText] is ' ' or '\t')
            {
                indent += chars[firstText] == '\t' ? measure.Tab : measure.Of(chars.Slice(firstText, 1));
                firstText++;
            }
            if (firstText == length)
            {
                return null;
            }
            double drawnIndent = wrapIndent && indent <= width / 2 ? indent : 0;
            List<int>? starts = plain ? WrapPlain(chars, uniform, width, firstText, drawnIndent) : null;
            double x = 0;
            double rowX = 0;
            double room = width;
            int rowStart = 0;
            (int At, double X) good = (-1, 0);
            (int At, double X) fair = (-1, 0);
            int h = 0;
            int i = plain ? length : 0;
            while (i < length)
            {
                char c = chars[i];
                if (i > firstText && i > rowStart && !IsBlank(c))
                {
                    char previous = chars[i - 1];
                    if (IsBlank(previous) || previous is ',' or ';')
                    {
                        good = (i, x);
                    }
                    else if (c is '.' or '(' or '[' or '{')
                    {
                        fair = (i, x);
                    }
                }
                double before = x;
                while (h < hints.Length && hints[h].Column <= i)
                {
                    x += hints[h].Width;
                    h++;
                }
                int size = Grapheme(chars, i);
                double advance = c == '\t' ? measure.Tab : measure.Of(chars.Slice(i, size));
                if (x + advance - rowX > room && i > rowStart && !IsBlank(c))
                {
                    (int at, double atX) = good.At > rowStart ? good : fair.At > rowStart ? fair : (i, before);
                    (starts ??= []).Add(at);
                    rowStart = at;
                    rowX = atX;
                    room = width - drawnIndent;
                    // Read again from the new row's start, whose room may be less than the row before's.
                    i = at;
                    x = atX;
                    h = 0;
                    while (h < hints.Length && hints[h].Column < at)
                    {
                        h++;
                    }
                    continue;
                }
                x += advance;
                i += size;
            }
            return starts is null ? null : new LineWrap([.. starts], drawnIndent);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>The same wraps for a line of printable ASCII and no hints in a font that gives each the one width: a row's
    /// end found by counting, and its break by looking back from there, rather than by reading every character.</summary>
    private static List<int>? WrapPlain(ReadOnlySpan<char> chars, double uniform, double width, int firstText, double drawnIndent)
    {
        List<int>? starts = null;
        int rowStart = 0;
        double room = width;
        while (true)
        {
            // The first text that would not fit: spaces past the edge stay on the row.
            int over = rowStart + Math.Max(1, (int)Math.Floor(room / uniform));
            int text = over < chars.Length ? chars[over..].IndexOfAnyExcept(' ') : -1;
            if (text < 0)
            {
                return starts;
            }
            int at = BreakBefore(chars, over + text, Math.Max(rowStart, firstText));
            (starts ??= []).Add(at);
            rowStart = at;
            room = width - drawnIndent;
        }
    }

    /// <summary>Where a row of plain text that would not fit at <paramref name="over"/> ends: after its last space, comma or
    /// semicolon past <paramref name="after"/>, else before its last dot or bracket, else at <paramref name="over"/>.</summary>
    private static int BreakBefore(ReadOnlySpan<char> chars, int over, int after)
    {
        for (int j = over; j > after; j--)
        {
            if (chars[j] != ' ' && chars[j - 1] is ' ' or ',' or ';')
            {
                return j;
            }
        }
        for (int j = over; j > after; j--)
        {
            if (chars[j] is '.' or '(' or '[' or '{')
            {
                return j;
            }
        }
        return over;
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';

    /// <summary>How many of a rising array's values are at most <paramref name="value"/>.</summary>
    private static int UpperBound(int[] values, int value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (values[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>One row of a line as it is drawn: the whole line, or with soft wrap one of the rows it wraps into, whose text
    /// is drawn from <see cref="Indent"/> (the line's own indent, on a row after its first). Only the last row has the line's
    /// break.</summary>
    private readonly record struct RowSpan(int Line, int Start, int Length, int BreakLength, int Row, bool IsLast, double Indent)
    {
        public int End => Start + Length;

        public int EndIncludingBreak => Start + Length + BreakLength;

        public static RowSpan Whole(LineSpan line) => new(line.Line, line.Start, line.Length, line.BreakLength, 0, true, 0);

        /// <summary>A line's row (from zero; past its last, its last), as its wraps make it.</summary>
        public static RowSpan Of(LineSpan line, LineWrap? wrap, int row)
        {
            if (wrap is null)
            {
                return Whole(line);
            }
            row = Math.Clamp(row, 0, wrap.Starts.Length);
            int from = row == 0 ? 0 : wrap.Starts[row - 1];
            bool last = row == wrap.Starts.Length;
            int to = last ? line.Length : wrap.Starts[row];
            return new RowSpan(line.Line, line.Start + from, to - from, last ? line.BreakLength : 0, row, last, row == 0 ? 0 : wrap.Indent);
        }
    }

    /// <summary>A wrapped line: the columns its rows after the first start at, rising, and how far in their text is drawn.</summary>
    private sealed record LineWrap(int[] Starts, double Indent);

    /// <summary>Where every line of one text wraps at one width, null for a line that fits; replaced, never changed, so a row
    /// map made from it stays true to it.</summary>
    private sealed class WrapMap(TextSnapshot text, double width, int look, LineWrap?[] lines)
    {
        public TextSnapshot Text { get; } = text;
        public double Width { get; } = width;
        public int Look { get; } = look;
        public LineWrap?[] Lines { get; } = lines;
    }

    /// <summary>Lines and rows, both ways: the lines the folds leave showing, each one row or, wrapped, as many as its wraps
    /// make. The wrapped lines showing are kept in order with the rows before each, so a line's row or a row's line is found by
    /// halving; with nothing wrapped it is the folds' own map.</summary>
    private sealed class RowMap
    {
        private readonly int[] wrapped = [];
        private readonly int[] firstRows = [];
        private readonly int[] extraBefore = [0];

        public RowMap(FoldLines folds, WrapMap? wraps)
        {
            Folds = folds;
            Wraps = wraps;
            if (wraps is null)
            {
                return;
            }
            List<int> lines = [];
            for (int line = 0; line < wraps.Lines.Length; line++)
            {
                if (wraps.Lines[line] is not null && !folds.IsHidden(line))
                {
                    lines.Add(line);
                }
            }
            wrapped = [.. lines];
            firstRows = new int[wrapped.Length];
            extraBefore = new int[wrapped.Length + 1];
            for (int i = 0; i < wrapped.Length; i++)
            {
                firstRows[i] = folds.RowOf(wrapped[i]) + extraBefore[i];
                extraBefore[i + 1] = extraBefore[i] + wraps.Lines[wrapped[i]]!.Starts.Length;
            }
        }

        public FoldLines Folds { get; }

        public WrapMap? Wraps { get; }

        public int Count => Folds.Text.LineCount - Folds.HiddenCount + extraBefore[^1];

        /// <summary>A line's first row; a folded line's is its fold's first line's.</summary>
        public int RowOf(int line)
        {
            line = Folds.Shown(line);
            int i = LowerBound(wrapped, line);
            return Folds.RowOf(line) + extraBefore[i];
        }

        /// <summary>How many rows a line takes: one for a folded one, which shares its fold's.</summary>
        public int RowsOf(int line) =>
            Wraps is null || Folds.IsHidden(line) || line >= Wraps.Lines.Length ? 1 : 1 + (Wraps.Lines[line]?.Starts.Length ?? 0);

        /// <summary>The line a row shows and which of its rows it is; past the last row, the lines past the end.</summary>
        public (int Line, int Row) LineOfRow(int row)
        {
            int i = UpperBound(firstRows, row) - 1;
            if (i >= 0 && row - firstRows[i] <= Wraps!.Lines[wrapped[i]]!.Starts.Length)
            {
                return (wrapped[i], row - firstRows[i]);
            }
            return (Folds.LineOfRow(row - extraBefore[i + 1]), 0);
        }

        private static int LowerBound(int[] values, int value)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (values[middle] < value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }
    }
}
