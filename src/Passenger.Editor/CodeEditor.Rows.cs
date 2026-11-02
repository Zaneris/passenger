using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The rows on screen as visuals of their own, each recorded once and then moved by the compositor: a scroll
/// moves the layer that holds them and records only the rows come into view (<see cref="PrepareFrame"/>). Any other
/// change records every row again (<see cref="Redraw"/>). A row draws in the editor's x and its own y: its gutter clipped
/// to it, its text and what lies on it clipped to the text's side of the gutter; the carets, a snippet's fields, a diff's
/// rules and the fold preview are an overlay recorded again for each frame that moves or changes them.</summary>
public sealed partial class CodeEditor
{
    /// <summary>The layer the scroll moves, the rows standing in it where the text puts them.</summary>
    private readonly Layer rowsLayer = new();
    private readonly Painted overlay;

    private readonly List<RowSlot> slots = [];
    private readonly Dictionary<int, RowSlot> keptSlots = [];
    private readonly List<int> unplaced = [];
    private readonly Action<TimeSpan> prepareFrame;

    /// <summary>Whether a frame has been asked for, every row then drawn again, or the overlay.</summary>
    private bool framePending;
    private bool rowsStale = true;
    private bool overlayStale = true;

    /// <summary>The frames prepared, by which a row, a label and a shape say when they were last drawn.</summary>
    private long frameNumber;

    /// <summary>What the rows drawn last were drawn with, any of which changed draws them all again.</summary>
    private (TextSnapshot? Text, Size Size, double Gutter, double ScrollX, bool Focused, Palette? Palette, Metrics Metrics) drawnWith;

    /// <summary>What the frame being drawn shows: its rows' first and the offsets they span, which lines bear a caret, the
    /// tints, and the look of a focused or unfocused editor.</summary>
    private TextSnapshot? frameText;
    private int frameRow;
    private int frameStart;
    private int frameEnd;
    private bool frameNoneSelected;
    private bool frameFocused;
    private BracePair? frameBraced;
    private LineTints? frameMarked;
    private Dictionary<int, IBrush>? frameTints;
    private Size frameSize;
    private double frameGutter;

    /// <summary>The scroll the rows were last placed for.</summary>
    private double frameScrollY = double.NaN;

    /// <summary>The top of the gutter's row being drawn, in the text's rows: where the columns' frame measures a line from.</summary>
    private double gutterTop;

    /// <summary>How many rows the editor has drawn, each with its gutter: what a frame records.</summary>
    public long RowsDrawn { get; private set; }

    /// <summary>The rows' layer and the overlay, under the scroll bars and the stripe.</summary>
    private void AddLayers()
    {
        VisualChildren.Insert(0, rowsLayer);
        VisualChildren.Insert(1, overlay);
    }

    /// <summary>Asks the next frame to draw every row again: what a change to what they show needs.</summary>
    private void Redraw()
    {
        rowsStale = true;
        overlayStale = true;
        InvalidateVisual();
        RequestFrame();
    }

    /// <summary>Asks the next frame to draw the overlay again: the carets' blink, the fold preview.</summary>
    private void RedrawOverlay()
    {
        overlayStale = true;
        RequestFrame();
    }

    /// <summary>Asks the next frame to move the rows where the view now stands, drawing only those come into view.</summary>
    private void Scrolled()
    {
        overlayStale = true;
        RequestFrame();
    }

    /// <summary>A frame, prepared before the next is rendered; none while the editor is in no window, whose showing it
    /// asks for one (<see cref="OnAttachedToVisualTree"/>).</summary>
    private void RequestFrame()
    {
        if (framePending || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        framePending = true;
        top.RequestAnimationFrame(prepareFrame);
    }

    /// <summary>The rows on screen given visuals, those that show what they showed kept where they stand now, the rest
    /// drawn again or anew; the layers moved to the scroll, and what the frame's rows draw with worked out once.</summary>
    private void PrepareFrame()
    {
        framePending = false;
        WrapOnScreen();
        Size size = Bounds.Size;
        double gutter = GutterWidth;
        double lineHeight = metrics.LineHeight;
        TextSnapshot text = document.Snapshot;
        bool focused = IsFocused;
        (TextSnapshot?, Size, double, double, bool, Palette?, Metrics) drawing = (text, size, gutter, scroll.X, focused, palette, metrics);
        bool stale = rowsStale || drawing != drawnWith;
        bool moved = frameText is null || scroll.Y != frameScrollY;
        if (!stale && !moved)
        {
            // The carets' blink or the fold preview: the rows and what they were drawn with stand.
            if (overlayStale)
            {
                overlayStale = false;
                Frames++;
                overlay.Redraw();
            }
            return;
        }
        rowsStale = false;
        overlayStale = false;
        drawnWith = drawing;
        frameScrollY = scroll.Y;
        Frames++;
        int row = Math.Max(0, (int)(scroll.Y / lineHeight));
        long now = ++frameNumber;
        long keepFrom = now;
        foreach (RowSlot slot in slots)
        {
            if (slot.Span is not null)
            {
                keepFrom = Math.Min(keepFrom, slot.Drawn);
            }
        }
        labels.Begin(now, keepFrom);
        numbers.Begin(now, keepFrom);
        shapes.Begin(now, keepFrom);
        gutterLabels.Begin(now, keepFrom);
        gutterFrame?.Keep(now, keepFrom);

        List<RowSpan> shown = RowsOnScreen(text, row, Math.Max(0, (int)((scroll.Y + size.Height) / lineHeight)));
        int first = shown.Count > 0 ? shown[0].Line : 0;
        int last = shown.Count > 0 ? shown[^1].Line : -1;
        LineView[] lineViews = ViewsOf(text, shown, scroll.X - TextPadLeft, Math.Max(0, size.Width - gutter));
        frameText = text;
        frameRow = row;
        frameStart = shown.Count > 0 ? lineViews[0].Span.Start : 0;
        frameEnd = shown.Count > 0 ? lineViews[shown.Count - 1].Span.EndIncludingBreak : 0;
        CaretLines(text, frameStart, frameEnd);
        frameNoneSelected = NoneSelected();
        frameFocused = focused;
        frameBraced = MatchedBraces;
        frameMarked = TintsOf(first, last);
        frameTints = ChangeBackgrounds ? ChangeTints(first, last) : null;
        frameSize = size;
        frameGutter = gutter;
        double widest = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            widest = Math.Max(widest, shown[i].Indent + lineViews[i].Width + HintsWidth(GapsOn(shown[i])));
        }

        PlaceRows(shown, row, stale, now);
        rowsLayer.Place(new Rect(0, -scroll.Y, size.Width, size.Height));
        overlay.Place(new Rect(size));
        overlay.Redraw();

        views.EndFrame();
        // The widest line drawn since the text changed, so a long line deleted gives its width back, but the view stays.
        widestLine = widestStale ? Math.Max(widest, scroll.X + Viewport.Width - TextPadLeft - (metrics.CharWidth * 4)) : Math.Max(widestLine, widest);
        widestStale = false;
        if (widestLine != measuredWidth)
        {
            // The widest line drawn changed: the horizontal extent follows, outside the frame.
            measuredWidth = widestLine;
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateScrollBars);
        }
        RefreshStripe();
    }

    /// <summary>Gives each row shown a slot: the one that drew it, if all need not be drawn again, else a free one, which
    /// draws it; slots left over draw nothing.</summary>
    private void PlaceRows(List<RowSpan> shown, int firstRow, bool stale, long now)
    {
        keptSlots.Clear();
        foreach (RowSlot slot in slots)
        {
            if (!stale && slot.Span is { } span)
            {
                keptSlots[span.Start] = slot;
            }
            slot.Showing = false;
        }
        unplaced.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            if (keptSlots.TryGetValue(shown[i].Start, out RowSlot? kept) && kept.Span == shown[i])
            {
                kept.Showing = true;
                kept.Index = i;
                Place(kept, firstRow + i);
            }
            else
            {
                unplaced.Add(i);
            }
        }
        int next = 0;
        foreach (int i in unplaced)
        {
            while (next < slots.Count && slots[next].Showing)
            {
                next++;
            }
            if (next == slots.Count)
            {
                RowSlot added = new(this);
                slots.Add(added);
                rowsLayer.Add(added.Visual);
            }
            RowSlot slot = slots[next];
            slot.Showing = true;
            slot.Forget();
            slot.Span = shown[i];
            slot.Index = i;
            slot.Drawn = now;
            Place(slot, firstRow + i);
            slot.Visual.Redraw();
        }
        foreach (RowSlot slot in slots)
        {
            if (!slot.Showing && slot.Span is not null)
            {
                slot.Span = null;
                slot.Forget();
                slot.Visual.Redraw();
            }
        }
    }

    /// <summary>A slot's visual at a row, across the editor, the rows below drawn over those above as they would be in one
    /// pass down the screen.</summary>
    private void Place(RowSlot slot, int row)
    {
        slot.Row = row;
        if (slot.Visual.ZIndex != row)
        {
            slot.Visual.ZIndex = row;
        }
        slot.Visual.Place(new Rect(0, row * metrics.LineHeight, frameSize.Width, metrics.LineHeight));
    }

    /// <summary>A row the frame shows, its gutter clipped to it and its text to the text's side of the gutter, where a glyph
    /// reaching past the row is drawn over the row above or below it as it would be in one pass.</summary>
    private void DrawRow(DrawingContext context, RowSlot slot)
    {
        if (ShownSpan(slot) is not { } span)
        {
            return;
        }
        RowsDrawn++;
        slot.Drawn = frameNumber;
        slot.Forget();
        double lineHeight = metrics.LineHeight;
        double gutter = frameGutter;
        using (context.PushClip(new Rect(0, 0, gutter, lineHeight)))
        {
            DrawGutterRow(context, slot, span);
        }
        using (context.PushClip(new Rect(gutter, -lineHeight, Math.Max(0, frameSize.Width - gutter), 3 * lineHeight)))
        {
            DrawTextRow(context, slot, span);
        }
    }

    /// <summary>The slot's row as it stands in the frame shown, or null for one free or left behind by another frame.</summary>
    private RowSpan? ShownSpan(RowSlot slot) =>
        slot.Span is { } span && slot.Index < rowsShown.Count && rowsShown[slot.Index] == span && frameText is not null ? span : null;

    /// <summary>A row the frame shows and what lies on it, at the top of its own visual: behind the text the current
    /// line, the tints, changes, uses, braces, selections and matches; over it links and squiggles; after it the fold's
    /// placeholder and the annotations.</summary>
    private void DrawTextRow(DrawingContext context, RowSlot slot, RowSpan span)
    {
        TextSnapshot text = frameText!;
        double width = frameSize.Width;
        double gutter = frameGutter;
        double lineHeight = metrics.LineHeight;
        int line = span.Line;
        LineView view = frameViews[slot.Index];
        const double y = 0;
        double from = TextLeft - scroll.X + span.Indent;
        Gap[] gaps = GapsOn(span);

        if (frameNoneSelected && caretLines.Contains(line))
        {
            context.FillRectangle(palette.CurrentLine, new Rect(gutter, y, width - gutter, lineHeight));
        }
        if (frameTints is not null && frameTints.TryGetValue(line, out IBrush? change))
        {
            context.FillRectangle(change, new Rect(gutter, y, width - gutter, lineHeight));
        }
        DrawInlineChanges(context, view, gaps, from, y);
        if (frameMarked!.Of(line) is { } tint)
        {
            context.FillRectangle(tint, new Rect(gutter, y, width - gutter, lineHeight));
        }
        else
        {
            foreach ((int partLine, TextRange part, IBrush brush) in frameMarked.Parts)
            {
                if (partLine == line)
                {
                    FillRange(context, view, gaps, from, y, part, brush);
                }
            }
        }
        DrawUsages(context, view, gaps, from, y);
        if (frameBraced is { } pair)
        {
            DrawBraces(context, view, gaps, from, y, pair);
        }
        DrawSelections(context, view, gaps, from, y, width, frameFocused);
        DrawHighlights(context, view, gaps, from, y);
        DrawChoices(context, view, gaps, from, y);
        if (showWhitespaces)
        {
            DrawWhitespace(context, text, view, gaps, from, y);
        }
        if (gaps.Length == 0)
        {
            view.Draw(context, from, y + (lineHeight - view.Height) / 2);
        }
        else
        {
            DrawWithHints(context, view, gaps, from, y);
        }
        DrawLinks(context, text, view, gaps, from, y);
        DrawMarkers(context, view, gaps, from, y, gutter, width);
        if (!span.IsLast)
        {
            if (caretLines.Contains(line))
            {
                DrawWrapSign(context, from + XWith(view, gaps, span.Length, afterHints: false), y);
            }
            return;
        }
        double end = from + view.CodeWidth + HintsWidth(gaps);
        if (FoldedAt(line) is { } fold)
        {
            Rect chip = DrawPlaceholder(context, fold, end, y);
            slot.Placeholder = (line, chip.Translate(new Vector(0, slot.Row * lineHeight)));
            end = chip.Right;
        }
        LineOrdered<LineAnnotation> annotations = decorations.AnnotationsByLine;
        int annotation = annotations.From(line);
        int after = annotation;
        while (after < annotations.Count && annotations.LineOf(after) == line)
        {
            after++;
        }
        if (after > annotation)
        {
            DrawAnnotations(context, annotations, annotation, after, end, y, slot);
        }
    }

    /// <summary>What goes over the rows: the carets, a snippet's fields and a diff's rules on the text's side of the gutter,
    /// and the fold preview over all.</summary>
    private void DrawOverlay(DrawingContext context)
    {
        if (frameText is not { } text)
        {
            return;
        }
        double gutter = frameGutter;
        Size size = frameSize;
        double left = TextLeft - scroll.X;
        using (context.PushClip(new Rect(gutter, 0, Math.Max(0, size.Width - gutter), size.Height)))
        {
            if (frameFocused && caretOn)
            {
                DrawCarets(context, text, frameStart, frameEnd, frameRow, frameRow + rowsShown.Count - 1, left);
            }
            DrawSnippet(context, text, left);
            if (ChangeBackgrounds && rowsShown.Count > 0)
            {
                DrawChangeRules(context, rowsShown[0].Line, rowsShown[^1].Line, gutter, size.Width);
            }
        }
        DrawFoldPreview(context);
    }

    /// <summary>A visual the editor places itself, holding others.</summary>
    private sealed class Layer : Visual
    {
        public void Place(Rect bounds)
        {
            if (Bounds != bounds)
            {
                Bounds = bounds;
            }
        }

        public void Add(Visual child) => VisualChildren.Add(child);
    }

    /// <summary>A visual the editor draws: a row, or the overlay.</summary>
    private sealed class Painted(Action<DrawingContext> draw) : Visual
    {
        public void Place(Rect bounds)
        {
            if (Bounds != bounds)
            {
                Bounds = bounds;
            }
        }

        public void Redraw() => InvalidateVisual();

        public override void Render(DrawingContext context) => draw(context);
    }

    /// <summary>A row on screen: its visual, the row it draws (none while free), where it stands in the text's rows and
    /// among those the frame shows, and the frame that last drew it.</summary>
    private sealed class RowSlot
    {
        public RowSlot(CodeEditor editor)
        {
            Visual = new Painted(context => editor.DrawRow(context, this));
        }

        public Painted Visual { get; }
        public RowSpan? Span { get; set; }
        public int Row { get; set; }
        public int Index { get; set; }
        public long Drawn { get; set; }
        public bool Showing { get; set; }

        /// <summary>The fold's placeholder chip its row drew, and its annotations' boxes, in the text's rows.</summary>
        public (int Line, Rect Chip)? Placeholder { get; set; }

        public List<(LineAnnotation Annotation, Rect Box)> Annotations { get; } = [];

        /// <summary>Lets go of what the row drew, as another row is given the slot, or none.</summary>
        public void Forget()
        {
            Placeholder = null;
            Annotations.Clear();
        }
    }
}
