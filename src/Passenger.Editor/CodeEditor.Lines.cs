using System.Buffers;
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

    /// <summary>How many characters the editor has read to place a long line's columns: what a key costs to measure.</summary>
    public long CharactersMeasured { get; private set; }

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
        return new LineView(span, new LongLine(this, text, span.Start, span.Length, Measures()));
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

        /// <summary>The same line after edits inside it, now <paramref name="span"/>: its measure carried through them, its
        /// window let go.</summary>
        public void Carried(RowSpan span)
        {
            Span = span;
            Text = null;
            Runs = null;
            From = 0;
            To = 0;
            X = 0;
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
        /// where it starts, or starting where it ends, count as reached, as the highlighter counts them; a long line it
        /// reached inside keeps its measure, carried through it, where that measures with <paramref name="measures"/>.</summary>
        public void Follow(DocumentChangedEventArgs change, Advances? measures)
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
                else if (view.Long is { } measure && measures is not null && measure.MeasuresWith(measures)
                    && view.Span.Start == line.Start && view.Span.Length == line.Length && Carry(change, line, measure) is { } now)
                {
                    view.Carried(RowSpan.Whole(now));
                    spare[now.Start] = view;
                }
            }
            (byLine, spare) = (spare, byLine);
            spare.Clear();
            text = change.After;
        }

        /// <summary>A long line's measure carried through the edits inside it, which leave it one line, still long: the line it
        /// is now, or null where an edit reaches past it.</summary>
        private static LineSpan? Carry(DocumentChangedEventArgs change, LineSpan line, LongLine measure)
        {
            IReadOnlyList<TextEdit> edits = change.Edits;
            int moved = 0;
            int grown = 0;
            int first = -1;
            int last = -1;
            for (int k = 0; k < edits.Count; k++)
            {
                TextEdit edit = edits[k];
                if (edit.Start >= line.Start && edit.End <= line.End)
                {
                    first = first < 0 ? k : first;
                    last = k;
                    grown += edit.Text.Length - edit.Length;
                }
                else if (edit.End <= line.Start)
                {
                    moved += edit.Text.Length - edit.Length;
                }
                else if (edit.Start >= line.EndIncludingBreak)
                {
                    break;
                }
                else
                {
                    return null;
                }
            }
            TextSnapshot after = change.After;
            int at = line.Start + moved;
            int length = line.Length + grown;
            if (length <= LongLineLength || at > after.Length)
            {
                return null;
            }
            // What came before may have joined it to the line above, and what it took in may have broken it.
            LineSpan now = after.GetLine(after.GetLineFromOffset(at));
            if (now.Start != at || now.Length != length)
            {
                return null;
            }
            if (first < 0)
            {
                measure.Follow(after, at);
            }
            else
            {
                measure.Carry(after, at, length, edits, first, last, line.Start);
            }
            return now;
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

    /// <summary>The parts of a pixel widths are summed in: every bit of a width from a pixel up, so a sum is exact and the
    /// same however it is grouped.</summary>
    private const double Unit = 4503599627370496.0;

    /// <summary>A width in <see cref="Unit"/>s.</summary>
    private static Int128 Fixed(double pixels) => pixels < 2048 ? (long)(pixels * Unit) : (Int128)(pixels * Unit);

    /// <summary>The length of the grapheme at a place: one for printable ASCII that no mark follows.</summary>
    private static int Grapheme(ReadOnlySpan<char> chars, int at) =>
        chars[at] is >= ' ' and <= '~' && (at + 1 == chars.Length || chars[at + 1] < '\u0080') ? 1
        : Math.Max(1, StringInfo.GetNextTextElementLength(chars[at..]));

    /// <summary>Where a long line's columns fall, without formatting it: each grapheme's width as the formatter lays it alone
    /// (a tab's fixed width), summed exactly, and a grapheme boundary kept with its x about every <see cref="Step"/> columns,
    /// so a column's x or an x's column reads a step or so at most. An edit inside the line reads it again only from the
    /// boundary kept before the edit to the first one after it that the graphemes fall on again, and moves those after by
    /// what it changed, so the measure is the one a fresh read makes, whatever edits made the line. Printable ASCII in a
    /// monospaced font keeps none: a column's x is a product. Only shaping across graphemes (Arabic's joined letters) lays a
    /// line out otherwise.</summary>
    private sealed class LongLine
    {
        public const int Step = 256;

        /// <summary>The longest a grapheme is read: one longer is cut there, wherever it starts.</summary>
        private const int Overrun = 64;

        private readonly CodeEditor editor;
        private readonly Advances advances;

        /// <summary>Printable ASCII's one width, or -1 where they differ.</summary>
        private readonly Int128 uniform;

        private readonly Int128 tab;
        private TextSnapshot text;
        private int start;

        /// <summary>The kept boundaries, rising from the line's start, and their x; null while the line is printable ASCII in
        /// one width.</summary>
        private int[]? kept;
        private Int128[] keptX = [];
        private int count;

        /// <summary>What an edit writes the kept boundaries into, swapped with them after it.</summary>
        private int[] nextKept = [];
        private Int128[] nextX = [];
        private int written;

        private Int128 width;
        private int codeEnd;

        public LongLine(CodeEditor editor, TextSnapshot text, int start, int length, Advances advances)
        {
            this.editor = editor;
            this.text = text;
            this.start = start;
            Length = length;
            this.advances = advances;
            uniform = double.IsNaN(advances.Uniform) ? -1 : Fixed(advances.Uniform);
            tab = Fixed(advances.Tab);
            bool plain = uniform >= 0;
            if (plain)
            {
                editor.CharactersMeasured += length;
                text.VisitChunks(start, length, chunk => plain = plain && !chunk.Span.ContainsAnyExceptInRange(' ', '~'));
            }
            if (plain)
            {
                width = length * uniform;
            }
            else
            {
                kept = new int[(length / Step) + 1];
                keptX = new Int128[kept.Length];
                count = kept.Length;
                width = Scan(kept, keptX);
            }
            codeEnd = CodeEnd();
            Settle();
        }

        public int Length { get; private set; }
        public double Width { get; private set; }
        public double CodeWidth { get; private set; }

        /// <summary>Whether it measures with these widths.</summary>
        public bool MeasuresWith(Advances measures) => ReferenceEquals(advances, measures);

        /// <summary>The same characters in a text an edit made, which it did not reach, starting at <paramref name="at"/>.</summary>
        public void Follow(TextSnapshot moved, int at)
        {
            text = moved;
            start = at;
        }

        /// <summary>The line after edits inside it that leave it one line, <paramref name="length"/> long from
        /// <paramref name="at"/> in <paramref name="moved"/>: <paramref name="edits"/> <paramref name="first"/> to
        /// <paramref name="last"/>, their offsets from <paramref name="was"/>, where the line started before them.</summary>
        public void Carry(TextSnapshot moved, int at, int length, IReadOnlyList<TextEdit> edits, int first, int last, int was)
        {
            int before = Length;
            int lastEnd = edits[last].End - was;
            text = moved;
            start = at;
            Length = length;
            if (kept is null && !Plain(edits, first, last))
            {
                KeepSteps(before);
            }
            if (kept is null)
            {
                width = length * uniform;
            }
            else
            {
                Remeasure(edits, first, last, was);
            }
            codeEnd = codeEnd > lastEnd ? codeEnd + length - before : CodeEnd();
            Settle();
        }

        /// <summary>The first grapheme boundary from a step at or before a column: where a window of the line starts, as a
        /// fresh measure puts it.</summary>
        public int KeptAtOrBefore(int column)
        {
            if (kept is null)
            {
                return column / Step * Step;
            }
            int step = Math.Min(column, Length) / Step;
            int at = BoundaryFrom(step * Step);
            return at <= column ? at : BoundaryFrom((step - 1) * Step);
        }

        /// <summary>The first grapheme boundary from a step at or after a column, or the line's end.</summary>
        public int KeptAtOrAfter(int column)
        {
            int step = (column + Step - 1) / Step;
            if (kept is null)
            {
                return Math.Min(Length, step * Step);
            }
            return step <= Length / Step ? BoundaryFrom(step * Step) : Length;
        }

        public double XOf(int column)
        {
            column = Math.Clamp(column, 0, Length);
            if (kept is null)
            {
                return ToDouble(column * uniform);
            }
            int k = Before(column);
            return ToDouble(Walk(kept[k], keptX[k], column, -1).X);
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
            Int128 find = Fixed(x);
            if (kept is null)
            {
                int column = (int)(find / uniform);
                return Math.Min(Length, (find - (column * uniform)) * 2 >= uniform ? column + 1 : column);
            }
            int low = 0;
            int high = count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (keptX[middle] <= find)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            int k = Math.Max(0, low - 1);
            return Walk(kept[k], keptX[k], k + 1 < count ? kept[k + 1] : Length, find).Column;
        }

        private void Settle()
        {
            Width = ToDouble(width);
            CodeWidth = XOf(codeEnd);
        }

        /// <summary>Whether what the edits put in keeps a line of printable ASCII so.</summary>
        private static bool Plain(IReadOnlyList<TextEdit> edits, int first, int last)
        {
            for (int e = first; e <= last; e++)
            {
                if (edits[e].Text.AsSpan().ContainsAnyExceptInRange(' ', '~'))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Boundaries kept for a line of printable ASCII in one width, as it was <paramref name="before"/> characters
        /// long: each step's column, as every column is one.</summary>
        private void KeepSteps(int before)
        {
            count = (before / Step) + 1;
            kept = new int[count];
            keptX = new Int128[count];
            for (int k = 0; k < count; k++)
            {
                kept[k] = k * Step;
                keptX[k] = kept[k] * uniform;
            }
        }

        /// <summary>The kept boundaries carried through edits: those before each edit kept, the line read again from the last of
        /// them to the first old one after the edit that the graphemes fall on again, and the rest moved by what the edits
        /// changed. What is read again keeps a boundary every step, and one left a step or less from the next is dropped.</summary>
        private void Remeasure(IReadOnlyList<TextEdit> edits, int first, int last, int was)
        {
            int[] cols = kept!;
            Int128[] xs = keptX;
            int n = count;
            if (nextKept.Length < n + 2)
            {
                nextKept = new int[n + (n / 2) + 2];
                nextX = new Int128[nextKept.Length];
            }
            written = 0;
            Put(0, 0);
            int i = 1;
            int moved = 0;
            Int128 movedX = 0;
            bool ended = false;
            Span<char> chars = stackalloc char[(2 * Step) + Overrun];
            int e = first;
            while (e <= last)
            {
                int editAt = edits[e].Start - was;
                // A boundary rests on the characters before it and the one or two after it.
                while (i < n && cols[i] + 2 <= editAt)
                {
                    Put(cols[i] + moved, xs[i] + movedX);
                    i++;
                }
                int column = nextKept[written - 1];
                Int128 x = nextX[written - 1];
                int lastKept = column;
                int reach = edits[e].End - was;
                int after = moved + edits[e].Text.Length - edits[e].Length;
                e++;
                int readAt = column;
                int read = 0;
                while (true)
                {
                    while (e <= last && column > edits[e].Start - was + after)
                    {
                        reach = edits[e].End - was;
                        after += edits[e].Text.Length - edits[e].Length;
                        e++;
                    }
                    while (i < n && (cols[i] < reach || cols[i] + after < column))
                    {
                        i++;
                    }
                    if (column == Length)
                    {
                        width = x;
                        ended = true;
                        i = n;
                        e = last + 1;
                        break;
                    }
                    if (i < n && cols[i] + after == column && (e > last || cols[i] <= edits[e].Start - was))
                    {
                        while (written > 1 && column - nextKept[written - 2] <= Step)
                        {
                            written--;
                        }
                        moved = after;
                        movedX = x - xs[i];
                        break;
                    }
                    if (column - lastKept >= Step)
                    {
                        Put(column, x);
                        lastKept = column;
                    }
                    if (column + Overrun > readAt + read && readAt + read < Length)
                    {
                        readAt = column;
                        read = Math.Min(chars.Length, Length - column);
                        text.CopyTo(start + column, chars[..read]);
                        editor.CharactersMeasured += read;
                    }
                    int size = Piece(chars[..read], column - readAt);
                    x += Of(chars.Slice(column - readAt, size));
                    column += size;
                }
            }
            for (; i < n; i++)
            {
                Put(cols[i] + moved, xs[i] + movedX);
            }
            if (!ended)
            {
                width += movedX;
            }
            (kept, nextKept) = (nextKept, cols);
            (keptX, nextX) = (nextX, xs);
            count = written;
        }

        private void Put(int column, Int128 x)
        {
            if (written == nextKept.Length)
            {
                Array.Resize(ref nextKept, nextKept.Length * 2);
                Array.Resize(ref nextX, nextKept.Length);
            }
            nextKept[written] = column;
            nextX[written] = x;
            written++;
        }

        /// <summary>The last kept boundary at or before a column.</summary>
        private int Before(int column)
        {
            int low = 0;
            int high = count - 1;
            while (low < high)
            {
                int middle = (int)((uint)(low + high + 1) >> 1);
                if (kept![middle] <= column)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return low;
        }

        /// <summary>The first grapheme boundary at or after a column.</summary>
        private int BoundaryFrom(int column)
        {
            int from = kept![Before(column)];
            if (from == column)
            {
                return column;
            }
            int read = Math.Min(Length, column + Overrun) - from;
            Span<char> chars = read <= (2 * Step) + (2 * Overrun) ? stackalloc char[(2 * Step) + (2 * Overrun)] : new char[read];
            chars = chars[..read];
            text.CopyTo(start + from, chars);
            editor.CharactersMeasured += read;
            int i = 0;
            while (from + i < column)
            {
                i += Piece(chars, i);
            }
            return from + i;
        }

        /// <summary>Reads on a grapheme at a time from a kept boundary and its x toward <paramref name="to"/>: the x of the
        /// grapheme that holds it (its own, at a boundary), or, should the x <paramref name="find"/> (unless negative) come
        /// first, the boundary nearest that.</summary>
        private (Int128 X, int Column) Walk(int from, Int128 x, int to, Int128 find)
        {
            int read = Math.Min(Length, to + Overrun) - from;
            Span<char> chars = read <= (2 * Step) + (2 * Overrun) ? stackalloc char[(2 * Step) + (2 * Overrun)] : new char[read];
            chars = chars[..read];
            text.CopyTo(start + from, chars);
            editor.CharactersMeasured += read;
            int i = 0;
            while (from + i < to)
            {
                int length = Piece(chars, i);
                Int128 advance = Of(chars.Slice(i, length));
                if (find >= 0 && find < x + advance)
                {
                    return (x, (find - x) * 2 >= advance ? from + i + length : from + i);
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
        private Int128 Scan(int[] marks, Int128[] xs)
        {
            const int Block = 4096;
            char[] rented = ArrayPool<char>.Shared.Rent(Block + Overrun);
            try
            {
                Span<char> buffer = rented.AsSpan(0, Block + Overrun);
                int column = 0;
                Int128 x = 0;
                int next = 0;
                while (column < Length)
                {
                    int read = Math.Min(buffer.Length, Length - column);
                    text.CopyTo(start + column, buffer[..read]);
                    editor.CharactersMeasured += read;
                    ReadOnlySpan<char> chars = buffer[..read];
                    int limit = Math.Min(Block, read);
                    int i = 0;
                    while (i < limit)
                    {
                        for (; next < marks.Length && next * Step <= column + i; next++)
                        {
                            marks[next] = column + i;
                            xs[next] = x;
                        }
                        if (uniform >= 0)
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
                        int length = Piece(chars, i);
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
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }

        /// <summary>The grapheme at a place, cut at <see cref="Overrun"/> characters: <paramref name="chars"/> reach that far
        /// past it or to the line's end.</summary>
        private static int Piece(ReadOnlySpan<char> chars, int at) => Grapheme(chars[..Math.Min(chars.Length, at + Overrun)], at);

        private Int128 Of(ReadOnlySpan<char> grapheme) =>
            grapheme is ['\t'] ? tab
            : uniform >= 0 && grapheme is [>= ' ' and <= '~'] ? uniform
            : Fixed(advances.Of(grapheme));

        private static double ToDouble(Int128 units) => (double)units / Unit;

        /// <summary>Where the line's code ends, before the spaces after it, read back from its end a block at a time.</summary>
        private int CodeEnd()
        {
            Span<char> block = stackalloc char[Step];
            int end = Length;
            while (end > 0)
            {
                int read = Math.Min(Step, end);
                text.CopyTo(start + end - read, block[..read]);
                editor.CharactersMeasured += read;
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
    /// formatter's tab, which does not go to a stop). A copy for the thread pool measures nothing: what it has no width
    /// for it gathers (<see cref="Lacking"/>) for the window's thread to measure.</summary>
    private sealed class Advances
    {
        /// <summary>The most graphemes a copy gathers that it has no width for.</summary>
        private const int LackedAtMost = 1024;

        private readonly double[] ascii = new double[128];
        private readonly Dictionary<string, double> measured;
        private readonly Dictionary<string, double>.AlternateLookup<ReadOnlySpan<char>> lookup;
        private readonly Func<string, double>? measure;

        public Advances(Func<string, double> measure, double tab)
        {
            this.measure = measure;
            measured = new(StringComparer.Ordinal);
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

        private Advances(Advances from)
        {
            from.ascii.CopyTo(ascii, 0);
            measured = new(from.measured, StringComparer.Ordinal);
            lookup = measured.GetAlternateLookup<ReadOnlySpan<char>>();
            Tab = from.Tab;
            Uniform = from.Uniform;
            Widest = from.Widest;
            Lacking = [];
        }

        public double Uniform { get; }
        public double Tab { get; }

        /// <summary>The widest a character of the graphemes measured is: a grapheme's width over its length.</summary>
        public double Widest { get; private set; }

        /// <summary>The graphemes a copy met that it has no width for; null for the editor's own, which measures them.</summary>
        public HashSet<string>? Lacking { get; }

        /// <summary>Whether a copy has met a grapheme it has no width for.</summary>
        public bool Lacked { get; private set; }

        /// <summary>Whether a copy has gathered as many graphemes it lacks as it takes.</summary>
        public bool Full => Lacking is { Count: >= LackedAtMost };

        /// <summary>The widths measured so far, for wrapping on the thread pool.</summary>
        public Advances Copy() => new(this);

        public double Of(ReadOnlySpan<char> grapheme)
        {
            if (grapheme is [< '\u0080' and var c])
            {
                return !double.IsNaN(ascii[c]) ? ascii[c] : measure is null ? Lack(grapheme) : ascii[c] = Widen(measure(c.ToString()), 1);
            }
            if (!lookup.TryGetValue(grapheme, out double width))
            {
                if (measure is null)
                {
                    return Lack(grapheme);
                }
                string key = grapheme.ToString();
                width = measured[key] = Widen(measure(key), key.Length);
            }
            return width;
        }

        private double Widen(double width, int length)
        {
            Widest = Math.Max(Widest, width / length);
            return width;
        }

        /// <summary>A copy's stand-in for a grapheme it has no width for, which it gathers.</summary>
        private double Lack(ReadOnlySpan<char> grapheme)
        {
            Lacked = true;
            if (!Full)
            {
                Lacking!.Add(grapheme.ToString());
            }
            return Tab;
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
