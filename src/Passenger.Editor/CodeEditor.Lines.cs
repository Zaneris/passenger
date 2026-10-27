using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Rows as the editor draws and measures them (a line, or a row soft wrap made of it): formatted with their colours
/// once for each text and colouring, found again by where they start in each frame, copying and hashing nothing; after a
/// key only the changed row is formatted again. Drawing, a caret's x and a click's column use that one formatted row. A
/// line longer than <see cref="LongLineLength"/> is formatted only for the columns on screen, placed by its
/// <see cref="LongLine"/> measure, so a key on a line of a million characters costs about what one on a short line does.</summary>
public sealed partial class CodeEditor
{
    /// <summary>The longest line formatted whole; longer, a window of its columns.</summary>
    private const int LongLineLength = 2048;

    private readonly LineViews views = new();
    private Advances? advances;
    private int colouring;

    /// <summary>A frame's rows' views and those it formats whole, kept from frame to frame.</summary>
    private LineView[] frameViews = [];
    private readonly List<LineView> frameWhole = [];

    /// <summary>The text whose rows on screen the highlighter was last told of.</summary>
    private TextSnapshot? toldShowing;

    /// <summary>How many characters the editor has given the text formatter: each line, window and label it formatted.
    /// What a key or a frame costs to draw.</summary>
    public long CharactersFormatted { get; private set; }

    /// <summary>How many characters the editor has asked its highlighter to colour.</summary>
    public long CharactersColoured { get; private set; }

    /// <summary>How many frames the editor has drawn: what a key costs to draw.</summary>
    public long Frames { get; private set; }

    /// <summary>The caret's blinks, a frame asked for each, which a count of a key's frames leaves out.</summary>
    public long Blinks { get; private set; }

    /// <summary>The views of the rows a frame shows: the ones they had while their text and colours (and a long line's
    /// window) still serve, the rest made, their colours asked for a run of neighbouring rows at a time.</summary>
    private LineView[] ViewsOf(TextSnapshot text, List<RowSpan> shown, double viewLeft, double viewWidth)
    {
        views.Use(text, colouring);
        TellShowing(text, shown);
        if (frameViews.Length < shown.Count)
        {
            frameViews = new LineView[Math.Max(shown.Count, frameViews.Length * 2)];
        }
        LineView[] found = frameViews;
        List<LineView> whole = frameWhole;
        whole.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            LineView view = views[shown[i]] ?? views.Add(NewView(text, shown[i]));
            found[i] = view;
            if (view.Long is { } measure)
            {
                double hints = HintsWidth(GapsOn(view.Span));
                int from = Math.Max(0, measure.ColumnAt(viewLeft - hints) - 1);
                int to = Math.Min(view.Span.Length, measure.ColumnAt(viewLeft + viewWidth) + 1);
                if (!view.Covers(from, to))
                {
                    FormatWindow(text, view, from, to);
                }
            }
            else if (view.Text is null)
            {
                whole.Add(view);
            }
            view.Use(views.Frame);
        }
        FormatWhole(text, whole);
        whole.Clear();
        return found;
    }

    /// <summary>Tells the highlighter each run of neighbouring rows on screen, once for each text: rows carried through an
    /// edit ask it for no colours, yet it reads ahead what the screen shows.</summary>
    private void TellShowing(TextSnapshot text, List<RowSpan> shown)
    {
        if (highlighter is null || ReferenceEquals(toldShowing, text) || shown.Count == 0)
        {
            return;
        }
        toldShowing = text;
        for (int i = 0; i < shown.Count;)
        {
            int j = i;
            while (j + 1 < shown.Count && shown[j + 1].Start == shown[j].EndIncludingBreak)
            {
                j++;
            }
            highlighter.Showing(text, shown[i].Start, shown[j].EndIncludingBreak - shown[i].Start);
            i = j + 1;
        }
    }

    /// <summary>A row's view to measure with: the one drawn, or one made now, which the next frame draws.</summary>
    private LineView ViewOf(TextSnapshot text, RowSpan row)
    {
        views.Use(text, colouring);
        LineView view = views[row] ?? views.Add(NewView(text, row));
        if (view.Long is null && view.Text is null)
        {
            List<LineView> one = frameWhole;
            int from = one.Count;
            one.Add(view);
            FormatWhole(text, one, from);
            one.RemoveAt(from);
        }
        view.Use(views.Frame);
        return view;
    }

    private LineView NewView(TextSnapshot text, RowSpan span)
    {
        if (span.Length <= LongLineLength)
        {
            return new LineView(span, null);
        }
        return new LineView(span, new LongLine(text, span.Start, span.Length, Measures()));
    }

    /// <summary>Whole rows formatted with their colours, which are asked for once for each run of neighbouring rows: those
    /// of the list from <paramref name="from"/> on.</summary>
    private void FormatWhole(TextSnapshot text, List<LineView> lines, int from = 0)
    {
        for (int i = from; i < lines.Count;)
        {
            int j = i;
            while (j + 1 < lines.Count && lines[j + 1].Span.Start == lines[j].Span.EndIncludingBreak)
            {
                j++;
            }
            int start = lines[i].Span.Start;
            IReadOnlyList<SyntaxSpan> spans = Colours(text, start, lines[j].Span.EndIncludingBreak - start);
            int index = 0;
            for (int k = i; k <= j; k++)
            {
                RowSpan span = lines[k].Span;
                LineRun[] runs = Runs(spans, ref index, span.Start, span.End);
                string content = text.GetText(span.Start, span.Length);
                FormatKey key = new(content, runs);
                lines[k].Text = views.Formatted(key) ?? views.Add(key, Format(content, runs, null));
                lines[k].Runs = runs;
                lines[k].Use(views.Frame);
            }
            i = j + 1;
        }
    }

    /// <summary>A long line's window: from the kept boundary before the columns on screen to the one after them, so a
    /// little scrolling keeps it, and between graphemes, so it is laid out as the whole line would be there.</summary>
    private void FormatWindow(TextSnapshot text, LineView view, int from, int to)
    {
        RowSpan span = view.Span;
        LongLine measure = view.Long!;
        int start = measure.KeptAtOrBefore(from);
        int end = measure.KeptAtOrAfter(to);
        IReadOnlyList<SyntaxSpan> spans = Colours(text, span.Start + start, end - start);
        int index = 0;
        LineRun[] runs = Runs(spans, ref index, span.Start + start, span.Start + end);
        string content = text.GetText(span.Start + start, end - start);
        FormatKey key = new(content, runs);
        view.Window(start, end, measure.XOf(start), views.Formatted(key) ?? views.Add(key, Format(content, runs, null)));
        view.Use(views.Frame);
    }

    private IReadOnlyList<SyntaxSpan> Colours(TextSnapshot text, int start, int length)
    {
        if (highlighter is null)
        {
            return [];
        }
        CharactersColoured += length;
        return highlighter.GetSpans(text, start, length);
    }

    /// <summary>The coloured stretches of the text from <paramref name="start"/> to <paramref name="end"/>, in its columns,
    /// read on from <paramref name="index"/> in spans sorted by where they start.</summary>
    private static LineRun[] Runs(IReadOnlyList<SyntaxSpan> spans, ref int index, int start, int end)
    {
        while (index < spans.Count && spans[index].End <= start)
        {
            index++;
        }
        // Counted first, so each line's colours are one array of their size.
        int count = 0;
        for (int i = index; i < spans.Count && spans[i].Start < end; i++)
        {
            if (Math.Min(spans[i].End, end) > Math.Max(spans[i].Start, start) && spans[i].Kind != TokenKind.Text)
            {
                count++;
            }
        }
        if (count == 0)
        {
            return [];
        }
        LineRun[] runs = new LineRun[count];
        int made = 0;
        for (int i = index; made < count; i++)
        {
            SyntaxSpan s = spans[i];
            int from = Math.Max(s.Start, start) - start;
            int to = Math.Min(s.End, end) - start;
            if (to > from && s.Kind != TokenKind.Text)
            {
                runs[made++] = new LineRun(from, to - from, s.Kind);
            }
        }
        return runs;
    }

    /// <summary>Whether the coloured stretches from <paramref name="start"/> to <paramref name="end"/>, read on from
    /// <paramref name="index"/>, are <paramref name="runs"/>, as <see cref="Runs"/> would make them, making nothing.</summary>
    private static bool SameRuns(IReadOnlyList<SyntaxSpan> spans, ref int index, int start, int end, LineRun[] runs)
    {
        while (index < spans.Count && spans[index].End <= start)
        {
            index++;
        }
        int made = 0;
        for (int i = index; i < spans.Count && spans[i].Start < end; i++)
        {
            SyntaxSpan s = spans[i];
            int from = Math.Max(s.Start, start) - start;
            int to = Math.Min(s.End, end) - start;
            if (to > from && s.Kind != TokenKind.Text)
            {
                if (made == runs.Length || runs[made] != new LineRun(from, to - from, s.Kind))
                {
                    return false;
                }
                made++;
            }
        }
        return made == runs.Length;
    }

    private TextLine Format(string content, LineRun[] runs, TextRunProperties? only)
    {
        TextRunProperties plain = only ?? Run(TokenKind.Text);
        ITextSource source = runs.Length == 0 ? new SingleRun(content, plain) : new ColouredRuns(content, runs, plain, Run);
        CharactersFormatted += content.Length;
        return TextFormatter.Current.FormatLine(source, 0, double.PositiveInfinity, new LineProperties(plain, metrics.CharWidth * Indent.TabWidth))!;
    }

    private double Measure(string text)
    {
        using TextLine line = Format(text, [], null);
        return line.WidthIncludingTrailingWhitespace;
    }

    /// <summary>Lets go of every formatted line, as a new text or a new look (font, theme) needs.</summary>
    private void ForgetLines()
    {
        views.Clear();
        shapes.Clear();
        labels.Clear();
        rowGaps.Clear();
        numbers.Clear();
        advances = null;
    }

    /// <summary>A coloured stretch of one line, in its columns.</summary>
    private readonly record struct LineRun(int Start, int Length, TokenKind Kind);

    /// <summary>What a formatted line shows: its text and colours, its hash worked out once, so finding or dropping it
    /// never hashes the text again.</summary>
    private readonly struct FormatKey : IEquatable<FormatKey>
    {
        private readonly int hash;

        public FormatKey(string text, LineRun[] runs)
        {
            Text = text;
            Runs = runs;
            HashCode combined = new();
            combined.Add(text);
            foreach (LineRun run in runs)
            {
                combined.Add(run);
            }
            hash = combined.ToHashCode();
        }

        public string Text { get; }
        public LineRun[] Runs { get; }

        public bool Equals(FormatKey other) =>
            hash == other.hash
            && (ReferenceEquals(Text, other.Text) || Text == other.Text)
            && (ReferenceEquals(Runs, other.Runs) || Runs.AsSpan().SequenceEqual(other.Runs));

        public override bool Equals(object? obj) => obj is FormatKey other && Equals(other);
        public override int GetHashCode() => hash;
    }

    /// <summary>A formatted line, and the last frame that drew or measured with it.</summary>
    private sealed class Formatted(TextLine line)
    {
        public TextLine Line { get; } = line;
        public long Frame { get; private set; }
        public bool Disposed { get; private set; }

        public void Touch(long frame) => Frame = frame;

        public void Dispose()
        {
            Disposed = true;
            Line.Dispose();
        }
    }

    /// <summary>A row as the editor draws and measures it: formatted whole, or for a long line the window of columns
    /// <see cref="From"/> to <see cref="To"/> placed at <see cref="X"/> by its measure, which gives every column outside it.</summary>
    private sealed class LineView(RowSpan span, LongLine? measure)
    {
        public RowSpan Span { get; private set; } = span;
        public LongLine? Long { get; } = measure;
        public Formatted? Text { get; set; }

        /// <summary>The colours a whole line was formatted with.</summary>
        public LineRun[]? Runs { get; set; }

        public int From { get; private set; }
        public int To { get; private set; }
        public double X { get; private set; }

        public double Width => Long?.Width ?? Text!.Line.WidthIncludingTrailingWhitespace;

        /// <summary>The width to the end of the line's last character that is not a space.</summary>
        public double CodeWidth => Long?.CodeWidth ?? Text!.Line.Width;

        public double Height => Text!.Line.Height;

        public double Baseline => Text!.Line.Baseline;

        public bool Covers(int from, int to) => Text is not null && From <= from && To >= to;

        /// <summary>The last frame that drew or measured with this row.</summary>
        public long Used { get; private set; }

        public void Use(long frame)
        {
            Used = frame;
            Text?.Touch(frame);
        }

        public void Window(int from, int to, double x, Formatted text)
        {
            From = from;
            To = to;
            X = x;
            Text = text;
        }

        public double DistanceOf(int column)
        {
            if (Long is null)
            {
                return Text!.Line.GetDistanceFromCharacterHit(new CharacterHit(column));
            }
            return Text is not null && column >= From && column <= To
                ? X + Text.Line.GetDistanceFromCharacterHit(new CharacterHit(column - From))
                : Long.XOf(column);
        }

        /// <summary>The caret's column nearest an x.</summary>
        public int ColumnAt(double x)
        {
            if (Long is null || (Text is not null && x >= X && x <= X + Text.Line.WidthIncludingTrailingWhitespace))
            {
                CharacterHit hit = Text!.Line.GetCharacterHitFromDistance(x - X);
                return From + hit.FirstCharacterIndex + hit.TrailingLength;
            }
            return Long.ColumnAt(x);
        }

        public void Draw(DrawingContext context, double left, double top) => Text!.Line.Draw(context, new Point(left + X, top));

        /// <summary>The same row of the text an edit made, which it did not reach: <paramref name="shift"/> characters and
        /// <paramref name="lines"/> lines on from where it was.</summary>
        public void Follow(TextSnapshot text, int shift, int lines)
        {
            Span = Span with { Start = Span.Start + shift, Line = Span.Line + lines };
            Long?.Follow(text, Span.Start);
        }
    }

    /// <summary>The rows drawn or measured since the text or its colours last changed, by where they start, and every
    /// formatted row alive, by what it shows. A frame disposes the formatted rows it did not use, so they never outgrow the
    /// screen.</summary>
    private sealed class LineViews
    {
        private Dictionary<int, LineView> byLine = [];
        private Dictionary<int, LineView> spare = [];
        private readonly Dictionary<FormatKey, Formatted> formatted = [];
        private readonly List<LineView> scratch = [];
        private readonly List<FormatKey> stale = [];
        private TextSnapshot? text;
        private int colouring;

        /// <summary>The frame being drawn: what is used before it ends lives through it.</summary>
        public long Frame { get; private set; } = 1;

        /// <summary>A row's view, while it is still that row: soft wrap at another width makes other rows start there.</summary>
        public LineView? this[RowSpan row] => byLine.TryGetValue(row.Start, out LineView? view) && view.Span == row ? view : null;

        /// <summary>Starts using a text and colouring; the views of another are dropped (a long line's measure kept while
        /// its text is), their formatted lines kept for lines that show the same until the frame ends.</summary>
        public void Use(TextSnapshot snapshot, int colours)
        {
            if (!ReferenceEquals(text, snapshot))
            {
                byLine.Clear();
            }
            else if (colouring != colours)
            {
                DropShort(all: true);
            }
            text = snapshot;
            colouring = colours;
        }

        /// <summary>The views carried through an edit: those of the lines it did not reach kept, moved with the text after it,
        /// as the highlighter carries their colours, so the next frame makes again only the rows it changed. Lines ending
        /// where it starts, or starting where it ends, count as reached, as the highlighter counts them.</summary>
        public void Follow(DocumentChangedEventArgs change)
        {
            if (!ReferenceEquals(text, change.Before))
            {
                return;
            }
            IReadOnlyList<TextEdit> edits = change.Edits;
            if (edits.Count == 0 || byLine.Count == 0)
            {
                byLine.Clear();
                text = change.After;
                return;
            }
            TextSnapshot before = change.Before;
            int first = edits[0].Start;
            int last = edits[^1].Start + edits[^1].Length;
            int shift = change.After.Length - before.Length;
            int lines = change.After.LineCount - before.LineCount;
            spare.Clear();
            foreach ((int start, LineView view) in byLine)
            {
                LineSpan line = before.GetLine(view.Span.Line);
                if (line.EndIncludingBreak < first)
                {
                    view.Follow(change.After, 0, 0);
                    spare[start] = view;
                }
                else if (line.Start > last)
                {
                    view.Follow(change.After, shift, lines);
                    spare[start + shift] = view;
                }
            }
            (byLine, spare) = (spare, byLine);
            spare.Clear();
            text = change.After;
        }

        /// <summary>Drops the views of rows (all, or those this frame did not use or whose formatted line was disposed), a
        /// long one keeping its measure, which read the whole line, and letting go of its window.</summary>
        private void DropShort(bool all)
        {
            scratch.Clear();
            foreach (LineView view in byLine.Values)
            {
                if (all || view.Used != Frame || view.Text is { Disposed: true })
                {
                    scratch.Add(view);
                }
            }
            foreach (LineView view in scratch)
            {
                if (view.Long is null)
                {
                    byLine.Remove(view.Span.Start);
                }
                else
                {
                    view.Text = null;
                }
            }
            scratch.Clear();
        }

        public LineView Add(LineView view)
        {
            byLine[view.Span.Start] = view;
            return view;
        }

        /// <summary>Whether the lines in view of a text would take the colours they have from their highlighter now; if so
        /// they are kept as they are. A long line's window is never kept so.</summary>
        public bool Keep(TextSnapshot snapshot, Func<TextSnapshot, int, int, IReadOnlyList<SyntaxSpan>> colours)
        {
            if (!ReferenceEquals(text, snapshot))
            {
                // Made again for the text in view at the next frame anyway, with the colours as they are by then.
                return true;
            }
            List<LineView> shown = scratch;
            shown.Clear();
            foreach (LineView view in byLine.Values)
            {
                if (view.Long is not null && view.Text is not null)
                {
                    shown.Clear();
                    return false;
                }
                if (view.Runs is not null && view.Text is not null)
                {
                    shown.Add(view);
                }
            }
            shown.Sort(ByStart);
            try
            {
                for (int i = 0; i < shown.Count;)
                {
                    int j = i;
                    while (j + 1 < shown.Count && shown[j + 1].Span.Start == shown[j].Span.EndIncludingBreak)
                    {
                        j++;
                    }
                    int start = shown[i].Span.Start;
                    IReadOnlyList<SyntaxSpan> spans = colours(snapshot, start, shown[j].Span.EndIncludingBreak - start);
                    int index = 0;
                    for (int k = i; k <= j; k++)
                    {
                        if (!SameRuns(spans, ref index, shown[k].Span.Start, shown[k].Span.End, shown[k].Runs!))
                        {
                            return false;
                        }
                    }
                    i = j + 1;
                }
                return true;
            }
            finally
            {
                shown.Clear();
            }
        }

        private static readonly Comparison<LineView> ByStart = (a, b) => a.Span.Start.CompareTo(b.Span.Start);

        /// <summary>The line formatted to show this, kept on for this frame, if there is one.</summary>
        public Formatted? Formatted(FormatKey key)
        {
            if (!formatted.TryGetValue(key, out Formatted? line))
            {
                return null;
            }
            line.Touch(Frame);
            return line;
        }

        public Formatted Add(FormatKey key, TextLine text)
        {
            Formatted line = new(text);
            formatted[key] = line;
            line.Touch(Frame);
            return line;
        }

        public void EndFrame()
        {
            stale.Clear();
            foreach ((FormatKey key, Formatted line) in formatted)
            {
                if (line.Frame != Frame)
                {
                    line.Dispose();
                    stale.Add(key);
                }
            }
            foreach (FormatKey key in stale)
            {
                formatted.Remove(key);
            }
            stale.Clear();
            // A row not drawn or measured in this frame is made again when next shown, as it may be hidden or scrolled away.
            DropShort(all: false);
            Frame++;
        }

        public void Clear()
        {
            foreach (Formatted line in formatted.Values)
            {
                line.Dispose();
            }
            formatted.Clear();
            byLine.Clear();
            text = null;
        }
    }

    /// <summary>The length of the grapheme at a place: one for printable ASCII that no mark follows.</summary>
    private static int Grapheme(ReadOnlySpan<char> chars, int at) =>
        chars[at] is >= ' ' and <= '~' && (at + 1 == chars.Length || chars[at + 1] < '\u0080') ? 1
        : Math.Max(1, StringInfo.GetNextTextElementLength(chars[at..]));

    /// <summary>Where a long line's columns fall, without formatting it: each grapheme's width as the formatter lays it alone
    /// (a tab's fixed width), summed, and the first grapheme boundary from every <see cref="Step"/>-th column kept with its
    /// x, so a column's x or an x's column reads a step at most. Printable ASCII in a monospaced font keeps none: a
    /// column's x is a product. Only shaping across graphemes (Arabic's joined letters) lays a line out otherwise.</summary>
    private sealed class LongLine
    {
        public const int Step = 256;

        /// <summary>How far past what it reads a grapheme may run: one longer is cut there.</summary>
        private const int Overrun = 64;

        private TextSnapshot text;
        private int start;
        private readonly Advances advances;
        private readonly int[]? kept;
        private readonly double[]? keptX;

        public LongLine(TextSnapshot text, int start, int length, Advances advances)
        {
            this.text = text;
            this.start = start;
            Length = length;
            this.advances = advances;
            bool plain = !double.IsNaN(advances.Uniform);
            if (plain)
            {
                text.VisitChunks(start, length, chunk => plain = plain && !chunk.Span.ContainsAnyExceptInRange(' ', '~'));
            }
            if (plain)
            {
                Width = Length * advances.Uniform;
            }
            else
            {
                kept = new int[(Length / Step) + 1];
                keptX = new double[kept.Length];
                Width = Scan(kept, keptX);
            }
            CodeWidth = XOf(CodeEnd(text, start, length));
        }

        public int Length { get; }
        public double Width { get; }
        public double CodeWidth { get; }

        /// <summary>The same characters in a text an edit made, which it did not reach, starting at <paramref name="at"/>.</summary>
        public void Follow(TextSnapshot moved, int at)
        {
            text = moved;
            start = at;
        }

        /// <summary>The kept grapheme boundary at or before a column.</summary>
        public int KeptAtOrBefore(int column)
        {
            if (kept is null)
            {
                return column / Step * Step;
            }
            int k = Math.Min(column / Step, kept.Length - 1);
            return kept[k] <= column ? kept[k] : kept[k - 1];
        }

        /// <summary>The kept grapheme boundary at or after a column, or the line's end.</summary>
        public int KeptAtOrAfter(int column)
        {
            int k = (column + Step - 1) / Step;
            if (kept is null)
            {
                return Math.Min(Length, k * Step);
            }
            return k < kept.Length ? kept[k] : Length;
        }

        public double XOf(int column)
        {
            column = Math.Clamp(column, 0, Length);
            if (kept is null)
            {
                return column * advances.Uniform;
            }
            int k = Math.Min(column / Step, kept.Length - 1);
            if (kept[k] > column)
            {
                k--;
            }
            return Walk(kept[k], keptX![k], column, double.NaN).X;
        }

        /// <summary>The caret's column nearest an x: after the grapheme under it when past its middle.</summary>
        public int ColumnAt(double x)
        {
            if (x <= 0)
            {
                return 0;
            }
            if (x >= Width)
            {
                return Length;
            }
            if (kept is null)
            {
                int column = (int)(x / advances.Uniform);
                return Math.Min(Length, x - (column * advances.Uniform) >= advances.Uniform / 2 ? column + 1 : column);
            }
            int low = 0;
            int high = keptX!.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (keptX[middle] <= x)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            int k = Math.Max(0, low - 1);
            return Walk(kept[k], keptX[k], k + 1 < kept.Length ? kept[k + 1] : Length, x).Column;
        }

        /// <summary>Reads on a grapheme at a time from a kept boundary and its x toward <paramref name="to"/>: the x of the
        /// grapheme that holds it (its own, at a boundary), or, should the x <paramref name="find"/> come first, the
        /// boundary nearest that.</summary>
        private (double X, int Column) Walk(int from, double x, int to, double find)
        {
            int read = Math.Min(Length, to + Overrun) - from;
            Span<char> chars = read <= (2 * Step) + (2 * Overrun) ? stackalloc char[(2 * Step) + (2 * Overrun)] : new char[read];
            chars = chars[..read];
            text.CopyTo(start + from, chars);
            int i = 0;
            while (from + i < to)
            {
                int length = Grapheme(chars, i);
                double advance = Of(chars.Slice(i, length));
                if (find < x + advance)
                {
                    return (x, find - x >= advance / 2 ? from + i + length : from + i);
                }
                if (from + i + length > to)
                {
                    break;
                }
                x += advance;
                i += length;
            }
            return (x, from + i);
        }

        /// <summary>The whole line read a block at a time, keeping the boundaries; its width.</summary>
        private double Scan(int[] marks, double[] xs)
        {
            const int Block = 4096;
            char[] buffer = new char[Block + Overrun];
            double uniform = advances.Uniform;
            int column = 0;
            double x = 0;
            int next = 0;
            while (column < Length)
            {
                int read = Math.Min(buffer.Length, Length - column);
                text.CopyTo(start + column, buffer.AsSpan(0, read));
                ReadOnlySpan<char> chars = buffer.AsSpan(0, read);
                int limit = Math.Min(Block, read);
                int i = 0;
                while (i < limit)
                {
                    for (; next < marks.Length && next * Step <= column + i; next++)
                    {
                        marks[next] = column + i;
                        xs[next] = x;
                    }
                    if (!double.IsNaN(uniform))
                    {
                        int run = chars[i..].IndexOfAnyExceptInRange(' ', '~');
                        run = run < 0 ? read - i : run;
                        if (i + run < read || column + read < Length)
                        {
                            // The last may begin a grapheme with what follows it.
                            run--;
                        }
                        run = Math.Min(run, limit - i);
                        if (run > 0)
                        {
                            for (; next < marks.Length && next * Step < column + i + run; next++)
                            {
                                marks[next] = next * Step;
                                xs[next] = x + (((next * Step) - column - i) * uniform);
                            }
                            x += run * uniform;
                            i += run;
                            continue;
                        }
                    }
                    int length = Grapheme(chars, i);
                    x += Of(chars.Slice(i, length));
                    i += length;
                }
                column += i;
            }
            for (; next < marks.Length; next++)
            {
                marks[next] = Length;
                xs[next] = x;
            }
            return x;
        }

        private double Of(ReadOnlySpan<char> grapheme) => grapheme is ['\t'] ? advances.Tab : advances.Of(grapheme);

        /// <summary>Where a line's code ends, before the spaces after it, read back from its end a block at a time.</summary>
        private static int CodeEnd(TextSnapshot text, int start, int length)
        {
            Span<char> block = stackalloc char[Step];
            int end = length;
            while (end > 0)
            {
                int read = Math.Min(Step, end);
                text.CopyTo(start + end - read, block[..read]);
                for (int i = read - 1; i >= 0; i--, end--)
                {
                    if (!char.IsWhiteSpace(block[i]))
                    {
                        return end;
                    }
                }
            }
            return 0;
        }
    }

    /// <summary>How wide each grapheme is in the editor's font as the formatter lays it alone, measured the first time it is
    /// met and kept; printable ASCII's one width when they share it (a monospaced font), and a tab's fixed width (the
    /// formatter's tab, which does not go to a stop).</summary>
    private sealed class Advances
    {
        private readonly double[] ascii = new double[128];
        private readonly Dictionary<string, double> measured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double>.AlternateLookup<ReadOnlySpan<char>> lookup;
        private readonly Func<string, double> measure;

        public Advances(Func<string, double> measure, double tab)
        {
            this.measure = measure;
            lookup = measured.GetAlternateLookup<ReadOnlySpan<char>>();
            Array.Fill(ascii, double.NaN);
            Tab = tab;
            double first = Of(" ");
            bool same = true;
            for (char c = '!'; c <= '~'; c++)
            {
                same &= Math.Abs(Of([c]) - first) < 1e-9;
            }
            Uniform = same ? first : double.NaN;
        }

        public double Uniform { get; }
        public double Tab { get; }

        public double Of(ReadOnlySpan<char> grapheme)
        {
            if (grapheme is [< '\u0080' and var c])
            {
                return double.IsNaN(ascii[c]) ? ascii[c] = measure(c.ToString()) : ascii[c];
            }
            if (!lookup.TryGetValue(grapheme, out double width))
            {
                string key = grapheme.ToString();
                width = measured[key] = measure(key);
            }
            return width;
        }
    }

    /// <summary>Labels drawn in the last frame (line numbers, hints, placeholders, annotations), by what they show. A
    /// frame keeps the ones it draws again and disposes the rest.</summary>
    private sealed class LabelCache<TKey>
        where TKey : notnull
    {
        private Dictionary<TKey, TextLine> previous = [];
        private Dictionary<TKey, TextLine> current = [];

        public TextLine Get(TKey key, Func<TextLine> format) => TryGet(key, out TextLine? line) ? line : Add(key, format());

        /// <summary>The label kept for what it shows, kept on for this frame.</summary>
        public bool TryGet(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TextLine? line)
        {
            if (current.TryGetValue(key, out line))
            {
                return true;
            }
            if (previous.Remove(key, out line))
            {
                current[key] = line;
                return true;
            }
            return false;
        }

        public TextLine Add(TKey key, TextLine line)
        {
            current[key] = line;
            return line;
        }

        public void EndFrame()
        {
            foreach (TextLine stale in previous.Values)
            {
                stale.Dispose();
            }
            previous.Clear();
            (previous, current) = (current, previous);
        }

        public void Clear()
        {
            foreach (TextLine line in previous.Values.Concat(current.Values))
            {
                line.Dispose();
            }
            previous.Clear();
            current.Clear();
        }
    }

    private sealed class SingleRun(string text, TextRunProperties props) : ITextSource
    {
        public TextRun GetTextRun(int textSourceIndex) =>
            textSourceIndex >= text.Length ? new TextEndOfParagraph() : new TextCharacters(text.AsMemory(textSourceIndex), props);
    }

    /// <summary>A line's text in its colours. The formatter asks for its runs in order, so the search for the run at a
    /// place goes on from the last one found rather than from the first.</summary>
    private sealed class ColouredRuns(string text, LineRun[] runs, TextRunProperties plain, Func<TokenKind, TextRunProperties> colour) : ITextSource
    {
        private int next;
        private int asked;

        public TextRun GetTextRun(int textSourceIndex)
        {
            if (textSourceIndex >= text.Length)
            {
                return new TextEndOfParagraph();
            }
            if (textSourceIndex < asked)
            {
                next = 0;
            }
            asked = textSourceIndex;
            for (int i = next; i < runs.Length; i++)
            {
                LineRun run = runs[i];
                if (textSourceIndex < run.Start)
                {
                    next = i;
                    return new TextCharacters(text.AsMemory(textSourceIndex, run.Start - textSourceIndex), plain);
                }
                if (textSourceIndex < run.Start + run.Length)
                {
                    next = i;
                    return new TextCharacters(text.AsMemory(textSourceIndex, run.Start + run.Length - textSourceIndex), colour(run.Kind));
                }
            }
            next = runs.Length;
            return new TextCharacters(text.AsMemory(textSourceIndex), plain);
        }
    }

    /// <summary>One unwrapped line whose tabs are <paramref name="tab"/> pixels wide.</summary>
    private sealed class LineProperties(TextRunProperties props, double tab) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => props;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override double Indent => 0;
        public override double DefaultIncrementalTab => tab;
    }
}
