using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Inlay hints (parameter and type names): a label on a chip inside a line, part of no document. The line is
/// drawn in pieces, each moved right by the hints before it, so every column's x and every x's column go through
/// <see cref="XWith"/> and <see cref="ColumnWith"/>. The hints at an offset are a place of their own for the caret
/// (<see cref="leaning"/>). An input method's composition is one more gap, where the primary caret
/// stands, so the text after it moves aside as typed text would.</summary>
public sealed partial class CodeEditor
{
    private TextRunProperties? hintRun;
    private bool showParameterHints = true;
    private bool showTypeHints = true;

    /// <summary>The offsets of carets standing on the other side of the hints there than a caret stands by default,
    /// stepped across by Left or Right; any other move or edit of the carets clears them.</summary>
    private HashSet<int> leaning = [];

    /// <summary>What the next <see cref="SetSelections"/> leaves in <see cref="leaning"/>: set by a step across hints.</summary>
    private HashSet<int>? nextLeaning;

    /// <summary>Whether parameter name hints are drawn; off, the lines stand as typed.</summary>
    public bool ShowParameterHints
    {
        get => showParameterHints;
        set
        {
            if (showParameterHints != value)
            {
                showParameterHints = value;
                // A hint takes room on its row.
                Rewrap();
            }
            InvalidateVisual();
        }
    }

    /// <summary>Whether type name hints are drawn.</summary>
    public bool ShowTypeHints
    {
        get => showTypeHints;
        set
        {
            if (showTypeHints != value)
            {
                showTypeHints = value;
                Rewrap();
            }
            InvalidateVisual();
        }
    }

    /// <summary>The hints the wraps were last made with: those the decorations held at the last change or set, so a set
    /// wraps again only the lines whose hints are drawn otherwise.</summary>
    private HintList wrappedHints = HintList.Empty;

    /// <summary>The hints, in offset order, a type's before a parameter's name at the same offset (it ends the text before):
    /// the decorations', carried through each edit ahead of everyone who hears of it (<see cref="OnChangedFirst"/>).</summary>
    public IReadOnlyList<InlayHint> InlayHints => decorations.InlayHints;

    /// <summary>The host's hints for the text as it stands, taken as they are when in order (a <see cref="HintList"/> always
    /// is); with soft wrap on, only the lines whose hints are drawn otherwise are wrapped again.</summary>
    public void SetInlayHints(IReadOnlyList<InlayHint> hints) => decorations.SetInlayHints(hints);

    /// <summary>A hint where it stands on its line: the column it goes at, the room it takes, its label, whether it ends
    /// the text before it (a type) rather than starting the text after it, and whether it is the input method's
    /// composition rather than a hint.</summary>
    private readonly record struct Gap(int Column, double Width, TextLine Label, bool PadLeft, bool Trailing, InlayHint Hint = default, bool Composing = false);

    private bool Shows(InlayHint hint) => hint.Kind switch
    {
        InlayHintKind.Type => showTypeHints,
        InlayHintKind.Parameter => showParameterHints,
        _ => true,
    };

    /// <summary>Whether a hint ends the text before it (a type, a value) rather than beginning the text after it.</summary>
    private static bool Trails(InlayHint hint) => HintList.Trails(hint);

    /// <summary>The hints on a row, in column order, and the composition where the primary caret stands among them.</summary>
    private Gap[] GapsOn(RowSpan span)
    {
        Gap[] hints = HintsOn(span);
        int caret = selections.Primary.Caret;
        if (preedit.Length == 0 || !IsOn(span, caret, LeansBack(caret)))
        {
            return hints;
        }
        TextLine composed = PreeditLabel();
        Gap composing = new(caret - span.Start, composed.WidthIncludingTrailingWhitespace, composed, false, false, Composing: true);
        bool after = StandsAfterHints(caret);
        int at = 0;
        while (at < hints.Length && (hints[at].Column < composing.Column || (after && hints[at].Column == composing.Column)))
        {
            at++;
        }
        return [.. hints[..at], composing, .. hints[at..]];
    }

    /// <summary>Whether a caret at an offset is on a row: where a row wraps, on the row after unless it leans back.</summary>
    private static bool IsOn(RowSpan span, int offset, bool beforeWrap) =>
        (offset > span.Start || (offset == span.Start && !(span.Row > 0 && beforeWrap)))
        && (offset < span.End || (offset == span.End && (span.IsLast || beforeWrap)));

    /// <summary>The composition as it is drawn.</summary>
    private TextLine PreeditLabel() => labels.Get((preedit, -9), () => Format(preedit, [], RunWith(palette.Preedit)));

    /// <summary>The first hint at or after an offset, shown or not.</summary>
    private int FirstHintFrom(int offset)
    {
        HintList hints = decorations.InlayHints;
        int low = 0;
        int high = hints.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (hints[middle].Offset < offset)
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

    /// <summary>The hints shown on a row: those at a wrap go on the row it starts. Kept for the row while the text, the hints
    /// and how they are drawn stand (<see cref="rowGaps"/>), so a frame that draws them again makes none of them again.</summary>
    private Gap[] HintsOn(RowSpan span)
    {
        HintList hints = decorations.InlayHints;
        int low = FirstHintFrom(span.Start);
        int end = span.IsLast ? span.End : span.End - 1;
        if (low >= hints.Count || hints[low].Offset > end)
        {
            return [];
        }
        (HintList, TextSnapshot, bool, bool, double, double) drawn = (hints, document.Snapshot, showTypeHints, showParameterHints, metrics.CharWidth, FontSize);
        if (!rowGapsOf.Equals(drawn) || rowGaps.Count > 4096)
        {
            rowGaps.Clear();
            rowGapsOf = drawn;
        }
        if (rowGaps.TryGetValue((span.Start, span.End), out Gap[]? kept) && Drawn(kept))
        {
            return kept;
        }
        int count = 0;
        for (int i = low; i < hints.Count && hints[i].Offset <= end; i++)
        {
            count += Shows(hints[i]) ? 1 : 0;
        }
        Gap[] gaps = count == 0 ? [] : new Gap[count];
        int at = 0;
        for (int i = low; i < hints.Count && hints[i].Offset <= end; i++)
        {
            InlayHint hint = hints[i];
            if (!Shows(hint))
            {
                continue;
            }
            double width = HintWidth(hint, out TextLine label);
            gaps[at++] = new Gap(hint.Offset - span.Start, width, label, hint.PadLeft, Trails(hint), hint);
        }
        rowGaps[(span.Start, span.End)] = gaps;
        return gaps;
    }

    /// <summary>The room a hint takes on its row, its label padded, and the label.</summary>
    private double HintWidth(InlayHint hint, out TextLine label)
    {
        label = labels.TryGet((hint.Label, -6), out TextLine? known) ? known : labels.Add((hint.Label, -6), Format(hint.Label, [], hintRun ??= HintRun()));
        double pad = metrics.CharWidth / 2;
        return label.WidthIncludingTrailingWhitespace + pad + (hint.PadLeft ? pad : 0) + (hint.PadRight ? pad : 0);
    }

    /// <summary>The first hint shown from an index of the hints on.</summary>
    private int ShownHintFrom(HintList hints, int index)
    {
        while (index < hints.Count && !Shows(hints[index]))
        {
            index++;
        }
        return index;
    }

    /// <summary>Whether a row's hints kept have their labels still, kept on for this frame: a frame lets go of the labels it
    /// did not draw.</summary>
    private bool Drawn(Gap[] gaps)
    {
        foreach (Gap gap in gaps)
        {
            if (!labels.TryGet((gap.Hint.Label, -6), out TextLine? label) || !ReferenceEquals(label, gap.Label))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Each row's hints as <see cref="HintsOn"/> found them, by where the row starts and ends, while what they were
    /// found for (<see cref="rowGapsOf"/>: the hints, the text, which are shown, the font) stands.</summary>
    private readonly Dictionary<(int Start, int End), Gap[]> rowGaps = [];
    private (HintList?, TextSnapshot?, bool, bool, double, double) rowGapsOf;

    /// <summary>Whether shown hints stand at an offset, and whether one of them ends the text before it (a type).</summary>
    private (bool Any, bool Trailing) HintsAt(int offset)
    {
        HintList hints = decorations.InlayHints;
        bool any = false;
        bool trailing = false;
        for (int i = FirstHintFrom(offset); i < hints.Count && hints[i].Offset == offset; i++)
        {
            if (Shows(hints[i]))
            {
                any = true;
                trailing |= Trails(hints[i]);
            }
        }
        return (any, trailing);
    }

    /// <summary>Whether a caret at an offset stands after the hints there: by default after a parameter's name, which
    /// begins its argument, and before a type, which ends the name it types; the other side once stepped across.</summary>
    private bool StandsAfterHints(int offset)
    {
        bool natural = !HintsAt(offset).Trailing;
        return leaning.Contains(offset) ? !natural : natural;
    }

    /// <summary>Left or Right over hints and soft wraps: the hints at an offset are one place of their own, and so is
    /// the end of a row a line wraps after, so a caret before them steps to after them (Right) and back (Left) without
    /// moving in the text, and one stepping onto such an offset stands on the near side of them. Where a row wraps, the
    /// end of the row before comes first, then the next row's start, then its hints.</summary>
    private void StepHorizontally(TextSnapshot text, bool forward, bool extend)
    {
        document.BreakUndoGroup();
        HashSet<int>? lean = null;
        HashSet<int>? wrapLean = null;
        SelectionSet moved = selections.With(s =>
        {
            if (!extend && !s.IsEmpty)
            {
                return s.MoveTo(forward ? s.End : s.Start, extend: false);
            }
            int caret = s.Caret;
            bool after = StandsAfterHints(caret);
            bool beforeWrap = LeansBack(caret);
            int to;
            bool toAfter;
            if (forward && beforeWrap)
            {
                // From the end of the row before a wrap to the start of the next, before its hints.
                to = caret;
                toAfter = false;
            }
            else if (HintsAt(caret).Any && after != forward)
            {
                to = caret;
                toAfter = forward;
            }
            else if (!forward && !beforeWrap && IsWrapAt(text, caret))
            {
                to = caret;
                toAfter = after;
                (wrapLean ??= []).Add(caret);
            }
            else
            {
                to = forward ? TextNavigation.NextGrapheme(text, caret) : TextNavigation.PreviousGrapheme(text, caret);
                // At either end of the text there is nowhere to go: the caret keeps its side.
                toAfter = to == caret ? after : !forward;
                if (forward && to != caret && IsWrapAt(text, to))
                {
                    (wrapLean ??= []).Add(to);
                }
            }
            if (HintsAt(to) is { Any: true, Trailing: bool trailing } && toAfter == trailing)
            {
                (lean ??= []).Add(to);
            }
            return s.MoveTo(to, extend);
        });
        nextLeaning = lean;
        nextWrapLeaning = wrapLean;
        SetSelections(moved, reveal: true);
    }

    /// <summary>Whether a row of the offset's line starts at it, its line wrapping there.</summary>
    private bool IsWrapAt(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(offset);
        return WrapOf(line) is { } wrap && wrap.Starts.BinarySearch(offset - text.GetLineStart(line)) >= 0;
    }

    /// <summary>Whether a point on the text is past the middle of the hints at the offset it falls at, where a click
    /// leaves the caret after them.</summary>
    private bool PastHintsAt(Point point, int offset)
    {
        TextSnapshot text = document.Snapshot;
        if (RowSpanAt(text, point) is not { } span || offset < span.Start || offset > span.End)
        {
            return false;
        }
        LineView view = ViewOf(text, span);
        Gap[] gaps = HintsOn(span);
        int column = offset - span.Start;
        double before = span.Indent + XWith(view, gaps, column, afterHints: false);
        double after = span.Indent + XWith(view, gaps, column, afterHints: true);
        return after > before && point.X - TextLeft + scroll.X >= (before + after) / 2;
    }

    /// <summary>The row under a point, or null below the last.</summary>
    private RowSpan? RowSpanAt(TextSnapshot text, Point point)
    {
        (int line, int row) = Rows.LineOfRow(RowAtY(point.Y));
        return line < 0 || line >= text.LineCount ? null : RowSpanOf(text, line, row);
    }

    /// <summary>The carets for a click at an offset, standing on the side of the hints there that the click was nearer.</summary>
    private void LeanFor(Point point, int offset)
    {
        if (HintsAt(offset) is { Any: true, Trailing: bool trailing } && PastHintsAt(point, offset) == trailing)
        {
            nextLeaning = [offset];
        }
    }

    /// <summary>The hint drawn under a point, if any: what a hover card over it describes.</summary>
    private InlayHint? HintUnder(Point point)
    {
        TextSnapshot text = document.Snapshot;
        if (RowSpanAt(text, point) is not { } span)
        {
            return null;
        }
        LineView view = ViewOf(text, span);
        double x = point.X - TextLeft + scroll.X - span.Indent;
        double shift = 0;
        foreach (Gap gap in GapsOn(span))
        {
            double at = view.DistanceOf(gap.Column) + shift;
            if (x >= at && x < at + gap.Width && !gap.Composing)
            {
                return gap.Hint;
            }
            shift += gap.Width;
        }
        return null;
    }

    /// <summary>A column's x on a line with hints: after the hints at it, or before them (a range's end, or a caret
    /// standing before a type). A composition at the column always counts, being typed where the caret stands.</summary>
    private static double XWith(LineView layout, Gap[] gaps, int column, bool afterHints = true)
    {
        double x = layout.DistanceOf(column);
        foreach (Gap gap in gaps)
        {
            if (gap.Column > column)
            {
                break;
            }
            if (gap.Column < column || afterHints || gap.Composing)
            {
                x += gap.Width;
            }
        }
        return x;
    }

    /// <summary>The column nearest an x on a line with hints; an x over a hint is the column the hint stands at.</summary>
    private static int ColumnWith(LineView layout, Gap[] gaps, double x, int length)
    {
        double shift = 0;
        foreach (Gap gap in gaps)
        {
            double at = layout.DistanceOf(gap.Column) + shift;
            if (x < at)
            {
                break;
            }
            if (x < at + gap.Width)
            {
                return gap.Column;
            }
            shift += gap.Width;
        }
        return Math.Clamp(layout.ColumnAt(x - shift), 0, length);
    }

    private static double HintsWidth(Gap[] gaps)
    {
        double width = 0;
        foreach (Gap gap in gaps)
        {
            width += gap.Width;
        }
        return width;
    }

    /// <summary>A line with hints: its text in pieces between them, each moved right by the hints before it, and
    /// each hint's label on its chip in the room left. Each of the line's runs is drawn once, in the piece it is in; only
    /// a run a hint stands inside (a stretch of one colour) is drawn in each piece it spans, clipped to it.</summary>
    private void DrawWithHints(DrawingContext context, LineView layout, Gap[] gaps, double left, double y)
    {
        double top = y + (metrics.LineHeight - layout.Height) / 2;
        TextLine line = layout.Text!.Line;
        // Where each hint stands, read once: the line's layout walks its runs for each.
        Span<double> boundaries = gaps.Length <= 64 ? stackalloc double[gaps.Length] : new double[gaps.Length];
        for (int i = 0; i < gaps.Length; i++)
        {
            boundaries[i] = layout.DistanceOf(gaps[i].Column);
        }
        double x = layout.X + line.Start;
        int piece = 0;
        double shift = 0;
        // By index: an enumerator of the runs would be made for each line each frame.
        IReadOnlyList<TextRun> runs = line.TextRuns;
        for (int r = 0; r < runs.Count; r++)
        {
            if (runs[r] is not DrawableTextRun drawable)
            {
                continue;
            }
            double end = x + drawable.Size.Width;
            // The piece the run begins in: past every hint at or before its start.
            while (piece < gaps.Length && boundaries[piece] <= x + Slack)
            {
                shift += gaps[piece++].Width;
            }
            double baseline = top + line.Baseline - drawable.Baseline;
            if (piece == gaps.Length || end <= boundaries[piece] + Slack)
            {
                drawable.Draw(context, new Point(left + shift + x, baseline));
            }
            else
            {
                // A hint inside the run: drawn in each piece it reaches, each clipped to its piece and moved with it.
                double from = piece == 0 ? double.NegativeInfinity : boundaries[piece - 1];
                double moved = shift;
                for (int at = piece; ; at++)
                {
                    double to = at < gaps.Length ? boundaries[at] : double.PositiveInfinity;
                    double clipFrom = Math.Max(from, x - metrics.CharWidth);
                    double clipTo = Math.Min(to, end + metrics.CharWidth);
                    if (clipTo > clipFrom)
                    {
                        using (context.PushClip(new Rect(left + moved + clipFrom, y, clipTo - clipFrom, metrics.LineHeight)))
                        {
                            drawable.Draw(context, new Point(left + moved + x, baseline));
                        }
                    }
                    if (at == gaps.Length || to >= end)
                    {
                        break;
                    }
                    moved += gaps[at].Width;
                    from = to;
                }
            }
            x = end;
        }
        double before = 0;
        for (int i = 0; i < gaps.Length; i++)
        {
            DrawHint(context, gaps[i], left + before + boundaries[i], y);
            before += gaps[i].Width;
        }
    }

    /// <summary>How far past a hint's place a run may begin, or before it end, and still count as beside it: a run's width
    /// summed and a column's distance read are the same place by different sums.</summary>
    private const double Slack = 0.01;

    private void DrawHint(DrawingContext context, Gap gap, double x, double y)
    {
        if (gap.Composing)
        {
            // Underlined until the input method commits it.
            gap.Label.Draw(context, new Point(x, y + (metrics.LineHeight - gap.Label.Height) / 2));
            context.FillRectangle(palette.Preedit, new Rect(x, y + metrics.LineHeight - 2, gap.Width, 1));
            return;
        }
        double pad = metrics.CharWidth / 2;
        double start = x + (gap.PadLeft ? pad : 0);
        Rect chip = new(start, y + 2, gap.Label.WidthIncludingTrailingWhitespace + pad, metrics.LineHeight - 4);
        context.FillRectangle(palette.InlayHintBack, chip, 3);
        gap.Label.Draw(context, new Point(start + pad / 2, y + (metrics.LineHeight - gap.Label.Height) / 2));
    }

    /// <summary>A hint's label: a little smaller than the code, in its own muted colour.</summary>
    private GenericTextRunProperties HintRun() => new(Typeface, FontSize * 0.88, foregroundBrush: palette.InlayHint);
}
