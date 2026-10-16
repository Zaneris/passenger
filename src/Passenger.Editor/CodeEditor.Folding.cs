using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Code folding, as Rider's: the host names the regions that fold (a member's body, a type's, the usings),
/// a folded one hides the lines after its first and shows its placeholder at that line's end, and the gutter's last
/// column has its chevrons. Everything that turns a line into a height goes through rows (<see cref="Rows"/>): the lines
/// left showing, looked up in <see cref="FoldLines"/>, made once for each text and set of regions, each one row or as many
/// as soft wrap makes it. A caret that lands in folded lines unfolds them, and the pointer resting on a placeholder shows
/// the lines it hides on a card (<see cref="FoldPreviewText"/>).</summary>
public sealed partial class CodeEditor
{
    /// <summary>The gutter's folding column, at its right edge against the code.</summary>
    private const double FoldWidth = 12;

    private IReadOnlyList<FoldRegion> folds = [];
    private FoldLines? foldLines;
    private readonly Dictionary<int, Rect> placeholders = [];
    private bool overGutter;

    /// <summary>The regions that fold, in offset order, each saying whether it is folded.</summary>
    public IReadOnlyList<FoldRegion> FoldRegions => folds;

    /// <summary>A region was folded or unfolded here (a chevron, a placeholder, a key, a caret landing in one): the
    /// regions as they now stand, for the host to keep.</summary>
    public event EventHandler<IReadOnlyList<FoldRegion>>? FoldsToggled;

    public void SetFoldRegions(IReadOnlyList<FoldRegion> regions)
    {
        if (ReferenceEquals(regions, folds))
        {
            return;
        }
        HideFoldPreview();
        folds = InOrder(regions) ? regions : [.. regions.OrderBy(r => r.Start)];
        InvalidateVisual();
        UpdateScrollBars();
        FollowInputMethod();
    }

    private static bool InOrder(IReadOnlyList<FoldRegion> regions)
    {
        for (int i = 1; i < regions.Count; i++)
        {
            if (regions[i].Start < regions[i - 1].Start)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The regions in lines for the text as it stands.</summary>
    private FoldLines Folding
    {
        get
        {
            TextSnapshot text = document.Snapshot;
            if (foldLines is not { } known || !ReferenceEquals(known.Text, text) || !ReferenceEquals(known.Folds, folds))
            {
                foldLines = known = new FoldLines(text, folds);
            }
            return known;
        }
    }

    /// <summary>The folded region whose placeholder ends a line, if any.</summary>
    private FoldRegion? FoldedAt(int line) => Folding.Placeholders.TryGetValue(line, out int index) ? folds[index] : null;

    /// <summary>The innermost region holding a line: its first line through its last.</summary>
    private int RegionAround(int line, Func<FoldRegion, bool> which)
    {
        TextSnapshot text = document.Snapshot;
        int best = -1;
        for (int i = 0; i < folds.Count; i++)
        {
            FoldRegion region = folds[i];
            int first = FoldLines.StartLineOf(text, region);
            int last = FoldLines.EndLineOf(text, region);
            if (last < 0 || !which(region))
            {
                continue;
            }
            if (line >= first && line <= last && last > first && (best < 0 || region.End - region.Start < folds[best].End - folds[best].Start))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>Rider's folding actions; false for one that is not this.</summary>
    private bool RunFolding(string action)
    {
        TextSnapshot text = document.Snapshot;
        int line = text.GetLineFromOffset(selections.Primary.Caret);
        switch (action)
        {
            case EditorActions.ToggleFold:
                int around = RegionAround(line, _ => true);
                if (around >= 0)
                {
                    SetFolded(i => i == around ? !folds[i].Folded : folds[i].Folded);
                }
                return true;
            case EditorActions.Fold:
                int open = RegionAround(line, r => !r.Folded);
                if (open >= 0)
                {
                    SetFolded(i => i == open || folds[i].Folded);
                }
                return true;
            case EditorActions.Unfold:
                int shut = RegionAround(line, r => r.Folded);
                if (shut >= 0)
                {
                    SetFolded(i => i != shut && folds[i].Folded);
                }
                return true;
            case EditorActions.FoldAll:
                SetFolded(_ => true);
                return true;
            case EditorActions.UnfoldAll:
                SetFolded(_ => false);
                return true;
            case EditorActions.FoldToDefinitions:
                // Rider's: the members' bodies and the doc comments folded, the rest left as it is.
                SetFolded(i => folds[i].Folded || folds[i].Kind is FoldKind.Body or FoldKind.DocComment);
                return true;
            case EditorActions.FoldDocComments:
                SetFolded(i => folds[i].Folded || folds[i].Kind == FoldKind.DocComment);
                return true;
            case EditorActions.UnfoldDocComments:
                SetFolded(i => folds[i].Folded && folds[i].Kind != FoldKind.DocComment);
                return true;
        }
        return false;
    }

    /// <summary>Sets which regions are folded, carets in newly folded lines moved to their fold's first line.</summary>
    private void SetFolded(Func<int, bool> folded)
    {
        FoldRegion[] next = [.. folds.Select((r, i) => r with { Folded = folded(i) })];
        if (next.SequenceEqual(folds))
        {
            return;
        }
        HideFoldPreview();
        folds = next;
        TextSnapshot text = document.Snapshot;
        if (selections.Items.Any(s => IsHidden(text.GetLineFromOffset(s.Caret))))
        {
            SetSelections(selections.With(s => IsHidden(text.GetLineFromOffset(s.Caret)) ? Selection.At(HeaderEnd(text, s.Caret)) : s), reveal: true);
        }
        UpdateScrollBars();
        InvalidateVisual();
        FollowInputMethod();
        FoldsToggled?.Invoke(this, folds);
    }

    /// <summary>The end of the line a folded offset's fold starts on, where a caret folded away goes.</summary>
    private int HeaderEnd(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(offset);
        FoldLines map = Folding;
        int k = map.RangeAtOrBefore(line);
        if (k >= 0 && line <= map.Hidden[k].Last)
        {
            line = map.Hidden[k].First - 1;
        }
        return text.GetLine(Math.Max(0, line)).End;
    }

    /// <summary>Unfolds whatever hides a caret, as Rider does for a caret that goes into folded code.</summary>
    private void UnfoldAroundCarets(SelectionSet carets)
    {
        FoldLines map = Folding;
        if (map.Hidden.Length == 0)
        {
            return;
        }
        TextSnapshot text = document.Snapshot;
        List<int> lines = [.. carets.Items.Select(s => text.GetLineFromOffset(s.Caret)).Where(IsHidden)];
        if (lines.Count == 0)
        {
            return;
        }
        HideFoldPreview();
        folds = [.. folds.Select(r => r.Folded && FoldLines.EndLineOf(text, r) is int end and >= 0 && lines.Any(l => l > FoldLines.StartLineOf(text, r) && l <= end) ? r with { Folded = false } : r)];
        UpdateScrollBars();
        FoldsToggled?.Invoke(this, folds);
    }

    /// <summary>A click in the folding column or on a placeholder: the fold there toggled. Whether it was one.</summary>
    private bool ClickFold(Point at)
    {
        if (PlaceholderAt(at) is { } chip && OutermostAt(chip.Line, r => r.Folded) is int shown)
        {
            SetFolded(i => i != shown && folds[i].Folded);
            return true;
        }
        if (!ShowLineNumbers || at.X < GutterWidth - FoldWidth || at.X >= GutterWidth)
        {
            return false;
        }
        // A chevron is on a line's first row.
        (int clicked, int row) = Rows.LineOfRow(RowAtY(at.Y));
        if (row == 0 && OutermostAt(clicked, _ => true) is int index)
        {
            SetFolded(i => i == index ? !folds[i].Folded : folds[i].Folded);
            return true;
        }
        return false;
    }

    /// <summary>The widest region of a kind that starts on a line and folds something, by its place in the list.</summary>
    private int? OutermostAt(int line, Func<FoldRegion, bool> which)
    {
        TextSnapshot text = document.Snapshot;
        int? best = null;
        for (int i = FirstStartingOn(text, line); i < folds.Count && FoldLines.StartLineOf(text, folds[i]) == line; i++)
        {
            FoldRegion region = folds[i];
            if (FoldLines.EndLineOf(text, region) > line && which(region) && (best is null || region.End > folds[best.Value].End))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>The chevrons in the folding column: a folded region's always, an open one's while the pointer is
    /// over the gutter, as Rider's new UI shows them.</summary>
    private void DrawFoldMarkers(DrawingContext context, int first, int last, double gutter)
    {
        double x = gutter - FoldWidth + 2;
        FoldLines map = Folding;
        if (!overGutter && map.Placeholders.Count == 0)
        {
            return;
        }
        TextSnapshot text = document.Snapshot;
        HashSet<int> drawn = [];
        for (int i = FirstStartingOn(text, first); i < folds.Count && FoldLines.StartLineOf(text, folds[i]) <= last; i++)
        {
            FoldRegion region = folds[i];
            int line = FoldLines.StartLineOf(text, region);
            if (FoldLines.EndLineOf(text, region) <= line || !(region.Folded || overGutter) || IsHidden(line) || !drawn.Add(line))
            {
                continue;
            }
            bool folded = map.Placeholders.ContainsKey(line);
            double middle = TopOf(line) - scroll.Y + metrics.LineHeight / 2;
            StreamGeometry chevron = new();
            using (StreamGeometryContext path = chevron.Open())
            {
                if (folded)
                {
                    path.BeginFigure(new Point(x + 2, middle - 4), false);
                    path.LineTo(new Point(x + 6, middle));
                    path.LineTo(new Point(x + 2, middle + 4));
                }
                else
                {
                    path.BeginFigure(new Point(x, middle - 2), false);
                    path.LineTo(new Point(x + 4, middle + 2));
                    path.LineTo(new Point(x + 8, middle - 2));
                }
            }
            context.DrawGeometry(null, new Pen(palette.LineNumber, 1.2), chevron);
        }
    }

    /// <summary>A folded region's placeholder after its first line's code, on a chip a click unfolds.</summary>
    private double DrawPlaceholder(DrawingContext context, int line, FoldRegion region, double x, double y)
    {
        x += metrics.CharWidth;
        TextLine label = labels.Get((region.Placeholder, -8), () => Format(region.Placeholder, [], RunWith(palette.InlineValue)));
        Rect chip = new(x, y + 2, label.WidthIncludingTrailingWhitespace + metrics.CharWidth, metrics.LineHeight - 4);
        context.FillRectangle(palette.InlayHintBack, chip, 3);
        label.Draw(context, new Point(x + metrics.CharWidth / 2, y + (metrics.LineHeight - label.Height) / 2));
        placeholders[line] = chip;
        return chip.Right;
    }

    /// <summary>The placeholder drawn under a point, if any: its line and its chip.</summary>
    private (int Line, Rect Chip)? PlaceholderAt(Point at)
    {
        foreach ((int line, Rect box) in placeholders)
        {
            if (box.Contains(at))
            {
                return (line, box);
            }
        }
        return null;
    }

    /// <summary>The lines the card over a placeholder shows, one to a line of text, or null while none shows: what the
    /// pointer resting on a folded region's placeholder sees of the code it hides.</summary>
    public string? FoldPreviewText => preview?.Text;

    private FoldPreview? preview;

    /// <summary>Rider's preview of folded code: the lines a placeholder's fold hides, coloured as the editor colours them
    /// and less the indent they share, on a card under the placeholder (over it when there is more room there), as many
    /// as the editor has room for.</summary>
    private void ShowFoldPreview(int line, Rect chip)
    {
        if (preview is { } shown && shown.Line == line)
        {
            return;
        }
        HideFoldPreview();
        TextSnapshot text = document.Snapshot;
        if (FoldedAt(line) is not { } region || FoldLines.EndLineOf(text, region) is not (int end and >= 0) || end <= line)
        {
            return;
        }
        (double across, double down) = PreviewPadding;
        double below = Bounds.Height - horizontal.DesiredSize.Height - chip.Bottom - 2;
        double above = chip.Top - 2;
        int room = Math.Max(1, (int)((Math.Max(below, above) - (2 * down)) / metrics.LineHeight));
        int first = line + 1;
        int last = Math.Min(end, first + room - 1);
        int shared = int.MaxValue;
        for (int i = first; i <= last; i++)
        {
            LineSpan span = text.GetLine(i);
            int indent = 0;
            while (indent < span.Length && text[span.Start + indent] is ' ' or '\t')
            {
                indent++;
            }
            if (indent < span.Length)
            {
                shared = Math.Min(shared, indent);
            }
        }
        shared = shared == int.MaxValue ? 0 : shared;
        int from = text.GetLineStart(first);
        IReadOnlyList<SyntaxSpan> spans = Colours(text, from, text.GetLine(last).End - from);
        int index = 0;
        TextLine[] lines = new TextLine[last - first + 1];
        string[] said = new string[lines.Length];
        double widest = 0;
        for (int i = first; i <= last; i++)
        {
            LineSpan span = text.GetLine(i);
            int start = span.Start + Math.Min(shared, span.Length);
            said[i - first] = text.GetText(start, span.End - start);
            lines[i - first] = Format(said[i - first], Runs(spans, ref index, start, span.End), null);
            widest = Math.Max(widest, lines[i - first].WidthIncludingTrailingWhitespace);
        }
        double height = (lines.Length * metrics.LineHeight) + (2 * down);
        double right = Bounds.Width - ScrollRailWidth - 4;
        double width = Math.Min(widest + (2 * across), Math.Max(metrics.CharWidth * 8, right - GutterWidth));
        double x = Math.Max(GutterWidth, Math.Min(chip.Left, right - width));
        double y = below >= height || below >= above ? chip.Bottom + 2 : chip.Top - 2 - height;
        preview = new FoldPreview(line, chip, new Rect(x, y, width, height), lines, string.Join('\n', said));
        InvalidateVisual();
    }

    /// <summary>Takes the preview card away: the pointer left its placeholder, or the folds, the text or the scroll changed.</summary>
    private void HideFoldPreview()
    {
        if (preview is not { } shown)
        {
            return;
        }
        preview = null;
        foreach (TextLine line in shown.Lines)
        {
            line.Dispose();
        }
        InvalidateVisual();
    }

    /// <summary>The preview card, over everything else the editor draws.</summary>
    private void DrawFoldPreview(DrawingContext context)
    {
        if (preview is not { } shown)
        {
            return;
        }
        (double across, double down) = PreviewPadding;
        context.DrawRectangle(palette.Background, new Pen(palette.PopupBorder, 1), shown.Card.Deflate(0.5), 4, 4);
        using (context.PushClip(shown.Card.Deflate(1)))
        {
            for (int i = 0; i < shown.Lines.Length; i++)
            {
                TextLine line = shown.Lines[i];
                double top = shown.Card.Top + down + (i * metrics.LineHeight) + ((metrics.LineHeight - line.Height) / 2);
                line.Draw(context, new Point(shown.Card.Left + across, top));
            }
        }
    }

    /// <summary>The room the preview card leaves around its lines: a character's width either side, half that above and below.</summary>
    private (double Across, double Down) PreviewPadding => (Math.Round(metrics.CharWidth), Math.Round(metrics.CharWidth / 2));

    /// <summary>A preview card: the placeholder it is for, by its line and chip, where the card is, and its lines formatted
    /// and as text.</summary>
    private sealed record FoldPreview(int Line, Rect Chip, Rect Card, TextLine[] Lines, string Text);

    /// <summary>The first region starting on a line at or after <paramref name="line"/>, found by where they start.</summary>
    private int FirstStartingOn(TextSnapshot text, int line)
    {
        int offset = line < text.LineCount ? text.GetLineStart(line) : text.Length + 1;
        int low = 0;
        int high = folds.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (folds[middle].Start < offset)
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

    /// <summary>The folded regions of one text in its lines: the folded lines as ranges in order with the lines folded
    /// before each, and the widest folded region starting on each line, whose placeholder ends it. Made for each change
    /// to the text, so it reads only the folded regions; the others' lines are found when one is drawn or asked for.</summary>
    private sealed class FoldLines
    {
        public FoldLines(TextSnapshot text, IReadOnlyList<FoldRegion> folds)
        {
            Text = text;
            Folds = folds;
            List<(int First, int Last)> ranges = [];
            for (int i = 0; i < folds.Count; i++)
            {
                FoldRegion region = folds[i];
                if (!region.Folded)
                {
                    continue;
                }
                int start = StartLineOf(text, region);
                if (start == int.MaxValue)
                {
                    continue;
                }
                if (!Placeholders.TryGetValue(start, out int widest) || region.End > folds[widest].End)
                {
                    Placeholders[start] = i;
                }
                int first = start + 1;
                int last = EndLineOf(text, region);
                if (last < first)
                {
                    continue;
                }
                if (ranges.Count > 0 && first <= ranges[^1].Last + 1)
                {
                    ranges[^1] = (ranges[^1].First, Math.Max(ranges[^1].Last, last));
                }
                else
                {
                    ranges.Add((first, last));
                }
            }
            Hidden = [.. ranges];
            HiddenBefore = new int[Hidden.Length + 1];
            for (int k = 0; k < Hidden.Length; k++)
            {
                HiddenBefore[k + 1] = HiddenBefore[k] + Hidden[k].Last - Hidden[k].First + 1;
            }
        }

        public TextSnapshot Text { get; }

        public IReadOnlyList<FoldRegion> Folds { get; }

        public (int First, int Last)[] Hidden { get; }

        /// <summary>How many lines the ranges before each one hide; one more than the ranges, the last all of them.</summary>
        public int[] HiddenBefore { get; }

        public int HiddenCount => HiddenBefore[^1];

        public Dictionary<int, int> Placeholders { get; } = [];

        /// <summary>A region's first line; past every line for one starting past the text.</summary>
        public static int StartLineOf(TextSnapshot text, FoldRegion region) => region.Start <= text.Length ? text.GetLineFromOffset(region.Start) : int.MaxValue;

        /// <summary>A region's last line; none for one ending past the text.</summary>
        public static int EndLineOf(TextSnapshot text, FoldRegion region) => region.End <= text.Length ? text.GetLineFromOffset(region.End) : -1;

        /// <summary>Whether a fold hides a line.</summary>
        public bool IsHidden(int line)
        {
            int k = RangeAtOrBefore(line);
            return k >= 0 && line <= Hidden[k].Last;
        }

        /// <summary>The line itself, or for one a fold hides the line its fold starts on, which shows.</summary>
        public int Shown(int line)
        {
            int k = RangeAtOrBefore(line);
            return k >= 0 && line <= Hidden[k].Last ? Hidden[k].First - 1 : line;
        }

        /// <summary>The row a line shows on among the lines left showing; a folded line is on its fold's first line's.</summary>
        public int RowOf(int line)
        {
            int k = RangeAtOrBefore(line);
            if (k < 0)
            {
                return line;
            }
            return line <= Hidden[k].Last ? Hidden[k].First - 1 - HiddenBefore[k] : line - HiddenBefore[k + 1];
        }

        /// <summary>The line a row of the lines left showing is, past the last row the lines past the end.</summary>
        public int LineOfRow(int row)
        {
            // A range comes before the row's line when its first line, less the lines folded before it, is at most the row.
            int low = 0;
            int high = Hidden.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (Hidden[middle].First - HiddenBefore[middle] <= row)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return row + HiddenBefore[low];
        }

        /// <summary>The last folded range starting at or before a line, or -1.</summary>
        public int RangeAtOrBefore(int line)
        {
            int low = 0;
            int high = Hidden.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (Hidden[middle].First <= line)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low - 1;
        }
    }
}
