using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What a row draws: its view (<see cref="ViewsOf"/>), and what goes over and beside it. A frame walks the rows
/// it shows, never the lines a fold hides between them, and finds the carets, selections and markers on a row by halving
/// rather than by looking at each. A line's number and marks are drawn on its first row, what follows its code (a fold's
/// placeholder, annotations) after its last.</summary>
public sealed partial class CodeEditor
{
    private readonly LabelCache<(string Text, int Style)> labels = new();
    private readonly LabelCache<(int Number, bool Current)> numbers = new();
    private readonly Dictionary<TokenKind, TextRunProperties> runProperties = [];
    private (TextRunProperties Current, TextRunProperties Other) numberRuns;

    /// <summary>The diff tints of the lines on screen, found again for each frame.</summary>
    private readonly Dictionary<int, IBrush> changeTints = [];
    private double measuredWidth;
    private bool showWhitespaces;

    /// <summary>Whether spaces are drawn as dots and tabs as arrows (Show Whitespaces).</summary>
    public bool ShowWhitespaces
    {
        get => showWhitespaces;
        set
        {
            showWhitespaces = value;
            Redraw();
        }
    }

    /// <summary>The editor's own drawing: the ground under the text and the gutter's. The rows draw themselves over it
    /// (<see cref="PrepareFrame"/>), so this is drawn again only for a change, never for a scroll.</summary>
    public override void Render(DrawingContext context)
    {
        Size size = Bounds.Size;
        context.FillRectangle(palette.Background, new Rect(size));
        context.FillRectangle(palette.Gutter, new Rect(0, 0, GutterWidth, size.Height));
    }

    private bool NoneSelected()
    {
        IReadOnlyList<Selection> items = selections.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].IsEmpty)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The first selection that ends at or after an offset; selections never overlap, so their ends rise with
    /// their starts.</summary>
    private int FirstSelectionEndingFrom(int offset) => Sorted.FirstAtOrAfter(selections.Items, static s => s.End, offset);

    /// <summary>The first caret at or after an offset; carets of selections that never overlap rise in order too.</summary>
    private int FirstCaretFrom(int offset) => Sorted.FirstAtOrAfter(selections.Items, static s => s.Caret, offset);

    /// <summary>The lines with a caret on them on screen, found again for each frame.</summary>
    private readonly HashSet<int> caretLines = [];

    /// <summary>The lines with a caret on them between two offsets: those on screen.</summary>
    private HashSet<int> CaretLines(TextSnapshot text, int start, int end)
    {
        HashSet<int> lines = caretLines;
        lines.Clear();
        IReadOnlyList<Selection> items = selections.Items;
        for (int i = FirstCaretFrom(start); i < items.Count && items[i].Caret <= end; i++)
        {
            lines.Add(text.GetLineFromOffset(items[i].Caret));
        }
        return lines;
    }

    /// <summary>The carets on rows <paramref name="first"/> to <paramref name="last"/>, each on the row it stands on.</summary>
    private void DrawCarets(DrawingContext context, TextSnapshot text, int start, int end, int first, int last, double left)
    {
        IReadOnlyList<Selection> items = selections.Items;
        for (int i = FirstCaretFrom(start); i < items.Count && items[i].Caret <= end; i++)
        {
            (int row, double x) = CaretPlace(text, items[i].Caret);
            if (row < first || row > last)
            {
                continue;
            }
            double y = (row * metrics.LineHeight) - scroll.Y;
            if (vim is { Mode: VimMode.Normal })
            {
                // ponytail: a block one CharWidth wide, so a wide character or a tab shows a narrow block; measure the character when it matters.
                context.DrawRectangle(null, palette.PenOf(palette.Caret, 1), new Rect(Math.Round(left + x) + 0.5, y + 0.5, metrics.CharWidth - 1, metrics.LineHeight - 1));
                continue;
            }
            context.FillRectangle(palette.Caret, new Rect(Math.Round(left + x), y, 2, metrics.LineHeight));
        }
    }

    private void DrawSelections(DrawingContext context, LineView view, Gap[] gaps, double left, double y, double width, bool focused)
    {
        RowSpan span = view.Span;
        IBrush brush = focused ? palette.Selection : palette.SelectionInactive;
        IReadOnlyList<Selection> items = selections.Items;
        for (int i = FirstSelectionEndingFrom(span.Start); i < items.Count && items[i].Start < span.EndIncludingBreak; i++)
        {
            Selection s = items[i];
            // One starting at the next line's start covers none of this line, its break included.
            if (s.IsEmpty || (s.End == span.Start && span.Length > 0))
            {
                continue;
            }
            int from = Math.Max(s.Start, span.Start) - span.Start;
            int to = Math.Min(s.End, span.End) - span.Start;
            double x1 = left + XWith(view, gaps, from);
            // A selection that runs on through the line break fills to the edge.
            double x2 = s.End > span.End ? width : left + XWith(view, gaps, to, afterHints: false);
            if (x2 > x1)
            {
                context.FillRectangle(brush, new Rect(x1, y, x2 - x1, metrics.LineHeight));
            }
        }
    }

    private void DrawHighlights(DrawingContext context, LineView view, Gap[] gaps, double left, double y)
    {
        RowSpan span = view.Span;
        IReadOnlyList<TextRange> ranges = Highlights;
        for (int i = Sorted.FirstAfter(ranges, static r => r.End, span.Start); i < ranges.Count && ranges[i].Start <= span.End; i++)
        {
            int from = Math.Max(ranges[i].Start, span.Start) - span.Start;
            int to = Math.Min(ranges[i].End, span.End) - span.Start;
            double x1 = left + XWith(view, gaps, from);
            double x2 = left + XWith(view, gaps, to, afterHints: false);
            if (x2 > x1)
            {
                Rect box = new(x1, y + 1, x2 - x1, metrics.LineHeight - 2);
                if (i == CurrentHighlight)
                {
                    // The current match stands out from the rest, outlined in the caret's colour.
                    context.DrawRectangle(palette.SearchCurrent, palette.PenOf(palette.Caret, 1), box, 2, 2);
                }
                else
                {
                    context.FillRectangle(palette.SearchMatch, box, 2);
                }
            }
        }
    }

    /// <summary>A row's whitespace: a dot in the middle of each space and an arrow across each tab, in their own
    /// colour. Only the columns on screen of a long line are read.</summary>
    private void DrawWhitespace(DrawingContext context, TextSnapshot text, LineView view, Gap[] gaps, double left, double y)
    {
        RowSpan span = view.Span;
        int from = 0;
        int to = span.Length;
        if (view.Long is not null)
        {
            from = view.From;
            to = view.To;
        }
        if (to <= from)
        {
            return;
        }
        char[] buffer = System.Buffers.ArrayPool<char>.Shared.Rent(to - from);
        try
        {
            text.CopyTo(span.Start + from, buffer.AsSpan(0, to - from));
            // On the pixel grid, so a dot and an arrow's shaft are drawn crisp in their colour.
            double middle = Math.Floor(y + (metrics.LineHeight / 2));
            double dot = Math.Max(2, Math.Round(metrics.FontSize / 7));
            IPen? arrow = null;
            for (int i = 0; i < to - from; i++)
            {
                char c = buffer[i];
                if (c is not (' ' or '\t' or ' ' or '　'))
                {
                    continue;
                }
                int column = from + i;
                double x1 = left + XWith(view, gaps, column);
                double x2 = left + XWith(view, gaps, column + 1, afterHints: false);
                if (c != '\t')
                {
                    context.FillRectangle(palette.Whitespace, new Rect(Math.Round(((x1 + x2) / 2) - (dot / 2)), middle - Math.Floor(dot / 2), dot, dot));
                    continue;
                }
                arrow ??= palette.PenOf(palette.Whitespace, 1);
                double shaft = middle + 0.5;
                double tail = x1 + (metrics.CharWidth * 0.3);
                double head = Math.Max(tail + 2, x2 - (metrics.CharWidth * 0.3));
                double barb = Math.Min(3, metrics.CharWidth * 0.35);
                ShapeKey key = new(ShapeKind.Tab, head - tail, barb, 0, 0);
                if (!shapes.TryGet(key, out StreamGeometry? shape))
                {
                    shape = shapes.Add(key, new StreamGeometry());
                    using StreamGeometryContext path = shape.Open();
                    path.BeginFigure(new Point(0, 0), false);
                    path.LineTo(new Point(head - tail, 0));
                    path.EndFigure(false);
                    path.BeginFigure(new Point(head - tail - barb, -barb), false);
                    path.LineTo(new Point(head - tail, 0));
                    path.LineTo(new Point(head - tail - barb, barb));
                    path.EndFigure(false);
                }
                DrawShape(context, arrow, shape, tail, shaft);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>The soft-wrap sign after a row its line goes on from: a small return arrow, in its own colour.</summary>
    private void DrawWrapSign(DrawingContext context, double x, double y)
    {
        double size = Math.Max(4, Math.Round(metrics.CharWidth * 0.7));
        double left = x + (metrics.CharWidth * 0.4);
        double top = y + ((metrics.LineHeight - size) / 2);
        double barb = Math.Max(2, size / 3);
        ShapeKey key = new(ShapeKind.Wrap, size, barb, 0, 0);
        if (!shapes.TryGet(key, out StreamGeometry? sign))
        {
            sign = shapes.Add(key, new StreamGeometry());
            using StreamGeometryContext path = sign.Open();
            path.BeginFigure(new Point(size, 0), false);
            path.LineTo(new Point(size, size));
            path.LineTo(new Point(0, size));
            path.EndFigure(false);
            path.BeginFigure(new Point(barb, size - barb), false);
            path.LineTo(new Point(0, size));
            path.LineTo(new Point(barb, size + barb));
            path.EndFigure(false);
        }
        DrawShape(context, palette.PenOf(palette.SoftWrapSign, 1), sign, left, top);
    }

    /// <summary>Squiggles under errors, warnings and suggestions; unnecessary code is faded by laying
    /// the background over it, half see-through.</summary>
    private void DrawMarkers(DrawingContext context, LineView view, Gap[] gaps, double left, double y, double clipLeft, double clipRight)
    {
        RowSpan span = view.Span;
        MarkerList markers = decorations.Markers;
        for (int i = markers.FirstReaching(span.Start); i < markers.Count; i++)
        {
            Marker marker = markers[i];
            // One starting where a row wraps is the next row's.
            if (marker.Range.Start > span.End || (!span.IsLast && marker.Range.Start == span.End))
            {
                break;
            }
            if (marker.Range.End < span.Start)
            {
                continue;
            }
            int from = Math.Max(marker.Range.Start, span.Start) - span.Start;
            int to = Math.Min(marker.Range.End, span.End) - span.Start;
            double x1 = left + XWith(view, gaps, from);
            double x2 = left + XWith(view, gaps, to, afterHints: false);
            if (x2 - x1 < metrics.CharWidth / 2)
            {
                // A zero-width diagnostic (a missing semicolon) still needs something to see.
                x2 = x1 + metrics.CharWidth;
            }
            if (marker.Kind == MarkerKind.Unnecessary)
            {
                context.FillRectangle(palette.Faded, new Rect(x1, y, x2 - x1, metrics.LineHeight));
                continue;
            }
            IBrush brush = marker.Kind switch
            {
                MarkerKind.Error => palette.Error,
                MarkerKind.Warning => palette.Warning,
                _ => palette.Info,
            };
            DrawWave(context, brush, x1, x2, y + metrics.LineHeight - 3, clipLeft, clipRight);
        }
    }

    /// <summary>A line's annotations after its code, a few spaces on, each in its look.</summary>
    private void DrawAnnotations(DrawingContext context, LineOrdered<LineAnnotation> annotations, int from, int to, double x, double y, RowSlot slot)
    {
        x += metrics.CharWidth * 3;
        for (int i = from; i < to; i++)
        {
            LineAnnotation annotation = annotations[i];
            bool ground = annotation.Look == LineAnnotationLook.ErrorOnGround;
            bool error = ground || annotation.Look == LineAnnotationLook.Error;
            if (!labels.TryGet((annotation.Text, error ? -4 : -3), out TextLine? text))
            {
                text = labels.Add((annotation.Text, error ? -4 : -3), Format(annotation.Text, [], error ? annotationRuns.Error : annotationRuns.Value));
            }
            double top = y + (metrics.LineHeight - text.Height) / 2;
            if (ground)
            {
                context.FillRectangle(palette.ErrorGround, new Rect(x - metrics.CharWidth / 2, y + 1, text.WidthIncludingTrailingWhitespace + metrics.CharWidth, metrics.LineHeight - 2), 3);
            }
            text.Draw(context, new Point(x, top));
            slot.Annotations.Add((annotation, new Rect(x - metrics.CharWidth / 2, (slot.Row * metrics.LineHeight) + y, text.WidthIncludingTrailingWhitespace + metrics.CharWidth, metrics.LineHeight)));
            x += text.WidthIncludingTrailingWhitespace + metrics.CharWidth * 2;
        }
    }

    /// <summary>A squiggle from x1 to x2, drawn only where it can be seen: whole waves off either edge are left out, so
    /// one under a line of a million characters costs the screen's width. Made at the origin for its length and placed
    /// there, so a scroll draws again the squiggles it moved without making them.</summary>
    private void DrawWave(DrawingContext context, IBrush brush, double x1, double x2, double y, double clipLeft, double clipRight)
    {
        const double Step = 2.5;
        double skipped = Math.Floor((clipLeft - (Step * 2) - x1) / (Step * 2));
        if (skipped > 0)
        {
            x1 += skipped * Step * 2;
        }
        x2 = Math.Min(x2, clipRight + (Step * 2));
        if (x2 <= x1)
        {
            return;
        }
        double length = x2 - x1;
        ShapeKey key = new(ShapeKind.Wave, length, 0, 0, 0);
        if (!shapes.TryGet(key, out StreamGeometry? wave))
        {
            wave = shapes.Add(key, new StreamGeometry());
            using StreamGeometryContext path = wave.Open();
            path.BeginFigure(new Point(0, 0), false);
            bool up = true;
            for (double x = Step; x < length + Step; x += Step)
            {
                path.LineTo(new Point(Math.Min(x, length), up ? -2 : 0));
                up = !up;
            }
            path.EndFigure(false);
        }
        DrawShape(context, palette.PenOf(brush, 1), wave, x1, y);
    }

    /// <summary>A shape made at the origin, drawn with its origin at a point.</summary>
    private static void DrawShape(DrawingContext context, IPen pen, StreamGeometry shape, double x, double y)
    {
        using (context.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            context.DrawGeometry(null, pen, shape);
        }
    }

    private readonly ShapeCache shapes = new();

    /// <summary>How many shapes (squiggles, tabs' arrows, wrap signs, fold chevrons, the gutter columns' marks) the editor
    /// has made to draw: what a frame that moved none should leave as it was.</summary>
    public long ShapesMade => shapes.Made + (gutterFrame?.ShapesMade ?? 0);

    /// <summary>What a shape the editor draws is: its kind and the numbers that place and size it.</summary>
    private readonly record struct ShapeKey(ShapeKind Kind, double A, double B, double C, double D);

    private enum ShapeKind
    {
        Wave,
        Tab,
        Wrap,
        Open,
        Folded,
    }

    /// <summary>The shapes drawn (squiggles, tabs' arrows, wrap signs, fold chevrons), by what they are and how large, each
    /// with the frame that last drew it: a row drawn later draws again those the rows on screen drew without making them,
    /// and a frame lets go of those no row on screen drew (<see cref="Begin"/>).</summary>
    private sealed class ShapeCache
    {
        private readonly Dictionary<ShapeKey, (StreamGeometry Shape, long Drawn)> kept = [];
        private long now;

        public bool TryGet(ShapeKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StreamGeometry? shape)
        {
            ref (StreamGeometry Shape, long Drawn) entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(kept, key);
            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref entry))
            {
                shape = null;
                return false;
            }
            entry.Drawn = now;
            shape = entry.Shape;
            return true;
        }

        /// <summary>How many shapes have been made: what drawing them has cost.</summary>
        public long Made { get; private set; }

        public StreamGeometry Add(ShapeKey key, StreamGeometry shape)
        {
            Made++;
            kept[key] = (shape, now);
            return shape;
        }

        /// <summary>Starts a frame, letting go of the shapes last drawn before <paramref name="keepFrom"/>.</summary>
        public void Begin(long frame, long keepFrom)
        {
            now = frame;
            foreach ((ShapeKey key, (StreamGeometry _, long drawn)) in kept)
            {
                if (drawn < keepFrom)
                {
                    kept.Remove(key);
                }
            }
        }

        public void Clear() => kept.Clear();
    }

    /// <summary>The text's I-beam and a link's hand, made once.</summary>
    private static Cursor? ibeam;
    private static Cursor? hand;

    private static Cursor IbeamCursor => ibeam ??= new Cursor(StandardCursorType.Ibeam);

    private static Cursor HandCursor => hand ??= new Cursor(StandardCursorType.Hand);

    /// <summary>A diff side's tint for each line from <paramref name="first"/> to <paramref name="last"/> inside a change,
    /// the first change's that holds it.</summary>
    private Dictionary<int, IBrush> ChangeTints(int first, int last)
    {
        Dictionary<int, IBrush> tints = changeTints;
        tints.Clear();
        LineOrdered<ChangeMark> changes = decorations.ChangesByLine;
        for (int i = changes.From(first); i < changes.Count && changes.LineOf(i) <= last; i++)
        {
            ChangeMark change = changes[i];
            IBrush brush = change.Kind switch
            {
                ChangeMarkKind.Added => palette.DiffAdded,
                ChangeMarkKind.Deleted => palette.DiffDeleted,
                ChangeMarkKind.Conflict => palette.DiffConflict,
                _ => palette.DiffModified,
            };
            for (int line = Math.Max(first, change.Line); line <= Math.Min(last, change.Line + change.Count - 1); line++)
            {
                tints.TryAdd(line, brush);
            }
        }
        return tints;
    }

    /// <summary>The changed words on a row, over its line's tint and behind its text; changes never overlap, so their ends
    /// rise with their starts and the first on the row is found by halving.</summary>
    private void DrawInlineChanges(DrawingContext context, LineView view, Gap[] gaps, double left, double y)
    {
        IReadOnlyList<InlineChange> changes = InlineChanges;
        if (changes.Count == 0)
        {
            return;
        }
        RowSpan span = view.Span;
        for (int i = Sorted.FirstAfter(changes, static c => c.Range.End, span.Start); i < changes.Count && changes[i].Range.Start < span.End; i++)
        {
            IBrush brush = changes[i].Kind switch
            {
                ChangeMarkKind.Added => palette.WordAdded,
                ChangeMarkKind.Deleted => palette.WordDeleted,
                ChangeMarkKind.Conflict => palette.WordConflict,
                _ => palette.WordModified,
            };
            FillRange(context, view, gaps, left, y, changes[i].Range, brush);
        }
    }

    /// <summary>A diff side's rule where the other side has lines this one has none of.</summary>
    private void DrawChangeRules(DrawingContext context, int first, int last, double gutter, double width)
    {
        LineOrdered<ChangeMark> changes = decorations.ChangesByLine;
        for (int i = changes.From(first); i < changes.Count && changes.LineOf(i) <= last + 1; i++)
        {
            ChangeMark change = changes[i];
            if (change.Count == 0 && change.Line >= first && change.Line <= last + 1)
            {
                IBrush brush = change.Kind switch
                {
                    ChangeMarkKind.Added => palette.ChangeAdded,
                    ChangeMarkKind.Deleted => palette.ChangeDeleted,
                    ChangeMarkKind.Conflict => palette.ChangeConflict,
                    _ => palette.ChangeModified,
                };
                context.FillRectangle(brush, new Rect(gutter, TopOf(change.Line) - scroll.Y - 1, width - gutter, 2));
            }
        }
    }

    /// <summary>The font and colour a token kind is drawn in, made once per theme; kinds drawn alike share theirs, by which
    /// neighbouring runs of one look on a line are formatted and drawn as one (<see cref="ColouredRuns"/>).</summary>
    private TextRunProperties Run(TokenKind kind)
    {
        if (!rainbowBraces && kind is TokenKind.Brace1 or TokenKind.Brace2 or TokenKind.Brace3)
        {
            kind = TokenKind.Text;
        }
        if (!runProperties.TryGetValue(kind, out TextRunProperties? props))
        {
            IBrush brush = palette.Syntax(kind);
            SyntaxStyle? style = palette.Style(kind);
            int set = style is not null ? 0 : kind switch
            {
                TokenKind.UserInput or TokenKind.Italic => 1,
                TokenKind.Heading or TokenKind.Bold => 2,
                _ => 0,
            };
            if (!runLooks.TryGetValue((brush, style, set), out props))
            {
                runLooks[(brush, style, set)] = props = style is { } styled ? StyledRunWith(brush, styled) : set switch
                {
                    1 => ItalicRunWith(brush),
                    2 => BoldRunWith(brush),
                    _ => RunWith(brush),
                };
            }
            runProperties[kind] = props;
        }
        return props;
    }

    /// <summary>The run properties made for each look: a brush, the theme's style or none, and the kind's own (plain, italic, bold).</summary>
    private readonly Dictionary<(IBrush Brush, SyntaxStyle? Style, int Set), TextRunProperties> runLooks = [];

    private static readonly FontFeatureCollection NoLigatures =
        [new FontFeature { Tag = "calt", Value = 0 }, new FontFeature { Tag = "liga", Value = 0 }];

    private GenericTextRunProperties RunWith(IBrush brush) =>
        new(Typeface, FontSize, foregroundBrush: brush, fontFeatures: ligatures ? null : NoLigatures);

    private GenericTextRunProperties ItalicRunWith(IBrush brush) =>
        new(new Typeface(FontFamily, FontStyle.Italic), FontSize, foregroundBrush: brush, fontFeatures: ligatures ? null : NoLigatures);

    private GenericTextRunProperties BoldRunWith(IBrush brush) =>
        new(new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold), FontSize, foregroundBrush: brush, fontFeatures: ligatures ? null : NoLigatures);

    /// <summary>A kind the scheme gives a style of its own (JetBrains Mono's bold and italic keep its columns).</summary>
    private GenericTextRunProperties StyledRunWith(IBrush brush, SyntaxStyle style) =>
        new(new Typeface(FontFamily, style.HasFlag(SyntaxStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
            style.HasFlag(SyntaxStyle.Bold) ? FontWeight.Bold : FontWeight.Normal), FontSize, foregroundBrush: brush, fontFeatures: ligatures ? null : NoLigatures);

    /// <summary>How a kind's text is set beside its colour, from the theme's <c>Passenger.SyntaxStyle.{Kind}</c>: "Bold",
    /// "Italic" or "Bold Italic".</summary>
    [Flags]
    private enum SyntaxStyle
    {
        Bold = 1,
        Italic = 2,
    }


    /// <summary>Every brush the editor paints with, from the look in force's <c>Passenger.Editor.*</c>,
    /// <c>Passenger.Syntax.*</c> and other tokens, never a colour of its own, so the look always draws the editor (a
    /// token the theme left out draws nothing), each taken as an immutable brush; and the problem counts' icons, the
    /// theme's <c>Icon.*</c> geometries, or none. Read again as the editor joins a window.</summary>
    private sealed class Palette
    {
        private static readonly IBrush None = Brushes.Transparent;
        private readonly Dictionary<TokenKind, IBrush> syntax = [];
        private readonly Dictionary<TokenKind, SyntaxStyle> styles = [];
        private readonly Dictionary<(IBrush Brush, double Thickness), IPen> pens = [];

        /// <summary>Every resource the palette was read from, in order, to tell a theme's change from none.</summary>
        private List<object?> found = [];

        public static Palette Default { get; } = new();

        public IBrush Background { get; private init; } = None;
        public IBrush Gutter { get; private init; } = None;
        public IBrush Text { get; private init; } = None;
        public IBrush LineNumber { get; private init; } = None;
        public IBrush LineNumberCurrent { get; private init; } = None;
        public IBrush CurrentLine { get; private init; } = None;
        public IBrush Selection { get; private init; } = None;
        public IBrush SelectionInactive { get; private init; } = None;
        public IBrush Caret { get; private init; } = None;
        public IBrush Preedit { get; private init; } = None;
        public IBrush SearchMatch { get; private init; } = None;
        public IBrush Error { get; private init; } = None;
        public IBrush Warning { get; private init; } = None;
        public IBrush Info { get; private init; } = None;
        public IBrush Faded { get; private init; } = None;
        public IBrush SearchCurrent { get; private init; } = None;
        public IBrush Annotation { get; private init; } = None;
        public IBrush Passed { get; private init; } = None;
        public IBrush Bookmark { get; private init; } = None;
        public IBrush DiffAdded { get; private init; } = None;
        public IBrush DiffModified { get; private init; } = None;
        public IBrush DiffDeleted { get; private init; } = None;
        public IBrush DiffConflict { get; private init; } = None;
        public IBrush WordAdded { get; private init; } = None;
        public IBrush WordModified { get; private init; } = None;
        public IBrush WordDeleted { get; private init; } = None;
        public IBrush WordConflict { get; private init; } = None;
        public IBrush ChangeAdded { get; private init; } = None;
        public IBrush ChangeModified { get; private init; } = None;
        public IBrush ChangeDeleted { get; private init; } = None;
        public IBrush ChangeConflict { get; private init; } = None;
        public IBrush ErrorGround { get; private init; } = None;
        public IBrush InlayHint { get; private init; } = None;
        public IBrush InlayHintBack { get; private init; } = None;
        public IBrush UsageRead { get; private init; } = None;
        public IBrush UsageWrite { get; private init; } = None;
        public IBrush UsageReadMark { get; private init; } = None;
        public IBrush UsageWriteMark { get; private init; } = None;
        public IBrush MatchedBrace { get; private init; } = None;
        public IBrush Whitespace { get; private init; } = None;
        public IBrush SoftWrapSign { get; private init; } = None;
        public IBrush LinkUnderline { get; private init; } = None;
        public IBrush PopupBorder { get; private init; } = None;

        /// <summary>The look's popup corners (the host's <c>Passenger.PopupRadius</c>), which the fold preview's card takes.</summary>
        public double PopupRadius { get; private init; } = 4;
        public IBrush SmartStepTarget { get; private init; } = None;
        public IBrush SmartStepChosen { get; private init; } = None;
        public IBrush SmartStepBorder { get; private init; } = None;
        public Geometry? ErrorIcon { get; private init; }
        public Geometry? WarningIcon { get; private init; }
        public Geometry? PassedIcon { get; private init; }

        public IBrush Syntax(TokenKind kind) => syntax.TryGetValue(kind, out IBrush? brush) ? brush : Text;

        /// <summary>A pen of a brush and a thickness, made once for each, immutable as a frame's drawing keeps it.</summary>
        public IPen PenOf(IBrush brush, double thickness)
        {
            if (!pens.TryGetValue((brush, thickness), out IPen? pen))
            {
                pens[(brush, thickness)] = pen = new ImmutablePen((IImmutableBrush)brush.ToImmutable(), thickness);
            }
            return pen;
        }

        /// <summary>The style the theme sets a kind in, or null for the kind's own.</summary>
        public SyntaxStyle? Style(TokenKind kind) => styles.TryGetValue(kind, out SyntaxStyle style) ? style : null;

        public static Palette From(Control control)
        {
            Palette defaults = Default;
            List<object?> found = [];
            Dictionary<IBrush, IBrush> fixedOf = new(ReferenceEqualityComparer.Instance);
            // Fixed, so drawing one adds no resource to a row's drawing to be tracked, and one brush stays one look.
            IBrush? F(string key)
            {
                IBrush? brush = Tokens.Find(control, key);
                found.Add(brush);
                if (brush is null)
                {
                    return null;
                }
                if (!fixedOf.TryGetValue(brush, out IBrush? still))
                {
                    fixedOf[brush] = still = brush.ToImmutable();
                }
                return still;
            }
            Palette palette = new()
            {
                Background = F(TokenKeys.Editor_Background) ?? defaults.Background,
                Gutter = F(TokenKeys.Editor_Gutter) ?? defaults.Gutter,
                Text = F(TokenKeys.Editor_Text) ?? defaults.Text,
                LineNumber = F(TokenKeys.Editor_LineNumber) ?? defaults.LineNumber,
                LineNumberCurrent = F(TokenKeys.Editor_LineNumberCurrent) ?? defaults.LineNumberCurrent,
                CurrentLine = F(TokenKeys.Editor_CurrentLine) ?? defaults.CurrentLine,
                Selection = F(TokenKeys.Editor_Selection) ?? defaults.Selection,
                SelectionInactive = F(TokenKeys.Editor_SelectionInactive) ?? defaults.SelectionInactive,
                Caret = F(TokenKeys.Editor_Caret) ?? defaults.Caret,
                Preedit = F(TokenKeys.Editor_Text) ?? defaults.Preedit,
                SearchMatch = F(TokenKeys.Editor_SearchMatch) ?? defaults.SearchMatch,
                Error = F(TokenKeys.Editor_Error) ?? defaults.Error,
                Warning = F(TokenKeys.Editor_Warning) ?? defaults.Warning,
                Info = F(TokenKeys.Editor_Info) ?? defaults.Info,
                Faded = Fade(F(TokenKeys.Editor_Background)) ?? defaults.Faded,
                SearchCurrent = F(TokenKeys.Editor_SearchCurrent) ?? defaults.SearchCurrent,
                Annotation = F(TokenKeys.Editor_InlineValue) ?? defaults.Annotation,
                Passed = F(TokenKeys.Test_Passed) ?? defaults.Passed,
                Bookmark = F(TokenKeys.Editor_Bookmark) ?? defaults.Bookmark,
                DiffAdded = F(TokenKeys.Diff_Added) ?? defaults.DiffAdded,
                DiffModified = F(TokenKeys.Diff_Modified) ?? defaults.DiffModified,
                DiffDeleted = F(TokenKeys.Diff_Deleted) ?? defaults.DiffDeleted,
                DiffConflict = F(TokenKeys.Diff_Conflict) ?? defaults.DiffConflict,
                WordAdded = F(TokenKeys.Diff_AddedWord) ?? defaults.WordAdded,
                WordModified = F(TokenKeys.Diff_ModifiedWord) ?? defaults.WordModified,
                WordDeleted = F(TokenKeys.Diff_DeletedWord) ?? defaults.WordDeleted,
                WordConflict = F(TokenKeys.Diff_ConflictWord) ?? defaults.WordConflict,
                ChangeAdded = F(TokenKeys.Vcs_Added) ?? defaults.ChangeAdded,
                ChangeModified = F(TokenKeys.Vcs_Modified) ?? defaults.ChangeModified,
                ChangeDeleted = F(TokenKeys.Vcs_Deleted) ?? defaults.ChangeDeleted,
                ChangeConflict = F(TokenKeys.Vcs_Conflict) ?? defaults.ChangeConflict,
                ErrorGround = F(TokenKeys.Editor_ExceptionBack) ?? defaults.ErrorGround,
                InlayHint = F(TokenKeys.Editor_InlayHint) ?? defaults.InlayHint,
                InlayHintBack = F(TokenKeys.Editor_InlayHintBack) ?? defaults.InlayHintBack,
                UsageRead = F(TokenKeys.Editor_UsageRead) ?? defaults.UsageRead,
                UsageWrite = F(TokenKeys.Editor_UsageWrite) ?? defaults.UsageWrite,
                UsageReadMark = F(TokenKeys.Editor_UsageReadMark) ?? defaults.UsageReadMark,
                UsageWriteMark = F(TokenKeys.Editor_UsageWriteMark) ?? defaults.UsageWriteMark,
                MatchedBrace = F(TokenKeys.Editor_MatchedBrace) ?? defaults.MatchedBrace,
                Whitespace = F(TokenKeys.Editor_Whitespace) ?? defaults.Whitespace,
                SoftWrapSign = F(TokenKeys.Editor_SoftWrapSign) ?? defaults.SoftWrapSign,
                LinkUnderline = F(TokenKeys.Editor_LinkUnderline) ?? defaults.LinkUnderline,
                PopupBorder = F(TokenKeys.Editor_PopupBorder) ?? defaults.PopupBorder,
                PopupRadius = control.TryFindResource(TokenKeys.PopupRadius, control.ActualThemeVariant, out object? radius) && radius is CornerRadius corners
                    ? corners.TopLeft : defaults.PopupRadius,
                SmartStepTarget = F(TokenKeys.Editor_SmartStepTarget) ?? defaults.SmartStepTarget,
                SmartStepChosen = F(TokenKeys.Editor_SmartStepChosen) ?? defaults.SmartStepChosen,
                SmartStepBorder = F(TokenKeys.Editor_SmartStepBorder) ?? defaults.SmartStepBorder,
                ErrorIcon = Icon(control, IconKeys.Error),
                WarningIcon = Icon(control, IconKeys.Problems),
                PassedIcon = Icon(control, IconKeys.Check),
            };
            foreach (TokenKind kind in Enum.GetValues<TokenKind>())
            {
                if (F($"Passenger.Syntax.{kind}") is { } brush)
                {
                    palette.syntax[kind] = brush;
                }
                object? set = control.TryFindResource($"Passenger.SyntaxStyle.{kind}", control.ActualThemeVariant, out object? style) ? style : null;
                found.Add(set);
                if (set is string named)
                {
                    palette.styles[kind] = (named.Contains("Bold", StringComparison.Ordinal) ? SyntaxStyle.Bold : 0)
                        | (named.Contains("Italic", StringComparison.Ordinal) ? SyntaxStyle.Italic : 0);
                }
            }
            palette.found = found;
            return palette;
        }

        /// <summary>Whether another palette was read from the very resources this one was, so nothing need be drawn again.</summary>
        public bool SameAs(Palette other) => found.SequenceEqual(other.found, ReferenceEqualityComparer.Instance);

        private static IBrush? Fade(IBrush? background) =>
            background is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.5).ToImmutable() : null;

        private static Geometry? Icon(Control control, string key) =>
            control.TryFindResource(key, control.ActualThemeVariant, out object? value) ? value as Geometry : null;
    }
}
