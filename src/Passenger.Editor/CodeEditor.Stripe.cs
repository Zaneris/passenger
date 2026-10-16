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

/// <summary>Rider's error stripe: the vertical scrollbar's rail stays and carries a mark for each problem (errors over
/// warnings over the rest), changed lines, search matches, the uses of the symbol at the caret, bookmarks and the caret,
/// each where its line is in the file
/// (a folded one at its fold). A mark says what it is on hover and takes the caret there on a click. Over the stripe's
/// top, the file's counts of errors and warnings, or a green tick: a click goes to the next problem and asks the host
/// for Problems.</summary>
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
    public IReadOnlyList<StripeMark> StripeMarks => showErrorStripe ? [.. BuiltMarks().Marks, CaretMark()] : [];

    /// <summary>How many times the stripe's marks have been worked out: once for each change to the text, the markers,
    /// the search's matches, the uses highlighted, the folds, the bookmarks or the changed lines, never for a frame or a
    /// pointer move.</summary>
    public int StripeMarksBuilt { get; private set; }

    /// <summary>The counts' errors and warnings, as their widget shows them, counted when the markers are set.</summary>
    public (int Errors, int Warnings) ProblemCount { get; private set; }

    /// <summary>Where a stripe mark is drawn, in this control's coordinates: what a click on it hits.</summary>
    public Point StripePoint(StripeMark mark)
    {
        Rect track = TrackRect();
        (double top, double height) = ErrorStripe.Span(mark, track, RowCount);
        return new Point(track.Center.X, top + (height / 2));
    }

    private static (int Errors, int Warnings) CountProblems(IReadOnlyList<Marker> markers)
    {
        int errors = 0;
        int warnings = 0;
        foreach (Marker marker in markers)
        {
            errors += marker.Kind == MarkerKind.Error ? 1 : 0;
            warnings += marker.Kind == MarkerKind.Warning ? 1 : 0;
        }
        return (errors, warnings);
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

    /// <summary>The stripe's marks but the caret's, worked out again only when what they come from is replaced.</summary>
    private StripeBuild BuiltMarks()
    {
        TextSnapshot text = document.Snapshot;
        RowMap rows = Rows;
        if (stripeBuild is { } built && built.Shows(text, Markers, Highlights, UsageHighlights, rows, Bookmarks, ChangeMarks))
        {
            return built;
        }
        List<StripeMark> marks = [];
        foreach (ChangeMark change in ChangeMarks)
        {
            int line = Math.Min(change.Line, text.LineCount - 1);
            int last = Math.Min(line + Math.Max(1, change.Count) - 1, text.LineCount - 1);
            string what = change.Kind switch { ChangeMarkKind.Added => "Added lines", ChangeMarkKind.Deleted => "Deleted lines", ChangeMarkKind.Conflict => "Conflict", _ => "Modified lines" };
            marks.Add(new StripeMark(StripeMarkKind.Change, RowOf(line), Math.Max(1, RowOf(last) + rows.RowsOf(last) - RowOf(line)),
                text.GetLineStart(line), what, change.Kind));
        }
        foreach (int line in Bookmarks.Where(l => l < text.LineCount))
        {
            marks.Add(new StripeMark(StripeMarkKind.Bookmark, RowOf(line), 1, text.GetLineStart(line), "Bookmark"));
        }
        // A match or a use is marked at its own row of a wrapped line.
        foreach (TextRange match in Highlights)
        {
            marks.Add(new StripeMark(StripeMarkKind.SearchMatch, RowOfOffset(text, Math.Min(match.Start, text.Length), beforeWrap: false).Row, 1, match.Start, "Search result"));
        }
        foreach (UsageHighlight use in UsageHighlights)
        {
            marks.Add(new StripeMark(use.IsWrite ? StripeMarkKind.WriteUsage : StripeMarkKind.Usage, RowOfOffset(text, Math.Min(use.Range.Start, text.Length), beforeWrap: false).Row, 1,
                use.Range.Start, use.IsWrite ? "Write usage" : "Read usage"));
        }
        // The worst last, so it is drawn over the rest; one mark a line and kind, its messages together.
        foreach (MarkerKind kind in new[] { MarkerKind.Info, MarkerKind.Warning, MarkerKind.Error })
        {
            foreach (IGrouping<int, Marker> line in Markers.Where(m => m.Kind == kind).GroupBy(m => text.GetLineFromOffset(Math.Min(m.Range.Start, text.Length))))
            {
                StripeMarkKind markKind = kind switch { MarkerKind.Error => StripeMarkKind.Error, MarkerKind.Warning => StripeMarkKind.Warning, _ => StripeMarkKind.Info };
                marks.Add(new StripeMark(markKind, RowOf(line.Key), 1, line.First().Range.Start, string.Join('\n', line.Select(m => m.Message).Distinct())));
            }
        }
        StripeMarksBuilt++;
        stripeBuild = new StripeBuild(text, Markers, Highlights, UsageHighlights, rows, Bookmarks, ChangeMarks, [.. marks], rows.Count);
        return stripeBuild;
    }

    /// <summary>The caret's mark, apart from the others so that moving the caret costs that one mark, not every mark.</summary>
    private StripeMark CaretMark()
    {
        int caret = selections.Primary.Caret;
        return new StripeMark(StripeMarkKind.Caret, RowOfOffset(document.Snapshot, caret, LeansBack(caret)).Row, 1, caret, "Caret");
    }

    /// <summary>The scrollbar's track, where the marks go, including when a short file needs no scrolling thumb.</summary>
    private Rect TrackRect()
    {
        Rect bar = stripe.Bounds;
        if (vertical.IsVisible && vertical.GetVisualDescendants().OfType<Track>().FirstOrDefault() is { } track && track.TranslatePoint(default, this) is { } origin)
        {
            return new Rect(bar.X, origin.Y, bar.Width, track.Bounds.Height);
        }
        return bar;
    }

    private StripeBuild? stripeBuild;
    private (StripeBuild? Marks, int CaretRow, (int, int) Counts) stripeDrawn;
    private bool stripeRefreshPosted;

    /// <summary>The stripe and the counts are drawn apart from the text: when what they show moved, they are asked again
    /// once the frame is done (invalidating a visual while the editor renders leaves the window's hit testing stale).</summary>
    private void RefreshStripe()
    {
        if (!showErrorStripe || stripeRefreshPosted)
        {
            return;
        }
        (StripeBuild Marks, int CaretRow, (int, int) Counts) now = (BuiltMarks(), CaretMark().Row, ProblemCount);
        if (ReferenceEquals(now.Marks, stripeDrawn.Marks) && now.CaretRow == stripeDrawn.CaretRow)
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

    /// <summary>A mark on the stripe: what it is, the rows it covers, the offset a click goes to, and what it says.</summary>
    public readonly record struct StripeMark(StripeMarkKind Kind, int Row, int Rows, int Offset, string Message, ChangeMarkKind Change = ChangeMarkKind.Modified);

    /// <summary>The stripe's marks as worked out from one text, its rows (the folds and the wraps) and the lists it shows,
    /// each kept by reference: a snapshot never changes, and the editor's lists and row maps are replaced, never changed in
    /// place.</summary>
    private sealed record StripeBuild(TextSnapshot Text, IReadOnlyList<Marker> Markers, IReadOnlyList<TextRange> Highlights,
        IReadOnlyList<UsageHighlight> Usages, RowMap Rows, IReadOnlyList<int> Bookmarks, IReadOnlyList<ChangeMark> Changes,
        StripeMark[] Marks, int RowCount)
    {
        public bool Shows(TextSnapshot text, IReadOnlyList<Marker> markers, IReadOnlyList<TextRange> highlights, IReadOnlyList<UsageHighlight> usages,
            RowMap rows, IReadOnlyList<int> bookmarks, IReadOnlyList<ChangeMark> changes) =>
            ReferenceEquals(Text, text) && ReferenceEquals(Markers, markers) && ReferenceEquals(Highlights, highlights) && ReferenceEquals(Usages, usages)
            && ReferenceEquals(Rows, rows) && ReferenceEquals(Bookmarks, bookmarks) && ReferenceEquals(Changes, changes);
    }

    /// <summary>The marks drawn over the scrollbar's track; hit only where a mark is, so the thumb and the track take every
    /// other press.</summary>
    private sealed class ErrorStripe(CodeEditor editor) : Control, ICustomHitTest
    {
        private const double Reach = 3;

        private (StripeBuild? Marks, Rect Track, (double Top, double Height)[] Spans) placed = (null, default, []);

        public bool HitTest(Point point) => At(point) is not null;

        public override void Render(DrawingContext context)
        {
            Palette palette = editor.palette;
            (StripeBuild built, Rect track, (double Top, double Height)[] spans) = Placed();
            for (int i = 0; i < spans.Length; i++)
            {
                Draw(context, palette, built.Marks[i], spans[i], track);
            }
            StripeMark caret = editor.CaretMark();
            Draw(context, palette, caret, Span(caret, track, built.RowCount), track);
        }

        /// <summary>The marks and where each is on the track, in this control's coordinates: both what is drawn and what a
        /// pointer hits, worked out again only for new marks or a moved track.</summary>
        private (StripeBuild Marks, Rect Track, (double Top, double Height)[] Spans) Placed()
        {
            StripeBuild built = editor.BuiltMarks();
            Rect track = editor.TrackRect().Translate(new Vector(-Bounds.X, -Bounds.Y));
            if (!ReferenceEquals(built, placed.Marks) || track != placed.Track)
            {
                placed = (built, track, [.. built.Marks.Select(m => Span(m, track, built.RowCount))]);
            }
            return (built, track, placed.Spans);
        }

        private static void Draw(DrawingContext context, Palette palette, StripeMark mark, (double Top, double Height) span, Rect track)
        {
            (IBrush brush, double left, double width) = mark.Kind switch
            {
                StripeMarkKind.Error => (palette.Error, 2.0, track.Width - 4),
                StripeMarkKind.Warning => (palette.Warning, 2.0, track.Width - 4),
                StripeMarkKind.Info => (palette.Info, 5.0, track.Width - 10),
                StripeMarkKind.SearchMatch => (palette.SearchCurrent, 3.0, track.Width - 6),
                StripeMarkKind.Usage => (palette.UsageReadMark, 3.0, track.Width - 6),
                StripeMarkKind.WriteUsage => (palette.UsageWriteMark, 3.0, track.Width - 6),
                StripeMarkKind.Bookmark => (palette.Bookmark, track.Width - 4, 3.0),
                StripeMarkKind.Change => (mark.Change switch { ChangeMarkKind.Added => palette.ChangeAdded, ChangeMarkKind.Deleted => palette.ChangeDeleted, ChangeMarkKind.Conflict => palette.ChangeConflict, _ => palette.ChangeModified }, 0.0, 2.0),
                _ => (palette.LineNumberCurrent, 1.0, track.Width - 2),
            };
            context.FillRectangle(brush, new Rect(track.X + left, span.Top, Math.Max(1, width), span.Height));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            ToolTip.SetTip(this, At(e.GetPosition(this)) is { } mark ? mark.Message : null);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (At(e.GetPosition(this)) is { } mark)
            {
                editor.GoTo(editor.document.Snapshot.GetPosition(Math.Min(mark.Offset, editor.document.Snapshot.Length)));
                editor.Focus();
                e.Handled = true;
            }
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

        /// <summary>The mark under a point, the one drawn over the others first; the caret's is not one to take. A custom
        /// hit test is asked beyond the control's bounds, so a point beside the stripe (the gutter's change bar at a
        /// mark's height) is none of its.</summary>
        private StripeMark? At(Point point)
        {
            if (!IsVisible || point.X < 0 || point.X >= Bounds.Width)
            {
                return null;
            }
            (StripeBuild built, _, (double Top, double Height)[] spans) = Placed();
            for (int i = spans.Length - 1; i >= 0; i--)
            {
                if (point.Y >= spans[i].Top - Reach && point.Y <= spans[i].Top + spans[i].Height + Reach)
                {
                    return built.Marks[i];
                }
            }
            return null;
        }
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
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            (int errors, int warnings) = editor.ProblemCount;
            double width = 4;
            foreach (int count in new[] { errors, warnings }.Where(c => c > 0))
            {
                width += 14 + 3 + Label(count.ToString(), Brushes.Transparent).Width + 6;
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
                Icon(context, palette.PassedIcon, palette.TestPassed, new Point(3, 2));
                return;
            }
            double x = 4;
            foreach ((int count, Geometry? icon, IBrush brush) in new[] { (errors, palette.ErrorIcon, palette.Error), (warnings, palette.WarningIcon, palette.Warning) })
            {
                if (count == 0)
                {
                    continue;
                }
                Icon(context, icon, brush, new Point(x, 2));
                x += 17;
                FormattedText label = Label(count.ToString(), palette.Text);
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

        /// <summary>A count in the window's own font, which the editor's code font is not.</summary>
        private FormattedText Label(string text, IBrush brush) =>
            new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this)), 11.5, brush);
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
