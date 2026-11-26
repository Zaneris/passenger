using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Rows above lines: words the host draws on a row of their own above a line (<see cref="RowAbove"/>), in the
/// rows soft wrap and the folds make (<see cref="RowMap"/>), never part of the text. Each is a retained visual of its own
/// beside the rows of text, drawn again only where its words, its place or the word under the pointer changed; a press on
/// a word is the host's (<see cref="RowAbovePressed"/>), and a rest over one its hover. Carets, selections and the text's
/// readers pass them by.</summary>
public sealed partial class CodeEditor
{
    private readonly List<AboveSlot> aboveSlots = [];
    private readonly Dictionary<(int Line, int Nth), AboveSlot> keptAbove = [];

    /// <summary>The word the pointer is over: its row's line and place among the rows above it, and the word's place.</summary>
    private (int Line, int Nth, int Word)? aboveHovered;

    /// <summary>The rows above lines, in offset order: the decorations', carried through each edit ahead of everyone who
    /// hears of it.</summary>
    public IReadOnlyList<RowAbove> RowsAbove => decorations.RowsAbove;

    /// <summary>The host's rows above lines for the text as it stands; the line at the view's top stays there.</summary>
    public void SetRowsAbove(IReadOnlyList<RowAbove> rows) => decorations.SetRowsAbove(rows);

    /// <summary>A press on a word of a row above a line: the host's.</summary>
    public event EventHandler<RowAbovePress>? RowAbovePressed;

    /// <summary>The middle of a word of the rows above a line (from zero), the <paramref name="nth"/> row above it, in the
    /// editor's coordinates; null while it is not on screen.</summary>
    public Point? RowAbovePoint(int line, int nth, int word)
    {
        foreach (AboveSlot slot in aboveSlots)
        {
            if (slot.Span is { } span && span.Line == line && span.Nth == nth && word < slot.WordBoxes.Count)
            {
                Rect box = slot.WordBoxes[word];
                return new Point(box.Center.X, (span.Row * metrics.LineHeight) + box.Center.Y - scroll.Y);
            }
        }
        return null;
    }

    /// <summary>How many rows above lines the editor has drawn: what a frame records of them.</summary>
    public long RowsAboveDrawn { get; private set; }

    /// <summary>Gives each row above a line shown a slot, as <see cref="PlaceRows"/> gives the rows of text theirs: the one
    /// that drew it, unless all are drawn again, which draws again only where what it is drawn from changed; else a free
    /// one, which draws it; slots left over draw nothing.</summary>
    private void PlaceAbove(TextSnapshot text, List<AboveSpan> shown, bool stale, long now)
    {
        keptAbove.Clear();
        foreach (AboveSlot slot in aboveSlots)
        {
            if (!stale && slot.Span is { } span)
            {
                keptAbove[(span.Line, span.Nth)] = slot;
            }
            slot.Showing = false;
        }
        RowAboveList rows = decorations.RowsAbove;
        unplaced.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            AboveSpan span = shown[i];
            if (!keptAbove.TryGetValue((span.Line, span.Nth), out AboveSlot? kept))
            {
                unplaced.Add(i);
                continue;
            }
            RowAbove row = rows[span.Index];
            double x = AboveX(text, span.Line, row.Offset);
            ulong key = AboveKey(span, row, x);
            kept.Showing = true;
            kept.Span = span;
            kept.Above = row;
            kept.X = x;
            kept.Place(span.Row, frameSize.Width, metrics.LineHeight);
            if (key != kept.Key)
            {
                kept.Key = key;
                kept.Drawn = now;
                kept.Visual.Redraw();
            }
        }
        int next = 0;
        foreach (int i in unplaced)
        {
            while (next < aboveSlots.Count && aboveSlots[next].Showing)
            {
                next++;
            }
            if (next == aboveSlots.Count)
            {
                AboveSlot added = new(this);
                aboveSlots.Add(added);
                rowsLayer.Add(added.Visual);
            }
            AboveSpan span = shown[i];
            RowAbove row = rows[span.Index];
            AboveSlot slot = aboveSlots[next];
            slot.Showing = true;
            slot.Span = span;
            slot.Above = row;
            slot.X = AboveX(text, span.Line, row.Offset);
            slot.Key = AboveKey(span, row, slot.X);
            slot.Drawn = now;
            slot.WordBoxes.Clear();
            slot.Place(span.Row, frameSize.Width, metrics.LineHeight);
            slot.Visual.Redraw();
        }
        foreach (AboveSlot slot in aboveSlots)
        {
            if (!slot.Showing && slot.Span is not null)
            {
                slot.LetGo();
            }
        }
    }

    /// <summary>Where a row above a line starts, from the text's left edge: at its offset's column on the line's first row.</summary>
    private double AboveX(TextSnapshot text, int line, int offset)
    {
        RowSpan span = RowSpanOf(text, line, 0);
        int at = Math.Clamp(offset, span.Start, span.End);
        return XWith(ViewOf(text, span), GapsOn(span), at - span.Start, afterHints: false);
    }

    /// <summary>What a row above a line is drawn from, mixed into one number: its words, where it starts, and the word under
    /// the pointer.</summary>
    private ulong AboveKey(AboveSpan span, RowAbove row, double x)
    {
        RowKey key = new();
        key.Add(span.Line);
        key.Add(span.Nth);
        key.Add(x);
        foreach (RowAboveWord word in row.Words)
        {
            key.Add(word.Text);
            key.Add(word.DotBrush);
        }
        key.Add(aboveHovered is { } hovered && hovered.Line == span.Line && hovered.Nth == span.Nth ? hovered.Word : -1);
        return key.Value;
    }

    /// <summary>A row above a line: its words in the hints' look, from where it starts, the one under the pointer underlined
    /// as a link is; each word's box kept for a press and a rest over it.</summary>
    private void DrawAbove(DrawingContext context, AboveSlot slot)
    {
        if (slot.Span is not { } span || frameText is null)
        {
            return;
        }
        RowsAboveDrawn++;
        slot.Drawn = frameNumber;
        slot.WordBoxes.Clear();
        double lineHeight = metrics.LineHeight;
        double gutter = frameGutter;
        DrawGutterRow(context, span.Row, span.Line, firstRow: false);
        using (context.PushClip(new Rect(gutter, 0, Math.Max(0, frameSize.Width - gutter), lineHeight)))
        {
            double x = TextLeft - scroll.X + slot.X;
            IReadOnlyList<RowAboveWord> words = slot.Above.Words;
            for (int i = 0; i < words.Count; i++)
            {
                RowAboveWord word = words[i];
                (string Text, int Style) key = (word.Text, -10);
                if (!labels.TryGet(key, out TextLine? label))
                {
                    label = labels.Add(key, Format(word.Text, [], hintRun ??= HintRun()));
                }
                double textX = x;
                if (word.DotBrush is { } resource && Tokens.Find(this, resource) is { } brush)
                {
                    const double radius = 3;
                    context.DrawEllipse(brush, null, new Point(x + radius, lineHeight / 2), radius, radius);
                    textX += 2 * radius + 5;
                }
                double width = textX - x + label.WidthIncludingTrailingWhitespace;
                double top = (lineHeight - label.Height) / 2;
                label.Draw(context, new Point(textX, top));
                if (aboveHovered is { } hovered && hovered.Line == span.Line && hovered.Nth == span.Nth && hovered.Word == i)
                {
                    context.FillRectangle(palette.LinkUnderline, new Rect(x, top + label.Baseline + 1, width, 1));
                }
                slot.WordBoxes.Add(new Rect(x, 0, width, lineHeight));
                x += width + (metrics.CharWidth * 2);
            }
        }
    }

    /// <summary>The word of a row above a line under a point in the control, with the row's line and place among the rows
    /// above it, and the word's place; null off every word.</summary>
    private (int Line, int Nth, int Index, RowAboveWord Word)? AboveWordAt(Point point)
    {
        if (point.X < GutterWidth)
        {
            return null;
        }
        int row = RowAtY(point.Y);
        RowAboveList rows = decorations.RowsAbove;
        foreach (AboveSlot slot in aboveSlots)
        {
            if (slot.Span is not { } span || span.Row != row || span.Index >= rows.Count || !slot.Above.DrawnAlike(rows[span.Index]))
            {
                continue;
            }
            // A row with the same text and look may have new data without drawing again.
            RowAbove current = rows[span.Index];
            double y = point.Y + scroll.Y - (row * metrics.LineHeight);
            for (int i = 0; i < slot.WordBoxes.Count; i++)
            {
                if (slot.WordBoxes[i].Contains(new Point(point.X, y)))
                {
                    return (span.Line, span.Nth, i, current.Words[i]);
                }
            }
        }
        return null;
    }

    /// <summary>Whether a point in the control is on a row above a line.</summary>
    private bool IsAboveRow(Point point) => point.Y + scroll.Y >= 0 && Rows.LineOfRow(RowAtY(point.Y)).Row < 0;

    /// <summary>The pointer moved: over a word of a row above a line it takes the hand, and the word is underlined.</summary>
    private void FollowAbove(Point position)
    {
        (int Line, int Nth, int Word)? over = drag.Mode == DragMode.None && AboveWordAt(position) is { } word ? (word.Line, word.Nth, word.Index) : null;
        if (over is not null)
        {
            Cursor = HandCursor;
        }
        else if (IsAboveRow(position) && drag.Mode == DragMode.None && position.X >= GutterWidth)
        {
            Cursor = Cursor.Default;
        }
        if (over != aboveHovered)
        {
            aboveHovered = over;
            RedrawChanged();
        }
    }

    /// <summary>A press on a row above a line, which is no place in the text, its gutter none of a line's: on a word, the
    /// host's; whether it was on such a row.</summary>
    private bool PressAbove(Point position, KeyModifiers modifiers, bool context)
    {
        if (!IsAboveRow(position))
        {
            return false;
        }
        if (AboveWordAt(position) is { } word)
        {
            RowAbovePressed?.Invoke(this, new RowAbovePress(word.Line, word.Word, modifiers, position, context));
        }
        return true;
    }

    /// <summary>The rows above lines the slots draw carried through an edit, as the rows of text are: a row above a line it
    /// did not reach stays that line's, moved with it; one it reached is let go.</summary>
    private void FollowAboveSlots(DocumentChangedEventArgs change, int first, int last, int lines)
    {
        TextSnapshot before = change.Before;
        foreach (AboveSlot slot in aboveSlots)
        {
            if (slot.Span is not { } span || span.Line >= before.LineCount)
            {
                continue;
            }
            LineSpan line = before.GetLine(span.Line);
            if (line.Start > last)
            {
                slot.Span = span with { Line = span.Line + lines };
            }
            else if (line.EndIncludingBreak >= first)
            {
                slot.LetGo();
            }
        }
        if (aboveHovered is { } hovered && hovered.Line < before.LineCount && before.GetLine(hovered.Line).EndIncludingBreak >= first)
        {
            aboveHovered = before.GetLine(hovered.Line).Start > last ? hovered with { Line = hovered.Line + lines } : null;
        }
    }

    /// <summary>A row above a line on screen: its visual, the row it draws (none while free), what it shows and from where,
    /// the frame that last drew it, and its words' boxes from the row's top in the editor's x.</summary>
    private sealed class AboveSlot
    {
        public AboveSlot(CodeEditor editor)
        {
            Visual = new Painted(context => editor.DrawAbove(context, this));
        }

        public Painted Visual { get; }
        public AboveSpan? Span { get; set; }
        public RowAbove Above { get; set; }
        public double X { get; set; }
        public long Drawn { get; set; }
        public bool Showing { get; set; }
        public ulong Key { get; set; }
        public List<Rect> WordBoxes { get; } = [];

        public void Place(int row, double width, double lineHeight)
        {
            if (Visual.ZIndex != row)
            {
                Visual.ZIndex = row;
            }
            Visual.Place(new Rect(0, row * lineHeight, width, lineHeight));
        }

        /// <summary>Lets go of its row and the drawing it holds, so the slot shows nothing until given another.</summary>
        public void LetGo()
        {
            Span = null;
            WordBoxes.Clear();
            Visual.Redraw();
        }
    }
}

/// <summary>A press on a word of a row above a line (from zero): the word, the keys held, where it was in the editor's
/// coordinates, and whether it was a right-click.</summary>
public readonly record struct RowAbovePress(int Line, RowAboveWord Word, KeyModifiers Modifiers, Point Position, bool Context);
