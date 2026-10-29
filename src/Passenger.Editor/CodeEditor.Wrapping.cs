using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Soft wrap: a line wider than the view goes on in rows below, broken after a space (or a comma or semicolon,
/// else before a dot or a bracket, else at the edge), its later rows from its own indent. <see cref="Rows"/> maps lines
/// to rows and back with the folds; a line's wraps come from its graphemes' widths, and after an edit only the rows it
/// reaches are wrapped again. A caret where a row wraps stands at the next row's start, or at the end of the row before
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
                KeepWraps();
                softWrap = value;
                wraps = value ? KeptWraps(document.Snapshot) : null;
            });
            if (!value)
            {
                StopWrapping();
            }
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
    /// yet. Made for another width or look, or for none, the lines on screen are wrapped at once and the rest kept as
    /// they were (a row a line where there were none) until the thread pool has wrapped them (<see cref="WrapRest"/>).</summary>
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
            if (wraps is { } map && ReferenceEquals(map.Text, text) && map.Width == width && map.Look == look)
            {
                if (!map.Settled && wrapping is null)
                {
                    // Kept unsettled while another text was shown.
                    WrapRest(map);
                }
                return map;
            }
            WrapMap from = wraps is { } old && ReferenceEquals(old.Text, text) ? old : WrapMap.Blank(text);
            RowMap rows = rowMap is { } kept && ReferenceEquals(kept.Wraps, from) && ReferenceEquals(kept.Folds, Folding) ? kept : new RowMap(Folding, from);
            (int line, int sub) = rows.LineOfRow(Math.Max(0, RowAtY(0)));
            wraps = map = WrapShown(from, Math.Min(line, text.LineCount - 1), ViewRows + sub, width)!;
            rowShape++;
            WrapRest(map);
            return map;
        }
    }

    /// <summary>How many rows the view shows, and one more.</summary>
    private int ViewRows => (int)Math.Ceiling(Math.Max(0, Bounds.Height) / metrics.LineHeight) + 1;

    /// <summary>The lines on screen wrapped at the width now before a frame draws them (those kept from another width,
    /// or taken to fit unread), the row at the top kept there.</summary>
    private void WrapOnScreen()
    {
        if (Wraps is not { Exact: false } map || Bounds.Height <= 0)
        {
            return;
        }
        (int line, int sub) = Rows.LineOfRow(Math.Max(0, RowAtY(0)));
        if (line < map.LineCount && WrapShown(map, line, ViewRows + sub, map.Width) is { } wrapped)
        {
            TakeWraps(wrapped);
        }
    }

    /// <summary>A line's wraps at the width now, wrapped first where the map has them from another width or took the line
    /// to fit unread: what a caret's place or a row's text is worked out from.</summary>
    private LineWrap? WrapOf(int line)
    {
        if (Wraps is not { } map || line >= map.LineCount)
        {
            return null;
        }
        if (!map.IsExact(line) && WrapShown(map, line, 1, map.Width) is { } wrapped)
        {
            TakeWraps(wrapped);
            return wrapped[line];
        }
        return map[line];
    }

    /// <summary>The map with the lines shown from <paramref name="line"/> on, until they make <paramref name="rows"/> rows,
    /// wrapped at the width where they are not yet; null where each was, or was found to fit as taken (marked so in
    /// place). From a map of another width or look, every other line is kept as it was there, to be wrapped again.</summary>
    private WrapMap? WrapShown(WrapMap from, int line, int rows, double width)
    {
        TextSnapshot text = from.Text;
        bool same = from.Width == width && from.Look == look;
        FoldLines folds = Folding;
        Advances measure = Measures();
        WrapBuilder? made = same ? null : new WrapBuilder(from.RunCount + 4);
        int copiedTo = 0;
        int k = folds.RangeAtOrBefore(line) + 1;
        int shown = 0;
        while (shown < rows && line < text.LineCount)
        {
            LineWrap? wrap = same && from.IsExact(line) ? from[line] : WrapLine(text, text.GetLine(line), width, measure);
            if (same && wrap is null && ReferenceEquals(from[line], LineWrap.Assumed) && from.IsFresh(line))
            {
                from.Verify(line);
            }
            else if (!same || !from.IsExact(line))
            {
                made ??= new WrapBuilder(from.RunCount + 4);
                from.CopyTo(made, copiedTo, line - copiedTo, same);
                if (same)
                {
                    Rewrapped(StartsOf(from[line]), StartsOf(wrap), line);
                }
                made.Add(wrap, fresh: true);
                copiedTo = line + 1;
            }
            shown += 1 + (wrap?.Breaks ?? 0);
            line++;
            if (k < folds.Hidden.Length && folds.Hidden[k].First == line)
            {
                line = folds.Hidden[k].Last + 1;
                k++;
            }
        }
        if (made is null)
        {
            return null;
        }
        from.CopyTo(made, copiedTo, from.LineCount - copiedTo, same);
        return made.Build(text, width, look);
    }

    /// <summary>Takes a map of the same text and width in place of the one drawn, the offset at the view's top kept on the
    /// row at the top.</summary>
    private void TakeWraps(WrapMap next)
    {
        TextSnapshot text = document.Snapshot;
        if (Bounds.Height <= 0 || wraps is not { } was || !ReferenceEquals(was.Text, text) || !ReferenceEquals(next.Text, text))
        {
            wraps = next;
            Dispatcher.UIThread.Post(FollowRows);
            return;
        }
        int top = Math.Max(0, RowAtY(0));
        (int line, int sub) = Rows.LineOfRow(top);
        double within = scroll.Y - (top * metrics.LineHeight);
        wraps = next;
        double y = MaxScrollY;
        if (line < text.LineCount)
        {
            LineSpan span = text.GetLine(line);
            int column = RowSpan.Of(span, was[line], sub).Start - span.Start;
            int row = Rows.RowOf(line) + (IsHidden(line) ? 0 : SubRowOf(next[line], column, beforeWrap: false));
            y = (row * metrics.LineHeight) + Math.Min(within, metrics.LineHeight - 1);
        }
        scroll = new Vector(scroll.X, Math.Clamp(y, 0, MaxScrollY));
        // Asked for a row as a frame is drawn (this one's or a part's beside it), the rest follows once it has been.
        Dispatcher.UIThread.Post(FollowRows);
    }

    /// <summary>The view, the scroll bars and the host told the rows changed under the view.</summary>
    private void FollowRows()
    {
        InvalidateVisual();
        UpdateScrollBars();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
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
    private RowSpan RowSpanOf(TextSnapshot text, int line, int row) => RowSpan.Of(text.GetLine(line), WrapOf(line), row);

    /// <summary>The row of the text an offset stands on, and the span of it: after a wrap there, or at the end of the row
    /// before it when <paramref name="beforeWrap"/>. An offset in folded lines is on its fold's first row.</summary>
    private (RowSpan Span, int Row) RowOfOffset(TextSnapshot text, int offset, bool beforeWrap)
    {
        int line = text.GetLineFromOffset(offset);
        LineWrap? wrap = WrapOf(line);
        LineSpan span = text.GetLine(line);
        int sub = SubRowOf(wrap, Math.Min(offset, span.End) - span.Start, beforeWrap);
        return (RowSpan.Of(span, wrap, sub), RowOf(line) + (IsHidden(line) ? 0 : sub));
    }

    /// <summary>The row an offset stands on as the rows are now, its line wrapped at another width where the map has it so:
    /// for a mark beside the text, which wraps no line to be placed.</summary>
    private int RowNear(TextSnapshot text, int offset, bool beforeWrap = false)
    {
        int line = text.GetLineFromOffset(offset);
        int column = Math.Min(offset, text.GetLine(line).End) - text.GetLineStart(line);
        return RowOf(line) + (IsHidden(line) ? 0 : SubRowOf(Wraps?[line], column, beforeWrap));
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

    /// <summary>The rows a frame shows, found again for each.</summary>
    private readonly List<RowSpan> rowsShown = [];

    /// <summary>The rows from <paramref name="firstRow"/> to <paramref name="lastRow"/>, in order, stepping over folded lines
    /// rather than visiting them.</summary>
    private List<RowSpan> RowsOnScreen(TextSnapshot text, int firstRow, int lastRow)
    {
        List<RowSpan> shown = rowsShown;
        shown.Clear();
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
            if (map[line] is { } wrap && wrap.Starts.BinarySearch(caret - text.GetLineStart(line)) >= 0)
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
        if (Wraps is null)
        {
            Move(s => TextNavigation.SmartHome(text, s.Caret), extend);
            return;
        }
        document.BreakUndoGroup();
        SetSelections(selections.With(s =>
        {
            int line = text.GetLineFromOffset(s.Caret);
            LineWrap? wrap = WrapOf(line);
            LineSpan span = text.GetLine(line);
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
        if (Wraps is null)
        {
            Move(s => TextNavigation.LineEnd(text, s.Caret), extend);
            return;
        }
        document.BreakUndoGroup();
        HashSet<int> lean = [];
        SelectionSet moved = selections.With(s =>
        {
            int line = text.GetLineFromOffset(s.Caret);
            LineWrap? wrap = WrapOf(line);
            LineSpan span = text.GetLine(line);
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
    /// where it is: the lines on screen at once, the rest on the thread pool.</summary>
    private void Rewrap()
    {
        look++;
        if (softWrap)
        {
            KeepTopLine(() => { });
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
            // The text wraps at the new width, on screen at once; the line at the top stays at the top.
            KeepTopLine(() => { });
        }
    }

    /// <summary>Ahead of everyone who hears of an edit (<see cref="TextDocument.ChangedFirst"/>): the decorations' hints and
    /// folds carried through it, then the wraps, so the lines it touched wrap with their hints where they now stand.</summary>
    private void OnChangedFirst(object? sender, DocumentChangedEventArgs change)
    {
        views.Follow(change, advances);
        decorations.Follow(change);
        wrappedHints = decorations.InlayHints;
        FollowWraps(change);
    }

    /// <summary>The wraps carried through an edit: a line it did not touch keeps its rows, a wrapped line it changed within
    /// keeps the rows it did not reach (<see cref="TryCarry"/>), and the other lines it touched are wrapped again. Ahead of
    /// everyone else who hears of the edit, since the host moving its folds through it asks for the rows, and what follows
    /// it asks where the carets are drawn.</summary>
    private void FollowWraps(DocumentChangedEventArgs change)
    {
        if (!softWrap || wraps is not { } map || !ReferenceEquals(map.Text, change.Before))
        {
            wraps = null;
            StopWrapping();
            return;
        }
        TextSnapshot before = change.Before;
        TextSnapshot after = change.After;
        WrapBuilder made = new(map.RunCount + 2);
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
            map.CopyTo(made, copiedTo, first - copiedTo, keepFresh: true);
            int lastAfter = last + shift + added;
            if (added == 0 && first == last && map.IsExact(first) && map[first] is { } was
                && TryCarry(was, change, first, first + shift, i, j, map.Width, measure, out LineWrap? carried))
            {
                made.Add(carried, fresh: true);
            }
            else
            {
                for (int line = first + shift; line <= lastAfter; line++)
                {
                    LineWrap? wrap = WrapLine(after, after.GetLine(line), map.Width, measure);
                    made.Add(wrap, fresh: true);
                    if (added == 0)
                    {
                        Rewrapped(StartsOf(map[line - shift]), StartsOf(wrap), line);
                    }
                }
            }
            wrapping?.Moves.Add((first + shift, lastAfter, added));
            if (added != 0)
            {
                rowShape++;
            }
            copiedTo = last + 1;
            shift += added;
            i = j;
        }
        map.CopyTo(made, copiedTo, map.LineCount - copiedTo, keepFresh: true);
        wraps = made.Build(after, map.Width, map.Look);
    }

    /// <summary>A wrapped line's rows carried through edits <paramref name="from"/> to <paramref name="to"/> inside it that
    /// leave it one line: around each edit, the rows before it whose wrap read as far as it and the rows from it are
    /// wrapped again, up to the first row past it that starts where one started before; the rest are kept, moved by what
    /// the edits changed, so a key costs the rows it reaches. False where an edit reaches the line's indent, from which
    /// every row's room comes.</summary>
    private bool TryCarry(LineWrap was, DocumentChangedEventArgs change, int line, int lineAfter, int from, int to, double width,
        Advances measure, out LineWrap? carried)
    {
        carried = null;
        IReadOnlyList<TextEdit> edits = change.Edits;
        LineSpan old = change.Before.GetLine(line);
        LineSpan now = change.After.GetLine(lineAfter);
        int grown = 0;
        for (int e = from; e < to; e++)
        {
            grown += edits[e].Text.Length - edits[e].Length;
        }
        if (now.Length != old.Length + grown || edits[from].Start - old.Start <= was.FirstText)
        {
            return false;
        }
        WrapReading chars = Reading(change.After, now);
        RowRule rule = new(now.Start, was.FirstText, Fixed(width), Fixed(width - was.Indent), measure, HintsNow);
        ReadOnlySpan<int> starts = was.Starts;
        List<int> rows = rowsMade;
        rows.Clear();
        // The next of the starts before, the rows at the list's start that are the text's now, and what earlier edits grew.
        int next = 0;
        int settled = 0;
        int shift = 0;
        int firstReach = was.FirstReach;
        bool ended = false;
        int at = from;
        while (at < to && !ended)
        {
            int edited = edits[at].Start - old.Start;
            while (next < starts.Length && starts[next] <= edited)
            {
                rows.Add(starts[next++] + shift);
            }
            edited += shift;
            // Back over the rows before the edit whose wrap read as far as it.
            while (rows.Count > settled && RowEnd(chars, rows.Count > 1 ? rows[^2] : 0, rule).Reach > edited)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            if (settled == 0 && rows.Count > 1 && was.Indent > 0 && firstReach > edited)
            {
                // The first row has more room than the rest, so its wrap may have read further than theirs.
                rows.Clear();
            }
            int start = rows.Count > 0 ? rows[^1] : 0;
            int passed = at;
            int passedShift = shift;
            while (true)
            {
                (int end, int reach) = RowEnd(chars, start, rule);
                if (start == 0)
                {
                    firstReach = reach;
                }
                if (end < 0)
                {
                    ended = true;
                    break;
                }
                rows.Add(end);
                while (passed < to && edits[passed].Start - old.Start + passedShift < end)
                {
                    passedShift += edits[passed].Text.Length - edits[passed].Length;
                    passed++;
                }
                // Where the row starts in the text before, where it is past the edits it passed and short of the next.
                int column = end - passedShift;
                if (passed > at && column > edits[passed - 1].End - old.Start && (passed == to || column < edits[passed].Start - old.Start))
                {
                    while (next < starts.Length && starts[next] < column)
                    {
                        next++;
                    }
                    if (next < starts.Length && starts[next] == column)
                    {
                        // From a row that started here before, the rows read what they read then, up to the next edit.
                        next++;
                        settled = rows.Count;
                        at = passed;
                        shift = passedShift;
                        break;
                    }
                }
                start = end;
            }
        }
        if (!ended)
        {
            while (next < starts.Length)
            {
                rows.Add(starts[next++] + shift);
            }
        }
        ReadOnlySpan<int> made = CollectionsMarshal.AsSpan(rows);
        Rewrapped(starts, made, lineAfter);
        if (made.Length > 0)
        {
            was.Carry(made, firstReach);
            carried = was;
        }
        return true;
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
        WrapBuilder made = new(map.RunCount + touched.Count + 1);
        Advances measure = Measures();
        int copiedTo = 0;
        foreach (int line in touched)
        {
            map.CopyTo(made, copiedTo, line - copiedTo, keepFresh: true);
            LineWrap? wrap = WrapLine(text, text.GetLine(line), map.Width, measure);
            made.Add(wrap, fresh: true);
            Rewrapped(StartsOf(map[line]), StartsOf(wrap), line);
            copiedTo = line + 1;
        }
        map.CopyTo(made, copiedTo, map.LineCount - copiedTo, keepFresh: true);
        wraps = made.Build(text, map.Width, map.Look);
    }

    private static int Breaks(string text) => text.AsSpan().Count('\n');

    private static ReadOnlySpan<int> StartsOf(LineWrap? wrap) => wrap is null ? [] : wrap.Starts;

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

    /// <summary>The rows a line's wraps were last worked out in, reused for the next.</summary>
    private readonly List<int> rowsMade = [];

    private WrapReading? wrapReading;

    private RowHints? hintsNow;

    /// <summary>The hints as they stand, which the rows wrapped on the window's thread share their room with.</summary>
    private RowHints HintsNow => hintsNow ??= RowHints.Live(this);

    private WrapReading Reading(TextSnapshot text, LineSpan line)
    {
        wrapReading ??= new WrapReading(this);
        wrapReading.Begin(text, line);
        return wrapReading;
    }

    /// <summary><see cref="WrapLine(WrapReading, TextSnapshot, LineSpan, double, Advances, RowHints, bool, List{int})"/> on the
    /// window's thread, with the hints as they stand, what it reads counted.</summary>
    private LineWrap? WrapLine(TextSnapshot text, LineSpan line, double width, Advances measure) =>
        WrapLine(wrapReading ??= new WrapReading(this), text, line, width, measure, HintsNow, wrapIndent, rowsMade);

    /// <summary>Where one line wraps at a width, or null for a line that fits: its rows one after another, each from where
    /// the one before it ended (<see cref="RowEnd"/>), its rows after the first drawn from its indent where that is no more
    /// than half the width (with <paramref name="indentWraps"/>).</summary>
    private static LineWrap? WrapLine(WrapReading chars, TextSnapshot text, LineSpan line, double width, Advances measure, RowHints hints,
        bool indentWraps, List<int> rows)
    {
        int length = line.Length;
        if (length == 0)
        {
            return null;
        }
        chars.Begin(text, line);
        int firstText = 0;
        double indent = 0;
        while (firstText < length)
        {
            chars.Cover(firstText, firstText + 1);
            char c = chars[firstText];
            if (!IsBlank(c))
            {
                break;
            }
            indent += c == '\t' ? measure.Tab : measure.Of(chars.Span(firstText, 1));
            firstText++;
        }
        if (firstText == length)
        {
            return null;
        }
        double drawnIndent = indentWraps && indent <= width / 2 ? indent : 0;
        RowRule rule = new(line.Start, firstText, Fixed(width), Fixed(width - drawnIndent), measure, hints);
        rows.Clear();
        (int end, int firstReach) = RowEnd(chars, 0, rule);
        while (end >= 0)
        {
            rows.Add(end);
            end = RowEnd(chars, end, rule).End;
        }
        return rows.Count == 0 ? null : new LineWrap([.. rows], drawnIndent, firstText, firstReach);
    }

    /// <summary>Where a line's row from a column wraps (-1 where the line ends on it), and past what column it read, which an
    /// edit beyond leaves it as it is: its graphemes' widths and its hints' summed exactly from its start, so it is the same
    /// whatever came before, and past its room it ends after its last space, comma or semicolon, else before its last dot or
    /// bracket, else before the grapheme that would not fit. Spaces never wrap: they stay at the end of their row however
    /// far they reach, so a row starts with text.</summary>
    private static (int End, int Reach) RowEnd(WrapReading chars, int start, in RowRule rule)
    {
        int length = chars.Length;
        Int128 room = start == 0 ? rule.FirstRoom : rule.Room;
        RowHints hints = rule.Hints;
        int h = hints.From(rule.LineStart + start);
        int hintAt = hints.ColumnOf(h, rule.LineStart);
        if (rule.Uniform >= 0 && PlainRowEnd(chars, start, room, hintAt, rule) is { } plain)
        {
            return plain;
        }
        Int128 x = 0;
        int good = -1;
        int fair = -1;
        int i = start;
        while (i < length)
        {
            chars.Cover(i > start ? i - 1 : i, i + 2);
            char c = chars[i];
            if (i > rule.FirstText && i > start && !IsBlank(c))
            {
                char previous = chars[i - 1];
                if (IsBlank(previous) || previous is ',' or ';')
                {
                    good = i;
                }
                else if (c is '.' or '(' or '[' or '{')
                {
                    fair = i;
                }
            }
            while (hintAt <= i)
            {
                x += hints.RoomOf(h);
                h = hints.After(h);
                hintAt = hints.ColumnOf(h, rule.LineStart);
            }
            int size = chars.Grapheme(i);
            Int128 advance = c == '\t' ? rule.Tab
                : size == 1 && rule.Uniform >= 0 && c is >= ' ' and <= '~' ? rule.Uniform
                : Fixed(rule.Measure.Of(chars.Span(i, size)));
            if (x + advance > room && i > start && !IsBlank(c))
            {
                return (good > start ? good : fair > start ? fair : i, i + size + 2);
            }
            x += advance;
            i += size;
        }
        return (-1, length + 1);
    }

    /// <summary>The same row's end where it is printable ASCII with no hint, in a font that gives each the one width: found
    /// by counting to the edge and looking back for the break, rather than by summing; null where the row is otherwise.</summary>
    private static (int End, int Reach)? PlainRowEnd(WrapReading chars, int start, Int128 room, int hintAt, in RowRule rule)
    {
        int length = chars.Length;
        Int128 fits = room / rule.Uniform;
        int over = fits >= length - start ? length : start + Math.Max(1, (int)fits);
        chars.Cover(start, over + 2);
        // The first text that would not fit: spaces past the edge stay on the row.
        int text = chars.SpacesFrom(over);
        if (text == length)
        {
            return hintAt >= length && chars.Plain(start, length) ? (-1, length + 1) : null;
        }
        if (hintAt <= text || !chars.Plain(start, Math.Min(length, text + 2)))
        {
            return null;
        }
        return (BreakBefore(chars, text, Math.Max(start, rule.FirstText)), text + 2);
    }

    /// <summary>Where a row of plain text that would not fit at <paramref name="over"/> ends: after its last space, comma or
    /// semicolon past <paramref name="after"/>, else before its last dot or bracket, else at <paramref name="over"/>.</summary>
    private static int BreakBefore(WrapReading chars, int over, int after)
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
    private static int UpperBound(ReadOnlySpan<int> values, int value)
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

    /// <summary>A wrapped line: the columns its rows after the first start at, rising; how far in their text is drawn; where
    /// its text begins after its indent; and how far its first row's wrap read (<see cref="RowEnd"/>). An edit inside the
    /// line carries its rows in place (<see cref="TryCarry"/>), the map then holding it being the one of the text it made.</summary>
    private sealed class LineWrap
    {
        /// <summary>A line taken to fit unread, too short to wrap however wide its characters are but for one never yet
        /// measured: one row, as a line that fits, read before it is drawn or a place on it is worked out.</summary>
        public static readonly LineWrap Assumed = new([], 0, 0, 0);

        private int[] starts;
        private int count;

        public LineWrap(int[] starts, double indent, int firstText, int firstReach)
        {
            this.starts = starts;
            count = starts.Length;
            Indent = indent;
            FirstText = firstText;
            FirstReach = firstReach;
        }

        public ReadOnlySpan<int> Starts => starts.AsSpan(0, count);

        /// <summary>How many times the line wraps: its rows less one.</summary>
        public int Breaks => count;

        public double Indent { get; }

        public int FirstText { get; }

        public int FirstReach { get; private set; }

        /// <summary>The line's rows after an edit inside it, and how far its first row's wrap reads now.</summary>
        public void Carry(ReadOnlySpan<int> now, int firstReach)
        {
            if (now.Length > starts.Length)
            {
                starts = new int[now.Length + (now.Length / 4) + 8];
            }
            now.CopyTo(starts);
            count = now.Length;
            FirstReach = firstReach;
        }
    }

    /// <summary>What a line's rows wrap by: where the line starts, where its text begins after its indent, the room of its
    /// first row and of the rest, the graphemes' widths, the hints sharing the rows, and printable ASCII's one width (or
    /// -1) and a tab's in <see cref="Unit"/>s.</summary>
    private readonly struct RowRule(int lineStart, int firstText, Int128 firstRoom, Int128 room, Advances measure, RowHints hints)
    {
        public int LineStart { get; } = lineStart;
        public int FirstText { get; } = firstText;
        public Int128 FirstRoom { get; } = firstRoom;
        public Int128 Room { get; } = room;
        public Advances Measure { get; } = measure;
        public RowHints Hints { get; } = hints;
        public Int128 Uniform { get; } = double.IsNaN(measure.Uniform) ? -1 : Fixed(measure.Uniform);
        public Int128 Tab { get; } = Fixed(measure.Tab);
    }

    /// <summary>The line being wrapped, read from its text a window at a time, so wrapping a few of its rows reads only
    /// around them; what the window's thread reads is counted in its editor's <see cref="CharactersWrapped"/>.</summary>
    private sealed class WrapReading(CodeEditor? counted)
    {
        private const int Window = 1024;

        private char[] buffer = new char[Window];
        private TextSnapshot? text;
        private int lineStart;
        private int readAt;
        private int read;

        public int Length { get; private set; }

        /// <summary>A character read: <see cref="Cover"/> it first.</summary>
        public char this[int column] => buffer[column - readAt];

        public void Begin(TextSnapshot snapshot, LineSpan line)
        {
            text = snapshot;
            lineStart = line.Start;
            Length = line.Length;
            readAt = 0;
            read = 0;
        }

        /// <summary>Columns [from, to) read, up to the line's end: a window from <paramref name="from"/> read where they are not.</summary>
        public void Cover(int from, int to)
        {
            to = Math.Min(to, Length);
            if (from >= readAt && to <= readAt + read)
            {
                return;
            }
            int count = Math.Max(to - from, Math.Min(Window, Length - from));
            if (count > buffer.Length)
            {
                buffer = new char[Math.Max(count, buffer.Length * 2)];
            }
            text!.CopyTo(lineStart + from, buffer.AsSpan(0, count));
            if (counted is not null)
            {
                counted.CharactersWrapped += count;
            }
            readAt = from;
            read = count;
        }

        /// <summary>Characters read: <see cref="Cover"/> them first.</summary>
        public ReadOnlySpan<char> Span(int from, int length) => buffer.AsSpan(from - readAt, length);

        /// <summary>The length of the grapheme at a column, read on as far as it goes and a whole character past it, so what
        /// follows it is read as the text has it.</summary>
        public int Grapheme(int at)
        {
            Cover(at, at + 3);
            while (true)
            {
                int size = CodeEditor.Grapheme(buffer.AsSpan(at - readAt, readAt + read - at), 0);
                if (at + size + 1 < readAt + read || readAt + read == Length)
                {
                    return size;
                }
                Cover(at, at + (2 * (readAt + read - at)));
            }
        }

        /// <summary>The first column from one on that is no space, or the line's length.</summary>
        public int SpacesFrom(int from)
        {
            while (from < Length)
            {
                Cover(from, from + 1);
                int run = buffer.AsSpan(from - readAt, readAt + read - from).IndexOfAnyExcept(' ');
                if (run >= 0)
                {
                    return from + run;
                }
                from = readAt + read;
            }
            return Length;
        }

        /// <summary>Whether columns [from, to) are all printable ASCII.</summary>
        public bool Plain(int from, int to)
        {
            Cover(from, to);
            return !Span(from, to - from).ContainsAnyExceptInRange(' ', '~');
        }
    }

    /// <summary>Where every line of one text wraps at one width, null for a line that fits, in runs of lines that the maps of
    /// later texts share: an edit's map makes again the lines it touched and takes the runs around them as they were, so a
    /// key costs the lines it changed and a few small arrays, whatever the length of the file. Replaced, never changed but
    /// for a line an edit carries in place for the next text's map, or one taken to fit found to, so a row map made from it
    /// stays true to it while its text stands. A run made at another width or look keeps that one's rows until wrapped
    /// again (stale), and a line may be taken to fit unread (<see cref="LineWrap.Assumed"/>).</summary>
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
            bool settled = true;
            bool exact = true;
            for (int k = 0; k < runs.Length; k++)
            {
                firstLines[k] = lines;
                lines += runs[k].Count;
                extraBefore[k + 1] = extraBefore[k] + runs[k].ExtraRows;
                settled &= runs[k].Fresh;
                exact &= runs[k].Fresh && !runs[k].Assumes;
            }
            LineCount = lines;
            Settled = settled;
            Exact = exact;
        }

        /// <summary>A text's lines with none wrapped yet, each a row: what a text first shown wraps from.</summary>
        public static WrapMap Blank(TextSnapshot text) => new(text, double.NaN, int.MinValue, [WrapRun.Blank(text.LineCount)]);

        public TextSnapshot Text { get; }

        public double Width { get; }

        public int Look { get; }

        public int LineCount { get; }

        /// <summary>Whether every line is wrapped at this width and look, or taken to fit there.</summary>
        public bool Settled { get; }

        /// <summary>Whether every line is wrapped at this width and look, none taken to fit unread.</summary>
        public bool Exact { get; }

        /// <summary>How many runs the lines are kept in.</summary>
        public int RunCount => runs.Length;

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

        /// <summary>A run and the line it starts at.</summary>
        public (int First, WrapRun Run) RunAt(int k) => (firstLines[k], runs[k]);

        /// <summary>Whether a line is wrapped at this width and look, or taken to fit there.</summary>
        public bool IsFresh(int line) => runs[RunOf(line)].Fresh;

        /// <summary>Whether a line is wrapped at this width and look, not taken to fit unread.</summary>
        public bool IsExact(int line)
        {
            int k = RunOf(line);
            return runs[k].Fresh && !ReferenceEquals(runs[k][line - firstLines[k]], LineWrap.Assumed);
        }

        /// <summary>A line taken to fit, read and found to, marked so in place, where every map sharing its run reads it.</summary>
        public void Verify(int line)
        {
            int k = RunOf(line);
            runs[k].Verify(line - firstLines[k]);
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
        /// and last cut to fit, stale unless <paramref name="keepFresh"/> (a map of this width and look).</summary>
        public void CopyTo(WrapBuilder made, int start, int count, bool keepFresh)
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
                WrapRun part = runs[k].Slice(from, to - from);
                made.Add(keepFresh ? part : part.Stale);
            }
        }

        private int RunOf(int line)
        {
            int k = Array.BinarySearch(firstLines, line);
            return k >= 0 ? k : ~k - 1;
        }
    }

    /// <summary>Lines' wraps as a stretch of one array, with the rows the wraps before each line of the array add, so a
    /// stretch of it is shared by every map that has those lines unchanged; whether they were wrapped at the width and look
    /// of the map holding it (fresh), and whether some were taken to fit unread. A blank run has no array: every line a row.</summary>
    private readonly struct WrapRun(LineWrap?[]? lines, int[]? extra, int from, int count, bool fresh, bool assumes)
    {
        public int Count { get; } = count;

        public bool Fresh { get; } = fresh;

        public bool Assumes { get; } = assumes;

        /// <summary>Whether it has no array, every line a row, none wrapped.</summary>
        public bool IsBlank => lines is null;

        public int ExtraRows => extra is null ? 0 : extra[from + Count] - extra[from];

        public LineWrap? this[int line] => lines?[from + line];

        /// <summary>A run that is the stale one of this, as a map of another width or look takes it.</summary>
        public WrapRun Stale => new(lines, extra, from, Count, false, Assumes);

        public static WrapRun Of(LineWrap?[] lines, bool fresh)
        {
            int[] extra = new int[lines.Length + 1];
            bool assumes = false;
            for (int i = 0; i < lines.Length; i++)
            {
                extra[i + 1] = extra[i] + (lines[i]?.Breaks ?? 0);
                assumes |= ReferenceEquals(lines[i], LineWrap.Assumed);
            }
            return new WrapRun(lines, extra, 0, lines.Length, fresh, assumes);
        }

        public static WrapRun Blank(int count) => new(null, null, 0, count, false, false);

        public int ExtraBefore(int line) => extra is null ? 0 : extra[from + line] - extra[from];

        public WrapRun Slice(int start, int length) => new(lines, extra, from + start, length, Fresh, Assumes);

        /// <summary>A line taken to fit marked as found to, which draws and counts the same.</summary>
        public void Verify(int line)
        {
            if (lines is not null && ReferenceEquals(lines[from + line], LineWrap.Assumed))
            {
                lines[from + line] = null;
            }
        }
    }

    /// <summary>Makes a <see cref="WrapMap"/> line by line, taking an older map's runs whole where they are long enough to
    /// share and copying the short ones, so the runs stay few however many edits a file has had; a run holds lines of
    /// one freshness, in runs of <paramref name="runLength"/> lines at most.</summary>
    private sealed class WrapBuilder(int runsAbout = 0, int runLength = 512)
    {
        private const int SharedAtLeast = 64;

        private readonly List<WrapRun> runs = new(runsAbout);
        private LineWrap?[]? pending;
        private int filled;
        private bool pendingFresh;

        public void Add(LineWrap? wrap, bool fresh)
        {
            if (filled == runLength || (filled > 0 && fresh != pendingFresh))
            {
                Flush();
            }
            pending ??= ArrayPool<LineWrap?>.Shared.Rent(runLength);
            pendingFresh = fresh;
            pending[filled++] = wrap;
        }

        /// <summary>A stretch of an older map's run.</summary>
        public void Add(WrapRun run)
        {
            if (run.Count >= SharedAtLeast || run.IsBlank)
            {
                Share(run);
                return;
            }
            for (int i = 0; i < run.Count; i++)
            {
                Add(run[i], run.Fresh);
            }
        }

        /// <summary>A run taken whole however short: for the thread pool, which reads no line of a run another map holds.</summary>
        public void Share(WrapRun run)
        {
            if (run.Count > 0)
            {
                Flush();
                runs.Add(run);
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
                runs.Add(WrapRun.Of(pending.AsSpan(0, filled).ToArray(), pendingFresh));
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
            Wraps is null || Folds.IsHidden(line) || line >= Wraps.LineCount ? 1 : 1 + (Wraps[line]?.Breaks ?? 0);

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
