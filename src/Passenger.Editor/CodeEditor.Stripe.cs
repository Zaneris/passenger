using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The error stripe: the vertical scrollbar's track stays and carries a mark for each problem (errors over
/// warnings over the rest), changed lines, search matches, the uses of the symbol at the caret, bookmarks and the
/// caret, each where its line is in the file (a folded one at its fold). A mark says what it is on hover and takes the
/// caret there on a click. Over the stripe's top, the file's counts of errors and warnings, or a green tick: a click
/// goes to the next problem and asks the host for Problems.</summary>
public sealed partial class CodeEditor
{
    private readonly ErrorStripe stripe;
    private readonly ProblemCounts counts;
    private bool showErrorStripe = true;

    /// <summary>The counts above the stripe were clicked: the host shows Problems.</summary>
    public event EventHandler? ProblemsRequested;

    /// <summary>Whether the scrollbar carries the error stripe and the counts (a console's and a preview's go without).</summary>
    public bool ShowErrorStripe
    {
        get => showErrorStripe;
        set
        {
            showErrorStripe = value;
            vertical.AllowAutoHide = !value;
            vertical.Classes.Set("stripe", value);
            stripe.IsVisible = counts.IsVisible = value;
            if (switcher is not null)
            {
                switcher.IsVisible = value && contextLabel is not null;
            }
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    /// <summary>The marks on the stripe now, with where each goes and what it says.</summary>
    public IReadOnlyList<StripeMark> StripeMarks
    {
        get
        {
            if (!showErrorStripe)
            {
                return [];
            }
            StripeBuild built = BuiltMarks();
            StripeMark[] marks = new StripeMark[built.Count + 1];
            for (int i = 0; i < built.Count; i++)
            {
                marks[i] = built.Marks[i] with { Message = Said(built.Marks[i]) };
            }
            marks[^1] = CaretMark();
            return marks;
        }
    }

    /// <summary>How many times the stripe's marks have been worked out: once for each change to the text, the markers,
    /// the search's matches, the uses highlighted, the folds, the bookmarks or the changed lines, never for a frame or a
    /// pointer move.</summary>
    public int StripeMarksBuilt { get; private set; }

    /// <summary>The counts' errors and warnings, as their widget shows them, counted when the markers are set.</summary>
    public (int Errors, int Warnings) ProblemCount => decorations.Problems;

    /// <summary>The middle of where a stripe mark is drawn, in this control's coordinates: what a click on it hits.</summary>
    public Point StripePoint(StripeMark mark)
    {
        Rect track = TrackRect();
        return ErrorStripe.Drawn(mark, ErrorStripe.Span(mark, track, RowCount), track).Center;
    }

    /// <summary>Goes to the next error or warning after the caret, wrapping, as the counts' click does.</summary>
    public void GoToNextProblem()
    {
        List<Marker> problems = [.. Markers.Where(m => m.Kind is MarkerKind.Error or MarkerKind.Warning)];
        if (problems.Count == 0)
        {
            return;
        }
        int caret = selections.Primary.Caret;
        Marker next = problems.FirstOrDefault(m => m.Range.Start > caret, problems[0]);
        GoTo(document.Snapshot.GetPosition(next.Range.Start));
    }

    /// <summary>Counts the changes to the text that added or took away a line break, since which a mark's line may be
    /// another: a key that leaves every line where it was leaves every mark on its row.</summary>
    private int lineShape;

    private void FollowLineShape(DocumentChangedEventArgs change)
    {
        foreach (TextEdit edit in change.Edits)
        {
            if (edit.Text.Contains('\n') || (edit.Length > 0 && change.Before.GetLineFromOffset(edit.Start) != change.Before.GetLineFromOffset(edit.End)))
            {
                lineShape++;
                return;
            }
        }
    }

    /// <summary>Counts the wraps made such that a line may stand on another row: made anew, or carried through an edit that
    /// gave a line another number of rows or added or took away lines.</summary>
    private int rowShape;

    /// <summary>The lines whose wraps an edit moved within them, each still on as many rows, since the stripe's marks were
    /// last worked out or found standing: a match or a use on one may now be on another of its rows.</summary>
    private readonly List<int> rebroken = [];

    /// <summary>The most lines <see cref="rebroken"/> keeps before the marks are taken for moved, worked out again.</summary>
    private const int RebrokenKept = 64;

    /// <summary>A line wrapped again where it stands (an edit or a hint on it): one taking another number of rows moves the
    /// lines after it; one whose rows break elsewhere moves only what is within it.</summary>
    private void Rewrapped(LineWrap? was, LineWrap? now, int line)
    {
        ReadOnlySpan<int> before = was is null ? [] : was.Starts;
        ReadOnlySpan<int> after = now is null ? [] : now.Starts;
        if (before.Length != after.Length)
        {
            rowShape++;
            rebroken.Clear();
        }
        else if (!before.SequenceEqual(after) && !rebroken.Contains(line))
        {
            if (rebroken.Count < RebrokenKept)
            {
                rebroken.Add(line);
            }
            else
            {
                rowShape++;
                rebroken.Clear();
            }
        }
    }

    /// <summary>The stripe's marks but the caret's, worked out again only when what they come from is replaced, and then
    /// not when only the text changed and no line came or went or took another number of rows, the problems, matches and
    /// uses the same ones moved with it (as the document carries markers through a key): each mark stands on the line and
    /// row it stood on. Worked out in one pass over each list into <see cref="stripeMarks"/>, whose length only grows.</summary>
    private StripeBuild BuiltMarks()
    {
        TextSnapshot text = document.Snapshot;
        RowMap rows = Rows;
        MarkerList markers = Markers;
        IReadOnlyList<TextRange> matches = Highlights;
        IReadOnlyList<UsageHighlight> uses = UsageHighlights;
        if (stripeBuild is { } built && built.Shows(text, markers, matches, uses, rows, Bookmarks, ChangeMarks))
        {
            return built;
        }
        if (stripeBuild is { } kept && kept.Shape == lineShape && kept.Matched == highlights.Given && kept.Used == usages.Given
            && ReferenceEquals(kept.Bookmarks, Bookmarks) && ReferenceEquals(kept.Changes, ChangeMarks) && SameRows(kept, rows, text, matches, uses)
            && kept.Markers.SameMoved(markers))
        {
            rebroken.Clear();
            stripeBuild = kept with { Text = text, Markers = markers, Highlights = matches, Usages = uses, Rows = rows };
            return stripeBuild;
        }
        rebroken.Clear();
        int count = 0;
        foreach (ChangeMark change in ChangeMarks)
        {
            int line = Math.Min(change.Line, text.LineCount - 1);
            int last = Math.Min(line + Math.Max(1, change.Count) - 1, text.LineCount - 1);
            string what = change.Kind switch { ChangeMarkKind.Added => "Added lines", ChangeMarkKind.Deleted => "Deleted lines", ChangeMarkKind.Conflict => "Conflict", _ => "Modified lines" };
            Mark(ref count, new StripeMark(StripeMarkKind.Change, RowOf(line), Math.Max(1, RowOf(last) + rows.RowsOf(last) - RowOf(line)),
                text.GetLineStart(line), what, change.Kind, line));
        }
        foreach (int line in Bookmarks)
        {
            if (line < text.LineCount)
            {
                Mark(ref count, new StripeMark(StripeMarkKind.Bookmark, RowOf(line), 1, text.GetLineStart(line), "Bookmark", Line: line));
            }
        }
        // A match or a use is marked at its own row of a wrapped line, and knows which it is as the text stands.
        int matchesAt = count;
        for (int i = 0; i < matches.Count; i++)
        {
            int at = matches[i].Start;
            Mark(ref count, new StripeMark(StripeMarkKind.SearchMatch, RowOfOffset(text, Math.Min(at, text.Length), beforeWrap: false).Row, 1, at, "Search result", Item: i));
        }
        int usesAt = count;
        for (int i = 0; i < uses.Count; i++)
        {
            UsageHighlight use = uses[i];
            Mark(ref count, new StripeMark(use.IsWrite ? StripeMarkKind.WriteUsage : StripeMarkKind.Usage, RowOfOffset(text, Math.Min(use.Range.Start, text.Length), beforeWrap: false).Row, 1,
                use.Range.Start, use.IsWrite ? "Write usage" : "Read usage", Item: i));
        }
        MarkProblems(ref count, text, markers);
        StripeMarksBuilt++;
        stripeBuild = new StripeBuild(text, markers, matches, uses, rows, Bookmarks, ChangeMarks, highlights.Given, usages.Given,
            stripeMarks, count, rows.Count, lineShape, StripeMarksBuilt, rowShape, matchesAt, usesAt);
        return stripeBuild;
    }

    /// <summary>The marks of the last build, written over by the next: a build is read only while it is the last.</summary>
    private StripeMark[] stripeMarks = [];

    /// <summary>The warnings' and the errors' marks, gathered while the information's are marked.</summary>
    private readonly List<StripeMark> warningMarks = [];
    private readonly List<StripeMark> errorMarks = [];

    private void Mark(ref int count, StripeMark mark)
    {
        if (count == stripeMarks.Length)
        {
            Array.Resize(ref stripeMarks, Math.Max(64, stripeMarks.Length * 2));
        }
        stripeMarks[count++] = mark;
    }

    /// <summary>One mark a line and kind of problem, at its first, in one pass over the markers (in the order of their
    /// starts, so a line's are together): the worst last, so it is drawn over the rest, each kind in line order.</summary>
    private void MarkProblems(ref int count, TextSnapshot text, MarkerList markers)
    {
        Span<int> lastLine = [-1, -1, -1];
        int line = -1;
        int nextLine = 0;
        for (int i = 0; i < markers.Count; i++)
        {
            Marker marker = markers[i];
            int kind = marker.Kind switch { MarkerKind.Info => 0, MarkerKind.Warning => 1, MarkerKind.Error => 2, _ => -1 };
            if (kind < 0)
            {
                continue;
            }
            int start = Math.Min(marker.Range.Start, text.Length);
            if (start >= nextLine)
            {
                line = text.GetLineFromOffset(start);
                nextLine = line + 1 < text.LineCount ? text.GetLineStart(line + 1) : int.MaxValue;
            }
            if (lastLine[kind] == line)
            {
                continue;
            }
            lastLine[kind] = line;
            StripeMark mark = new(kind switch { 0 => StripeMarkKind.Info, 1 => StripeMarkKind.Warning, _ => StripeMarkKind.Error }, RowOf(line), 1, marker.Range.Start,
                marker.Message, Line: line);
            if (kind == 0)
            {
                Mark(ref count, mark);
            }
            else
            {
                (kind == 1 ? warningMarks : errorMarks).Add(mark);
            }
        }
        foreach (StripeMark mark in warningMarks)
        {
            Mark(ref count, mark);
        }
        foreach (StripeMark mark in errorMarks)
        {
            Mark(ref count, mark);
        }
        warningMarks.Clear();
        errorMarks.Clear();
    }

    /// <summary>What a mark says: a problem's, the messages of its kind on its line, each once, in order; read as the pointer
    /// rests on it rather than for every mark.</summary>
    private string Said(StripeMark mark)
    {
        TextSnapshot text = document.Snapshot;
        if (ProblemKind(mark.Kind) is not { } kind || mark.Line < 0 || mark.Line >= text.LineCount)
        {
            return mark.Message;
        }
        MarkerList markers = Markers;
        int from = FirstStartingAt(markers, text.GetLineStart(mark.Line));
        string? first = null;
        List<string>? all = null;
        for (int i = from; i < markers.Count; i++)
        {
            Marker marker = markers[i];
            if (text.GetLineFromOffset(Math.Min(marker.Range.Start, text.Length)) != mark.Line)
            {
                break;
            }
            if (marker.Kind != kind)
            {
                continue;
            }
            if (first is null)
            {
                first = marker.Message;
            }
            else if (marker.Message != first && all?.Contains(marker.Message) != true)
            {
                (all ??= [first]).Add(marker.Message);
            }
        }
        return all is null ? first ?? mark.Message : string.Join('\n', all);
    }

    /// <summary>The kind of problem a mark stands for, or null for a mark of another kind.</summary>
    private static MarkerKind? ProblemKind(StripeMarkKind kind) => kind switch
    {
        StripeMarkKind.Error => MarkerKind.Error,
        StripeMarkKind.Warning => MarkerKind.Warning,
        StripeMarkKind.Info => MarkerKind.Info,
        _ => null,
    };

    /// <summary>The place of the first marker starting at an offset or after it, by halving.</summary>
    private static int FirstStartingAt(MarkerList markers, int offset)
    {
        int low = 0;
        int high = markers.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (markers[middle].Range.Start < offset)
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

    /// <summary>Whether every mark of a build stands on the row it stood on: the same lines folded away and, wrapped, every
    /// line on as many rows as it was, a match or a use on a line whose rows break elsewhere still on its row.</summary>
    private bool SameRows(StripeBuild kept, RowMap now, TextSnapshot text, IReadOnlyList<TextRange> matches, IReadOnlyList<UsageHighlight> uses)
    {
        RowMap was = kept.Rows;
        if (ReferenceEquals(was, now))
        {
            return true;
        }
        if (was.Count != now.Count || (was.Wraps is null) != (now.Wraps is null) || !was.Folds.Hidden.AsSpan().SequenceEqual(now.Folds.Hidden))
        {
            return false;
        }
        if (now.Wraps is null)
        {
            return true;
        }
        if (kept.RowShape != rowShape)
        {
            return false;
        }
        if (rebroken.Count == 0 || (matches.Count == 0 && uses.Count == 0))
        {
            return true;
        }
        Span<(int Start, int End)> lines = stackalloc (int, int)[rebroken.Count];
        for (int k = 0; k < rebroken.Count; k++)
        {
            LineSpan line = text.GetLine(rebroken[k]);
            lines[k] = (line.Start, line.End);
        }
        for (int i = 0; i < matches.Count; i++)
        {
            if (!Stays(text, lines, kept.Marks[kept.MatchesAt + i], matches[i].Start))
            {
                return false;
            }
        }
        for (int i = 0; i < uses.Count; i++)
        {
            if (!Stays(text, lines, kept.Marks[kept.UsesAt + i], uses[i].Range.Start))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a match's or a use's mark still stands on its row: off the lines whose rows break elsewhere, it does.</summary>
    private bool Stays(TextSnapshot text, ReadOnlySpan<(int Start, int End)> lines, StripeMark mark, int offset)
    {
        int at = Math.Min(offset, text.Length);
        foreach ((int start, int end) in lines)
        {
            if (at >= start && at <= end)
            {
                return RowOfOffset(text, at, beforeWrap: false).Row == mark.Row;
            }
        }
        return true;
    }

    /// <summary>Where a click on a mark goes in the text as it stands: a problem's mark to the first such problem on its line,
    /// a line's (a change, a bookmark) to the line's start, as a mark kept through keys has the line it stood on; a match's
    /// or a use's to where that one is now.</summary>
    private int OffsetOf(StripeMark mark)
    {
        TextSnapshot text = document.Snapshot;
        if (mark.Kind == StripeMarkKind.SearchMatch && mark.Item >= 0 && mark.Item < Highlights.Count)
        {
            return Math.Min(Highlights[mark.Item].Start, text.Length);
        }
        if (mark.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage && mark.Item >= 0 && mark.Item < UsageHighlights.Count)
        {
            return Math.Min(UsageHighlights[mark.Item].Range.Start, text.Length);
        }
        if (mark.Line < 0 || mark.Line >= text.LineCount)
        {
            return Math.Min(mark.Offset, text.Length);
        }
        LineSpan line = text.GetLine(mark.Line);
        if (ProblemKind(mark.Kind) is { } problem)
        {
            MarkerList markers = Markers;
            for (int i = FirstStartingAt(markers, line.Start); i < markers.Count && markers[i].Range.Start <= line.End; i++)
            {
                if (markers[i].Kind == problem)
                {
                    return markers[i].Range.Start;
                }
            }
        }
        return line.Start;
    }

    /// <summary>The caret's mark, apart from the others so that moving the caret costs that one mark, not every mark.</summary>
    private StripeMark CaretMark()
    {
        int caret = selections.Primary.Caret;
        return new StripeMark(StripeMarkKind.Caret, RowOfOffset(document.Snapshot, caret, LeansBack(caret)).Row, 1, caret, "Caret");
    }

    /// <summary>The scrollbar's track, where the marks go: between its arrows, in this control's coordinates.</summary>
    private Rect TrackRect()
    {
        Rect bar = vertical.Bounds;
        if (BarTrack() is { } track && track.TranslatePoint(default, this) is { } origin)
        {
            return new Rect(bar.X, origin.Y, bar.Width, track.Bounds.Height);
        }
        return bar;
    }

    private Track? BarTrack() => vertical.GetVisualDescendants().OfType<Track>().FirstOrDefault();

    private StripeBuild? stripeBuild;
    private (int Built, int CaretRow, (int, int) Counts) stripeDrawn;
    private bool stripeRefreshPosted;

    /// <summary>The stripe and the counts are drawn apart from the text: when what they show moved, they are asked again
    /// once the frame is done (invalidating a visual while the editor renders leaves the window's hit testing stale).</summary>
    private void RefreshStripe()
    {
        if (!showErrorStripe || stripeRefreshPosted)
        {
            return;
        }
        (int Built, int CaretRow, (int, int) Counts) now = (BuiltMarks().Number, CaretMark().Row, ProblemCount);
        // A build kept through a key shares its marks: the stripe is drawn again only for other marks or another caret row.
        if (now == stripeDrawn)
        {
            return;
        }
        bool recount = now.Counts != stripeDrawn.Counts;
        stripeDrawn = now;
        stripeRefreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            stripeRefreshPosted = false;
            if (recount)
            {
                counts.InvalidateMeasure();
                InvalidateArrange();
            }
            stripe.InvalidateVisual();
            counts.InvalidateVisual();
        });
    }

    private void ArrangeStripe(Rect bar)
    {
        stripe.Arrange(bar);
        counts.Measure(Size.Infinity);
        Size size = counts.DesiredSize;
        Rect counted = new(bar.X - size.Width - 6, 4, size.Width, size.Height);
        counts.Arrange(counted);
        ArrangeContext(counted);
    }

    /// <summary>A mark on the stripe: what it is, the rows it covers, the offset it stood for as it was worked out, what it
    /// says, the line it is of (none for a match's or a use's) and the match or use it is (none for the rest), by which a
    /// click finds its place as the text stands.</summary>
    public readonly record struct StripeMark(StripeMarkKind Kind, int Row, int Rows, int Offset, string Message, ChangeMarkKind Change = ChangeMarkKind.Modified,
        int Line = -1, int Item = -1);

    /// <summary>The stripe's marks as worked out from one text, its rows (the folds and the wraps) and the lists it shows,
    /// each kept by reference: a snapshot never changes, and the editor's lists and row maps are replaced, never changed in
    /// place; how many times the matches and uses had been given; the first <see cref="Count"/> of <see cref="Marks"/>; the
    /// counts of line-moving changes and of row-moving wraps it was worked out at, which build it is, and where among its
    /// marks the matches' and the uses' begin.</summary>
    private sealed record StripeBuild(TextSnapshot Text, MarkerList Markers, IReadOnlyList<TextRange> Highlights,
        IReadOnlyList<UsageHighlight> Usages, RowMap Rows, IReadOnlyList<int> Bookmarks, IReadOnlyList<ChangeMark> Changes, int Matched, int Used,
        StripeMark[] Marks, int Count, int RowCount, int Shape, int Number, int RowShape, int MatchesAt, int UsesAt)
    {
        public bool Shows(TextSnapshot text, MarkerList markers, IReadOnlyList<TextRange> highlights, IReadOnlyList<UsageHighlight> usages,
            RowMap rows, IReadOnlyList<int> bookmarks, IReadOnlyList<ChangeMark> changes) =>
            ReferenceEquals(Text, text) && ReferenceEquals(Markers, markers) && ReferenceEquals(Highlights, highlights) && ReferenceEquals(Usages, usages)
            && ReferenceEquals(Rows, rows) && ReferenceEquals(Bookmarks, bookmarks) && ReferenceEquals(Changes, changes);
    }

    /// <summary>The marks drawn over the scrollbar's track; hit only where a mark is drawn, so the thumb and the track take
    /// every other press, beside a change's strip too. A press on a mark goes to its line, but over the thumb one that
    /// moves drags the thumb instead.</summary>
    private sealed class ErrorStripe(CodeEditor editor) : Control, ICustomHitTest
    {
        private const double Reach = 3;

        /// <summary>The build the spans were placed for (its number), the track then, and where each mark is on it, in a
        /// list whose length only grows.</summary>
        private (int Built, int RowCount, Rect Track) placed = (-1, 0, default);
        private (double Top, double Height)[] spans = [];

        public bool HitTest(Point point) => At(point) is not null;

        public override void Render(DrawingContext context)
        {
            Palette palette = editor.palette;
            (StripeBuild built, Rect track) = Placed();
            for (int i = 0; i < built.Count; i++)
            {
                Draw(context, palette, built.Marks[i], spans[i], track);
            }
            StripeMark caret = editor.CaretMark();
            Draw(context, palette, caret, Span(caret, track, built.RowCount), track);
        }

        /// <summary>The marks and where each is on the track, in this control's coordinates: both what is drawn and what a
        /// pointer hits, worked out again only for new marks or a moved track.</summary>
        private (StripeBuild Marks, Rect Track) Placed()
        {
            StripeBuild built = editor.BuiltMarks();
            Rect track = editor.TrackRect().Translate(new Vector(-Bounds.X, -Bounds.Y));
            if (placed != (built.Number, built.RowCount, track))
            {
                if (spans.Length < built.Count)
                {
                    spans = new (double, double)[Math.Max(built.Count, spans.Length * 2)];
                }
                for (int i = 0; i < built.Count; i++)
                {
                    spans[i] = Span(built.Marks[i], track, built.RowCount);
                }
                placed = (built.Number, built.RowCount, track);
            }
            return (built, track);
        }

        private static void Draw(DrawingContext context, Palette palette, StripeMark mark, (double Top, double Height) span, Rect track)
        {
            IBrush brush = mark.Kind switch
            {
                StripeMarkKind.Error => palette.Error,
                StripeMarkKind.Warning => palette.Warning,
                StripeMarkKind.Info => palette.Info,
                StripeMarkKind.SearchMatch => palette.SearchCurrent,
                StripeMarkKind.Usage => palette.UsageReadMark,
                StripeMarkKind.WriteUsage => palette.UsageWriteMark,
                StripeMarkKind.Bookmark => palette.Bookmark,
                StripeMarkKind.Change => mark.Change switch { ChangeMarkKind.Added => palette.ChangeAdded, ChangeMarkKind.Deleted => palette.ChangeDeleted, ChangeMarkKind.Conflict => palette.ChangeConflict, _ => palette.ChangeModified },
                _ => palette.LineNumberCurrent,
            };
            context.FillRectangle(brush, Drawn(mark, span, track));
        }

        /// <summary>What a mark fills across the track at its span: a problem, a match or a use most of its breadth, a
        /// bookmark a dot at its right, a change a strip down its left edge.</summary>
        public static Rect Drawn(StripeMark mark, (double Top, double Height) span, Rect track)
        {
            (double left, double width) = mark.Kind switch
            {
                StripeMarkKind.Error or StripeMarkKind.Warning => (2.0, track.Width - 4),
                StripeMarkKind.Info => (5.0, track.Width - 10),
                StripeMarkKind.SearchMatch or StripeMarkKind.Usage or StripeMarkKind.WriteUsage => (3.0, track.Width - 6),
                StripeMarkKind.Bookmark => (track.Width - 4, 3.0),
                StripeMarkKind.Change => (0.0, 2.0),
                _ => (1.0, track.Width - 2),
            };
            return new Rect(track.X + left, span.Top, Math.Max(1, width), span.Height);
        }

        /// <summary>A left press on a mark over the thumb, with where it went down and the bar's value then: a click on the
        /// mark until the pointer moves past <see cref="Reach"/>, and from then the thumb's drag.</summary>
        private (StripeMark Mark, double Y, double Value, bool Dragging)? held;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point point = e.GetPosition(this);
            if (held is not { } press)
            {
                ToolTip.SetTip(this, At(point) is { } mark ? editor.Said(mark) : null);
                return;
            }
            if ((press.Dragging || Math.Abs(point.Y - press.Y) > Reach) && editor.BarTrack() is { } track)
            {
                held = press with { Dragging = true };
                ScrollBar bar = editor.vertical;
                bar.Value = Math.Clamp(press.Value + track.ValueFromDistance(0, point.Y - press.Y), bar.Minimum, bar.Maximum);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Point point = e.GetPosition(this);
            if (At(point) is not { } mark)
            {
                return;
            }
            e.Handled = true;
            if (OnThumb(point) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                ToolTip.SetTip(this, null);
                held = (mark, point.Y, editor.vertical.Value, false);
                return;
            }
            GoTo(mark);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (held is { Dragging: false } press)
            {
                GoTo(press.Mark);
            }
            held = null;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            held = null;
        }

        private void GoTo(StripeMark mark)
        {
            editor.GoTo(editor.document.Snapshot.GetPosition(editor.OffsetOf(mark)));
            editor.Focus();
        }

        /// <summary>Where a mark is drawn on the track: its row's place in the file, a tick three pixels high (a suggestion's
        /// two, the caret's a line), a change as tall as its lines.</summary>
        public static (double Top, double Height) Span(StripeMark mark, Rect track, int rowCount)
        {
            double rows = Math.Max(1, rowCount);
            double top = Math.Round(track.Top + (mark.Row / rows * track.Height));
            double height = mark.Kind switch
            {
                StripeMarkKind.Caret => 1,
                StripeMarkKind.Change => Math.Max(2, mark.Rows / rows * track.Height),
                StripeMarkKind.Info => 2,
                _ => 3,
            };
            return (top, height);
        }

        /// <summary>The mark drawn under a point, give or take <see cref="Reach"/>, the one drawn over the others first; the
        /// caret's is not one to take. A custom hit test is asked beyond the control's bounds, so a point beside the stripe
        /// (the gutter's change bar at a mark's height) is none of its.</summary>
        private StripeMark? At(Point point)
        {
            if (!IsVisible || point.X < 0 || point.X >= Bounds.Width)
            {
                return null;
            }
            (StripeBuild built, Rect track) = Placed();
            for (int i = built.Count - 1; i >= 0; i--)
            {
                if (Drawn(built.Marks[i], spans[i], track).Inflate(Reach).Contains(point))
                {
                    return built.Marks[i];
                }
            }
            return null;
        }

        private bool OnThumb(Point point) =>
            editor.BarTrack()?.Thumb is { IsEffectivelyVisible: true } thumb && thumb.TranslatePoint(default, this) is { } origin
            && new Rect(origin, thumb.Bounds.Size).Contains(point);
    }

    /// <summary>The file's error and warning counts, or a green tick with none, over the stripe's top.</summary>
    private sealed class ProblemCounts : Control
    {
        private readonly CodeEditor editor;

        static ProblemCounts()
        {
            AffectsMeasure<ProblemCounts>(TextElement.FontFamilyProperty);
            AffectsRender<ProblemCounts>(TextElement.FontFamilyProperty);
        }

        public ProblemCounts(CodeEditor editor)
        {
            this.editor = editor;
            Cursor = HandCursor;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            (int errors, int warnings) = measured = editor.ProblemCount;
            double width = 4;
            for (int which = 0; which < 2; which++)
            {
                int count = which == 0 ? errors : warnings;
                if (count > 0)
                {
                    width += 14 + 3 + Label(which, count, editor.palette.Text).Width + 6;
                }
            }
            return new Size(errors + warnings == 0 ? 20 : width, 18);
        }

        public override void Render(DrawingContext context)
        {
            (int errors, int warnings) = editor.ProblemCount;
            Palette palette = editor.palette;
            context.FillRectangle(palette.Background, new Rect(Bounds.Size), 4);
            if (errors + warnings == 0)
            {
                Icon(context, palette.PassedIcon, palette.Passed, new Point(3, 2));
                return;
            }
            double x = 4;
            for (int which = 0; which < 2; which++)
            {
                int count = which == 0 ? errors : warnings;
                if (count == 0)
                {
                    continue;
                }
                Icon(context, which == 0 ? palette.ErrorIcon : palette.WarningIcon, which == 0 ? palette.Error : palette.Warning, new Point(x, 2));
                x += 17;
                FormattedText label = Label(which, count, palette.Text);
                context.DrawText(label, new Point(x, (Bounds.Height - label.Height) / 2));
                x += label.Width + 6;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            editor.GoToNextProblem();
            editor.Focus();
            editor.ProblemsRequested?.Invoke(editor, EventArgs.Empty);
            e.Handled = true;
        }

        /// <summary>An icon 14 pixels square; a theme without it gets a dot in its colour.</summary>
        private static void Icon(DrawingContext context, Geometry? icon, IBrush brush, Point at)
        {
            if (icon is null)
            {
                context.DrawEllipse(brush, null, at + new Vector(7, 7), 5, 5);
                return;
            }
            Rect bounds = icon.Bounds;
            double scale = 14 / Math.Max(bounds.Width, bounds.Height);
            using (context.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
            {
                context.DrawGeometry(brush, null, icon);
            }
        }

        /// <summary>The errors' and the warnings' counts as last laid out, each made again only for another count, colour or
        /// font.</summary>
        private readonly (int Count, IBrush? Brush, FontFamily? Font, FormattedText? Text)[] labels = new (int, IBrush?, FontFamily?, FormattedText?)[2];

        /// <summary>The counts last measured.</summary>
        private (int Errors, int Warnings) measured;

        /// <summary>Whether the counts differ from those last measured, which are measured again only then.</summary>
        public bool Stale => editor.ProblemCount != measured;

        /// <summary>A count (<paramref name="which"/> 0 the errors', 1 the warnings') in the window's own font, which the
        /// editor's code font is not.</summary>
        private FormattedText Label(int which, int count, IBrush brush)
        {
            FontFamily font = TextElement.GetFontFamily(this);
            (int Count, IBrush? Brush, FontFamily? Font, FormattedText? Text) label = labels[which];
            if (label.Text is { } made && label.Count == count && ReferenceEquals(label.Brush, brush) && Equals(label.Font, font))
            {
                return made;
            }
            FormattedText text = new(count.ToString(System.Globalization.CultureInfo.CurrentCulture), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(font), 11.5, brush);
            labels[which] = (count, brush, font, text);
            return text;
        }
    }
}

/// <summary>What a mark on the error stripe stands for.</summary>
public enum StripeMarkKind
{
    Change,
    Bookmark,
    SearchMatch,
    Usage,
    WriteUsage,
    Info,
    Warning,
    Error,
    Caret,
}
