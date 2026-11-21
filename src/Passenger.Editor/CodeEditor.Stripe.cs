using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The error stripe: the vertical scrollbar's track stays and carries a mark for each problem (errors over
/// warnings over the rest), changed lines, search matches, the uses of the symbol at the caret, bookmarks and the
/// caret, each where its row is on the thumb's scale (<see cref="StripeScale"/>; a folded line at its fold, a wrapped
/// one's at its own row). A mark says what it is on hover and takes the caret there on a click.</summary>
public sealed partial class CodeEditor
{
    private readonly ErrorStripe stripe;
    private bool showErrorStripe = true;

    /// <summary>Whether the scrollbar carries the error stripe (a console's and a preview's go without).</summary>
    public bool ShowErrorStripe
    {
        get => showErrorStripe;
        set
        {
            showErrorStripe = value;
            vertical.AllowAutoHide = !value;
            vertical.Classes.Set(StyleClasses.Stripe, value);
            stripe.IsVisible = value;
            InvalidateArrange();
            Redraw();
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

    /// <summary>How many rectangles the stripe's marks were drawn as the last time it was drawn, its caret's line aside: what
    /// the compositor draws again in every frame that scrolls.</summary>
    public int StripeRectangles { get; private set; }

    /// <summary>How many times the stripe has been drawn.</summary>
    public long StripesDrawn { get; private set; }

    /// <summary>The file's errors and warnings, counted when the markers are set.</summary>
    public (int Errors, int Warnings) ProblemCount => decorations.Problems;

    /// <summary>The scrollbar's track the stripe's marks are drawn on, in this control's coordinates.</summary>
    public Rect StripeTrack => TrackRect();

    /// <summary>The middle of where a stripe mark is drawn, in this control's coordinates: what a click on it hits.</summary>
    public Point StripePoint(StripeMark mark)
    {
        Rect track = TrackRect();
        return ErrorStripe.Drawn(mark, ErrorStripe.Span(mark, track, Scale()), track).Center;
    }

    /// <summary>Goes to the next error or warning after the caret, wrapping.</summary>
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
    private void Rewrapped(ReadOnlySpan<int> before, ReadOnlySpan<int> after, int line)
    {
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
            Mark(ref count, new StripeMark(StripeMarkKind.SearchMatch, RowNear(text, Math.Min(at, text.Length)), 1, at, "Search result", Item: i));
        }
        int usesAt = count;
        for (int i = 0; i < uses.Count; i++)
        {
            UsageHighlight use = uses[i];
            Mark(ref count, new StripeMark(use.IsWrite ? StripeMarkKind.WriteUsage : StripeMarkKind.Usage, RowNear(text, Math.Min(use.Range.Start, text.Length)), 1,
                use.Range.Start, use.ByText ? "Same word" : use.IsWrite ? "Write usage" : "Read usage", Item: i));
        }
        MarkProblems(ref count, text, markers);
        StripeMarksBuilt++;
        stripeBuild = new StripeBuild(text, markers, matches, uses, rows, Bookmarks, ChangeMarks, highlights.Given, usages.Given,
            stripeMarks, count, lineShape, StripeMarksBuilt, rowShape, matchesAt, usesAt);
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
    private static int FirstStartingAt(MarkerList markers, int offset) => Sorted.FirstAtOrAfter(markers, static m => m.Range.Start, offset);

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
                return RowNear(text, at) == mark.Row;
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
        return new StripeMark(StripeMarkKind.Caret, RowNear(document.Snapshot, caret, LeansBack(caret)), 1, caret, "Caret");
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

    /// <summary>The scrollbar's own measures, which place its thumb: how far it scrolls, the height it shows, a row's height
    /// and the thumb's length as laid out (none while it is hidden).</summary>
    private StripeScale Scale() => new(vertical.Maximum, vertical.ViewportSize, metrics.LineHeight,
        BarTrack()?.Thumb is { IsVisible: true, Bounds.Height: > 0 } thumb ? thumb.Bounds.Height : 0);

    /// <summary>Where the thumb stands for a place in the text: a row's mark lies where the thumb's middle is when that row
    /// stands in the middle of the view, so a row on screen has its mark on the thumb as far down it as the row is down the
    /// view, the thumb's least length aside.</summary>
    internal readonly record struct StripeScale(double Maximum, double Viewport, double RowHeight, double Thumb)
    {
        /// <summary>How far down a track of a height the thumb's place for a height into the text is, kept on the track.</summary>
        public double At(double y, double track)
        {
            double extent = Maximum + Viewport;
            double at = Maximum > 0 && Thumb > 0 && Thumb < track
                ? (Thumb / 2) + ((y - (Viewport / 2)) * (track - Thumb) / Maximum)
                : y * track / Math.Max(extent, RowHeight);
            return Math.Clamp(at, 0, track);
        }
    }

    private StripeBuild? stripeBuild;
    /// <summary>What the stripe was last asked to draw: the marks' build and where the caret's mark stands on the track;
    /// default once a new look or document has it drawn again whole.</summary>
    private (int Built, double CaretTop) stripeDrawn;
    private bool stripeRefreshPosted;

    /// <summary>The stripe is drawn apart from the text: when what it shows moved, it is asked again once the frame is done
    /// (invalidating a visual while the editor renders leaves the window's hit testing stale), only for other marks or its
    /// caret's mark moved, which a file of more rows than the track has pixels leaves where it was for a row or two.</summary>
    private void RefreshStripe()
    {
        if (!showErrorStripe || stripeRefreshPosted)
        {
            return;
        }
        StripeBuild built = BuiltMarks();
        (int Built, double CaretTop) now = (built.Number, stripe.CaretTop(CaretMark()));
        // A build kept through a key shares its marks: the stripe is drawn again only for other marks or its caret's moved.
        if (now == stripeDrawn)
        {
            return;
        }
        stripeDrawn = now;
        stripeRefreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            stripeRefreshPosted = false;
            stripe.InvalidateVisual();
        });
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
        StripeMark[] Marks, int Count, int Shape, int Number, int RowShape, int MatchesAt, int UsesAt)
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
        private (int Built, StripeScale Scale, Rect Track) placed = (-1, default, default);
        private (double Top, double Height)[] spans = [];

        public bool HitTest(Point point) => At(point) is not null;

        /// <summary>Where the caret's mark is drawn on the track as last placed; before the stripe was first drawn, nowhere.</summary>
        public double CaretTop(StripeMark caret) =>
            placed.Built < 0 ? double.NegativeInfinity : Span(caret, placed.Track, placed.Scale).Top;

        /// <summary>The marks in order, those next to each other drawn alike and touching as one rectangle, so thousands of
        /// problems are as many rectangles as the track has room for: what the compositor draws again in every frame.</summary>
        public override void Render(DrawingContext context)
        {
            editor.StripesDrawn++;
            Palette palette = editor.palette;
            (StripeBuild built, Rect track, StripeScale scale) = Placed();
            IBrush? brush = null;
            Rect run = default;
            int drawn = 0;
            for (int i = 0; i < built.Count; i++)
            {
                IBrush next = BrushOf(palette, built.Marks[i]);
                Rect mark = Drawn(built.Marks[i], spans[i], track);
                if (built.Marks[i] is { Kind: StripeMarkKind.Change } change && ChangeShapes.Of(change.Change) is ChangeShape.Dashes or ChangeShape.Rails)
                {
                    if (brush is not null)
                    {
                        context.FillRectangle(brush, run);
                        drawn++;
                        brush = null;
                    }
                    drawn += DrawBroken(context, next, ChangeShapes.Of(change.Change), mark);
                    continue;
                }
                if (ReferenceEquals(next, brush) && mark.X == run.X && mark.Width == run.Width && mark.Top <= run.Bottom && mark.Bottom >= run.Top)
                {
                    run = run.Union(mark);
                    continue;
                }
                if (brush is not null)
                {
                    context.FillRectangle(brush, run);
                    drawn++;
                }
                (brush, run) = (next, mark);
            }
            if (brush is not null)
            {
                context.FillRectangle(brush, run);
                drawn++;
            }
            editor.StripeRectangles = drawn;
            StripeMark caret = editor.CaretMark();
            context.FillRectangle(BrushOf(palette, caret), Drawn(caret, Span(caret, track, scale), track));
        }

        /// <summary>A change's strip in its shape as the gutter draws it: dashes from its top, or two rails; how many
        /// rectangles that took.</summary>
        private static int DrawBroken(DrawingContext context, IBrush brush, ChangeShape shape, Rect strip)
        {
            if (shape == ChangeShape.Rails)
            {
                context.FillRectangle(brush, strip.WithWidth(1));
                context.FillRectangle(brush, strip.WithX(strip.Right - 1).WithWidth(1));
                return 2;
            }
            int drawn = 0;
            for (double y = strip.Top; y < strip.Bottom; y += 3)
            {
                context.FillRectangle(brush, new Rect(strip.X, y, strip.Width, Math.Min(2, strip.Bottom - y)));
                drawn++;
            }
            return drawn;
        }

        /// <summary>The marks and where each is on the track, in this control's coordinates: both what is drawn and what a
        /// pointer hits, worked out again only for new marks or a moved track.</summary>
        private (StripeBuild Marks, Rect Track, StripeScale Scale) Placed()
        {
            StripeBuild built = editor.BuiltMarks();
            Rect track = editor.TrackRect().Translate(new Vector(-Bounds.X, -Bounds.Y));
            StripeScale scale = editor.Scale();
            if (placed != (built.Number, scale, track))
            {
                if (spans.Length < built.Count)
                {
                    spans = new (double, double)[Math.Max(built.Count, spans.Length * 2)];
                }
                for (int i = 0; i < built.Count; i++)
                {
                    spans[i] = Span(built.Marks[i], track, scale);
                }
                placed = (built.Number, scale, track);
            }
            return (built, track, scale);
        }

        private static IBrush BrushOf(Palette palette, StripeMark mark) =>
            mark.Kind switch
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

        /// <summary>What a mark fills across the track at its span: an error, a match or a use most of its breadth, a
        /// warning its right half and a suggestion its right quarter, so each is told by its breadth as well as its colour, a
        /// bookmark a dot at its right, a change a strip down its left edge (drawn in its shape, <see cref="ChangeShapes"/>:
        /// a conflict's rails four wide, a deletion a tick five wide).</summary>
        public static Rect Drawn(StripeMark mark, (double Top, double Height) span, Rect track)
        {
            (double left, double width) = mark.Kind switch
            {
                StripeMarkKind.Error => (2.0, track.Width - 4),
                StripeMarkKind.Warning => (track.Width / 2, (track.Width / 2) - 2),
                StripeMarkKind.Info => (track.Width * 3 / 4, (track.Width / 4) - 2),
                StripeMarkKind.SearchMatch or StripeMarkKind.Usage or StripeMarkKind.WriteUsage => (3.0, track.Width - 6),
                StripeMarkKind.Bookmark => (track.Width - 4, 3.0),
                StripeMarkKind.Change => ChangeShapes.Of(mark.Change) switch
                {
                    ChangeShape.Wedge => (0.0, 5.0),
                    ChangeShape.Rails => (0.0, 4.0),
                    _ => (0.0, 2.0),
                },
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

        /// <summary>Where a mark is drawn on the track, on the thumb's scale: a tick three pixels high about its row's middle,
        /// a suggestion's two and the caret's a line from the same top, so the caret's line on a problem's row leaves the
        /// problem's middle and foot in its colour; a change from its first row's top to its last row's foot, four high at
        /// least, a deletion a tick two high at its place.</summary>
        public static (double Top, double Height) Span(StripeMark mark, Rect track, StripeScale scale)
        {
            double row = scale.RowHeight;
            if (mark.Kind == StripeMarkKind.Change)
            {
                double first = scale.At(mark.Row * row, track.Height);
                double foot = scale.At((mark.Row + mark.Rows) * row, track.Height);
                // A deletion a tick between lines; the rest tall enough for a dash and its gap to show.
                return (Math.Round(track.Top + first), ChangeShapes.Of(mark.Change) == ChangeShape.Wedge ? 2 : Math.Max(4, foot - first));
            }
            double top = Math.Round(track.Top + scale.At((mark.Row + 0.5) * row, track.Height) - (Tick / 2));
            return (top, mark.Kind switch
            {
                StripeMarkKind.Caret => 1,
                StripeMarkKind.Info => 2,
                _ => Tick,
            });
        }

        /// <summary>How high a problem's, a match's or a use's tick is.</summary>
        private const double Tick = 3;

        /// <summary>The mark drawn under a point, give or take <see cref="Reach"/>, the one drawn over the others first; the
        /// caret's is not one to take. A custom hit test is asked beyond the control's bounds, so a point beside the stripe
        /// (the gutter's change bar at a mark's height) is none of its.</summary>
        private StripeMark? At(Point point)
        {
            if (!IsVisible || point.X < 0 || point.X >= Bounds.Width)
            {
                return null;
            }
            (StripeBuild built, Rect track, _) = Placed();
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
