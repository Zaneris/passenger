using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Soft wrap: a line wider than the view goes on in rows below, broken after a space (or a comma or semicolon,
/// else before a dot or a bracket, else at the edge), its later rows from its own indent. <see cref="Rows"/> maps lines
/// to rows and back with the folds; a line's wraps come from its graphemes' widths, and after an edit only the lines it
/// touched are wrapped again. A caret where a row wraps stands at the next row's start, or at the end of the row before
/// (after End, a step right onto it, a click past the row's end, typing).</summary>
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

    /// <summary>How many characters the editor has read to work out where lines wrap: what a key costs to wrap.</summary>
    public long CharactersWrapped { get; private set; }

    /// <summary>Whether lines wider than the view wrap onto rows below (Soft-Wrap). On, nothing scrolls sideways; the
    /// line at the top of the view stays there as it turns on or off.</summary>
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

    /// <summary>Whether a wrapped line's rows after its first start where its text does, or at the left edge.</summary>
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
        : Math.Max(metrics.CharWidth, Bounds.Width - GutterWidth - TextPadLeft - vertical.DesiredSize.Width - (2 * metrics.CharWidth));

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
    private RowSpan RowSpanOf(TextSnapshot text, int line, int row) => RowSpan.Of(text.GetLine(line), Wraps?[line], row);

    /// <summary>The row of the text an offset stands on, and the span of it: after a wrap there, or at the end of the row
    /// before it when <paramref name="beforeWrap"/>. An offset in folded lines is on its fold's first row.</summary>
    private (RowSpan Span, int Row) RowOfOffset(TextSnapshot text, int offset, bool beforeWrap)
    {
        int line = text.GetLineFromOffset(offset);
        LineSpan span = text.GetLine(line);
        LineWrap? wrap = Wraps?[line];
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
            LineWrap? wrap = map.Wraps?[line];
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
            if (map[line] is { } wrap && Array.BinarySearch(wrap.Starts, caret - text.GetLineStart(line)) >= 0)
            {
                (at ??= []).Add(caret);
            }
        }
        return at;
    }

    /// <summary>Home on a wrapped line: on a row after the first, to the row's start, and from there to the row
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
            LineWrap? wrap = map[line];
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

    /// <summary>End on a wrapped line: to the end of the caret's row, before the wrap, and from there to the end of the
    /// next row; on a line's last row, to the line's end.</summary>
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
            LineWrap? wrap = map[line];
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

    /// <summary>Changes what the rows are made of, keeping the offset at the view's top on the row at the top.</summary>
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
        int anchor = line >= text.LineCount ? text.Length : RowSpan.Of(text.GetLine(line), before.Wraps?[line], sub).Start;
        double within = scroll.Y - (top * metrics.LineHeight);
        change();
        int row = RowOfOffset(text, anchor, beforeWrap: false).Row;
        scroll = new Vector(scroll.X, Math.Clamp((row * metrics.LineHeight) + Math.Min(within, metrics.LineHeight - 1), 0, MaxScrollY));
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

    /// <summary>Ahead of everyone who hears of an edit (<see cref="TextDocument.ChangedFirst"/>): the decorations' hints and
    /// folds carried through it, then the wraps, so the lines it touched wrap with their hints where they now stand.</summary>
    private void OnChangedFirst(object? sender, DocumentChangedEventArgs change)
    {
        decorations.Follow(change);
        wrappedHints = decorations.InlayHints;
        FollowWraps(change);
    }

    /// <summary>The wraps carried through an edit: a line it did not touch keeps its rows, the ones it did are wrapped again.
    /// Ahead of everyone else who hears of the edit, since the host moving its folds through it asks for the rows, and what
    /// follows it asks where the carets are drawn.</summary>
    private void FollowWraps(DocumentChangedEventArgs change)
    {
        if (wraps is not { } map || !ReferenceEquals(map.Text, change.Before))
        {
            wraps = null;
            return;
        }
        TextSnapshot before = change.Before;
        TextSnapshot after = change.After;
        WrapBuilder made = new(map.Runs + 2);
        Advances measure = Measures();
        int copiedTo = 0;
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
            made.AddRange(map, copiedTo, first - copiedTo);
            int lastAfter = last + shift + added;
            for (int line = first + shift; line <= lastAfter; line++)
            {
                LineWrap? wrap = WrapLine(after, after.GetLine(line), map.Width, measure);
                made.Add(wrap);
                if (added == 0)
                {
                    Rewrapped(map[line - shift], wrap, line);
                }
            }
            if (added != 0)
            {
                rowShape++;
            }
            copiedTo = last + 1;
            shift += added;
            i = j;
        }
        made.AddRange(map, copiedTo, map.LineCount - copiedTo);
        wraps = made.Build(after, map.Width, map.Look);
    }

    /// <summary>The hints changed: the lines where one is drawn otherwise than before (where it stands, what it says) are
    /// wrapped again, since a hint takes room on its row; a host's hints carried through a key as the editor's were draw
    /// alike, and wrap nothing again.</summary>
    private void FollowHints(HintList before, HintList after)
    {
        TextSnapshot text = document.Snapshot;
        if (wraps is not { } map || !ReferenceEquals(map.Text, text) || HintList.Differing(before, after) is not { } moved)
        {
            return;
        }
        SortedSet<int> touched = [];
        foreach (int offset in moved)
        {
            touched.Add(text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length)));
        }
        WrapBuilder made = new(map.Runs + touched.Count + 1);
        Advances measure = Measures();
        int copiedTo = 0;
        foreach (int line in touched)
        {
            made.AddRange(map, copiedTo, line - copiedTo);
            LineWrap? wrap = WrapLine(text, text.GetLine(line), map.Width, measure);
            made.Add(wrap);
            Rewrapped(map[line], wrap, line);
            copiedTo = line + 1;
        }
        made.AddRange(map, copiedTo, map.LineCount - copiedTo);
        wraps = made.Build(text, map.Width, map.Look);
    }

    private static int Breaks(string text) => text.AsSpan().Count('\n');

    private WrapMap WrapAll(TextSnapshot text, double width)
    {
        rowShape++;
        WrapBuilder made = new();
        Advances measure = Measures();
        for (int line = 0; line < text.LineCount; line++)
        {
            made.Add(WrapLine(text, text.GetLine(line), width, measure));
        }
        return made.Build(text, width, look);
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

    /// <summary>Where every line of one text wraps at one width, null for a line that fits, in runs of lines that the maps of
    /// later texts share: an edit's map makes again the lines it touched and takes the runs around them as they were, so a
    /// key costs the lines it changed and a few small arrays, whatever the length of the file. Replaced, never changed, so a
    /// row map made from it stays true to it.</summary>
    private sealed class WrapMap
    {
        private readonly WrapRun[] runs;

        /// <summary>The line each run starts at.</summary>
        private readonly int[] firstLines;

        /// <summary>The rows the wraps of the runs before each add; one more than the runs, the last all of them.</summary>
        private readonly int[] extraBefore;

        public WrapMap(TextSnapshot text, double width, int look, WrapRun[] runs)
        {
            Text = text;
            Width = width;
            Look = look;
            this.runs = runs;
            firstLines = new int[runs.Length];
            extraBefore = new int[runs.Length + 1];
            int lines = 0;
            for (int k = 0; k < runs.Length; k++)
            {
                firstLines[k] = lines;
                lines += runs[k].Count;
                extraBefore[k + 1] = extraBefore[k] + runs[k].ExtraRows;
            }
            LineCount = lines;
        }

        public TextSnapshot Text { get; }

        public double Width { get; }

        public int Look { get; }

        public int LineCount { get; }

        /// <summary>How many runs the lines are kept in.</summary>
        public int Runs => runs.Length;

        /// <summary>The rows every line's wraps add.</summary>
        public int ExtraRows => extraBefore[^1];

        public LineWrap? this[int line]
        {
            get
            {
                int k = RunOf(line);
                return runs[k][line - firstLines[k]];
            }
        }

        /// <summary>The rows the wraps of the lines before a line add.</summary>
        public int ExtraBefore(int line)
        {
            if (line >= LineCount)
            {
                return ExtraRows;
            }
            int k = RunOf(line);
            return extraBefore[k] + runs[k].ExtraBefore(line - firstLines[k]);
        }

        /// <summary>Lines [start, start + count) as they are, given to a map being made: the runs that hold them, the first
        /// and last cut to fit.</summary>
        public void CopyTo(WrapBuilder made, int start, int count)
        {
            if (count <= 0)
            {
                return;
            }
            int end = start + count;
            for (int k = RunOf(start); k < runs.Length && firstLines[k] < end; k++)
            {
                int from = Math.Max(start, firstLines[k]) - firstLines[k];
                int to = Math.Min(end, firstLines[k] + runs[k].Count) - firstLines[k];
                made.Add(runs[k].Slice(from, to - from));
            }
        }

        private int RunOf(int line)
        {
            int k = Array.BinarySearch(firstLines, line);
            return k >= 0 ? k : ~k - 1;
        }
    }

    /// <summary>Lines' wraps as a stretch of one array, with the rows the wraps before each line of the array add, so a
    /// stretch of it is shared by every map that has those lines unchanged.</summary>
    private readonly struct WrapRun(LineWrap?[] lines, int[] extra, int from, int count)
    {
        public int Count { get; } = count;

        public int ExtraRows => extra[from + Count] - extra[from];

        public LineWrap? this[int line] => lines[from + line];

        public static WrapRun Of(LineWrap?[] lines)
        {
            int[] extra = new int[lines.Length + 1];
            for (int i = 0; i < lines.Length; i++)
            {
                extra[i + 1] = extra[i] + (lines[i]?.Starts.Length ?? 0);
            }
            return new WrapRun(lines, extra, 0, lines.Length);
        }

        public int ExtraBefore(int line) => extra[from + line] - extra[from];

        public WrapRun Slice(int start, int length) => new(lines, extra, from + start, length);
    }

    /// <summary>Makes a <see cref="WrapMap"/> line by line, taking an older map's runs whole where they are long enough to
    /// share and copying the short ones, so the runs stay few however many edits a file has had.</summary>
    private sealed class WrapBuilder(int runsAbout = 0)
    {
        private const int RunLength = 512;
        private const int SharedAtLeast = 64;

        private readonly List<WrapRun> runs = new(runsAbout);
        private LineWrap?[]? pending;
        private int filled;

        public void Add(LineWrap? wrap)
        {
            if (filled == RunLength)
            {
                Flush();
            }
            pending ??= ArrayPool<LineWrap?>.Shared.Rent(RunLength);
            pending[filled++] = wrap;
        }

        /// <summary>Lines [start, start + count) of an older map, as they are.</summary>
        public void AddRange(WrapMap map, int start, int count) => map.CopyTo(this, start, count);

        /// <summary>A stretch of an older map's run.</summary>
        public void Add(WrapRun run)
        {
            if (run.Count >= SharedAtLeast)
            {
                Flush();
                runs.Add(run);
                return;
            }
            for (int i = 0; i < run.Count; i++)
            {
                Add(run[i]);
            }
        }

        public WrapMap Build(TextSnapshot text, double width, int look)
        {
            Flush();
            if (pending is not null)
            {
                ArrayPool<LineWrap?>.Shared.Return(pending, clearArray: true);
                pending = null;
            }
            return new WrapMap(text, width, look, [.. runs]);
        }

        private void Flush()
        {
            if (filled > 0)
            {
                runs.Add(WrapRun.Of(pending.AsSpan(0, filled).ToArray()));
                filled = 0;
            }
        }
    }

    /// <summary>Lines and rows, both ways: the lines the folds leave showing, each one row or, wrapped, as many as its wraps
    /// make. A line's row is its row among the lines showing and the rows the wraps of the lines showing before it add, from
    /// the wraps' runs less what the folded ranges before it hide; a row's line is found by halving the lines. With nothing
    /// wrapped it is the folds' own map. Made for each text and set of folds, it reads only the folded ranges.</summary>
    private sealed class RowMap
    {
        private static readonly int[] NoneHidden = [0];

        /// <summary>The rows the wraps add to the lines the folded ranges before each hide; one more than the ranges.</summary>
        private readonly int[] hiddenExtra = NoneHidden;

        public RowMap(FoldLines folds, WrapMap? wraps)
        {
            Folds = folds;
            Wraps = wraps;
            Count = folds.Text.LineCount - folds.HiddenCount;
            if (wraps is null)
            {
                return;
            }
            if (folds.Hidden.Length > 0)
            {
                hiddenExtra = new int[folds.Hidden.Length + 1];
            }
            for (int k = 0; k < folds.Hidden.Length; k++)
            {
                (int first, int last) = folds.Hidden[k];
                hiddenExtra[k + 1] = hiddenExtra[k] + wraps.ExtraBefore(last + 1) - wraps.ExtraBefore(first);
            }
            Count += wraps.ExtraRows - hiddenExtra[^1];
        }

        public FoldLines Folds { get; }

        public WrapMap? Wraps { get; }

        public int Count { get; }

        /// <summary>A line's first row; a folded line's is its fold's first line's.</summary>
        public int RowOf(int line)
        {
            line = Folds.Shown(line);
            return Folds.RowOf(line) + (Wraps is null ? 0 : Wraps.ExtraBefore(line) - hiddenExtra[Folds.RangeAtOrBefore(line) + 1]);
        }

        /// <summary>How many rows a line takes: one for a folded one, which shares its fold's.</summary>
        public int RowsOf(int line) =>
            Wraps is null || Folds.IsHidden(line) || line >= Wraps.LineCount ? 1 : 1 + (Wraps[line]?.Starts.Length ?? 0);

        /// <summary>The line a row shows and which of its rows it is; past the last row, the lines past the end.</summary>
        public (int Line, int Row) LineOfRow(int row)
        {
            if (Wraps is null || row < 0)
            {
                return (Folds.LineOfRow(row), 0);
            }
            if (row >= Count)
            {
                return (Folds.Text.LineCount + row - Count, 0);
            }
            // The last line whose first row is at most the row; a folded one is on its fold's first line's.
            int low = 0;
            int high = Folds.Text.LineCount - 1;
            while (low < high)
            {
                int middle = (int)((uint)(low + high + 1) >> 1);
                if (RowOf(middle) <= row)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            int line = Folds.Shown(low);
            return (line, row - RowOf(line));
        }
    }
}
