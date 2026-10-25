using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Colours a C# document as it is typed, and is the one parse of it that everything else reading its syntax
/// shares (<see cref="Current"/>, <see cref="Parsed"/>). Roslyn reads the document's own snapshots, told the range each
/// edit changed, so each parse is incremental and runs on a worker thread. The editor asks for the lines it draws,
/// which are classified from the newest tree (immutable, so safe to read from any thread) and kept while that tree
/// stands; a parse classifies the lines last asked for on its worker. Edits made since that tree was parsed are mapped
/// back to find the tree's text and forward again to place its colours, so a word being typed keeps its colour and
/// typing never flashes plain. A key costs the UI thread the lines it changed, whatever the size of the file.</summary>
public sealed class CSharpHighlighter : IHighlighter, IBraceMatcher, IFoldFinder, IDisposable
{
    /// <summary>The symbols a Debug build defines, so <c>#if DEBUG</c> code is coloured as code.</summary>
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview, DocumentationMode.Parse, preprocessorSymbols: ["DEBUG", "TRACE", "NET", "NETCOREAPP"]);

    private readonly TextDocument document;
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;
    private readonly List<(int Version, IReadOnlyList<TextEdit> Edits)> sinceTree = [];
    private SnapshotText text;
    private SyntaxTree? tree;
    private TextSnapshot? treeText;

    /// <summary>What folds in <see cref="tree"/>, which the next parse's folds are found from.</summary>
    private FoldRegions? treeFolds;
    private long parseBytes;
    private int parses;
    private int version;

    /// <summary>The version the parse under way is of, whose change is never joined to a later one: the parse's landing
    /// lets go of the changes up to it.</summary>
    private int parsingVersion = -1;
    private bool parsing;
    private bool disposed;

    /// <summary>The parse under way, which a caller asking for the syntax before the first has landed waits for.</summary>
    private Task<(SyntaxTree Tree, FoldRegions Folds, Dictionary<int, (int Length, SyntaxSpan[] Spans)> Lines)>? running;

    /// <summary>The syntax given for <see cref="currentVersion"/> of the text.</summary>
    private ParsedText? current;
    private int currentVersion = -1;

    /// <summary>The spans given for each range asked of the newest text: a frame asks for each run of lines between
    /// folds, and a caret's line on its own.</summary>
    private readonly Dictionary<(int Start, int Length), SyntaxSpan[]> cached = [];
    private int cachedVersion = -1;

    /// <summary>The lines asked of the newest text, first and last, which the next parse classifies ahead and hands over as
    /// it lands (<see cref="lines"/>): a frame after a key asks for the lines the frame before it did.</summary>
    private readonly List<(int First, int Last)> asked = [];

    /// <summary>Each line's colours as read from <see cref="tree"/>, by where the line starts in the text of
    /// <see cref="linesVersion"/>: its length with its break, and its colours relative to its start. Carried from one
    /// version to the next while the tree stands (<see cref="CarryLines"/>), let go as another lands; the other is the one
    /// a carry fills.</summary>
    private Dictionary<int, (int Length, SyntaxSpan[] Spans)> lines = [];
    private Dictionary<int, (int Length, SyntaxSpan[] Spans)> spareLines = [];
    private int linesVersion = -1;
    private long classified;

    /// <summary>The most lines kept, past which they are let go: a few screens.</summary>
    private const int MostLines = 4096;

    public CSharpHighlighter(TextDocument document)
    {
        this.document = document;
        text = new SnapshotText(document.Snapshot);
        document.Changed += OnChanged;
        Parse();
    }

    public event EventHandler? Changed;

    /// <summary>What folds in the text (<see cref="FoldRegions"/>), from the newest parse of the text as it stands.</summary>
    public IReadOnlyList<FoldRegion> Folds { get; private set; } = [];

    /// <summary>A parse of the text as it stands found its folds.</summary>
    public event EventHandler? FoldsChanged;

    /// <summary>What the parses of this file allocated on their worker, its folds and the lines on screen classified ahead
    /// included, and how many there were: what typing costs off the UI thread.</summary>
    public (long Bytes, int Parses) Worked => (Interlocked.Read(ref parseBytes), Volatile.Read(ref parses));

    /// <summary>A parse of the text as it stands landed: its syntax, for work on it off the UI thread. Raised on the UI
    /// thread, once typing has paused long enough for a parse to catch up with it.</summary>
    public event EventHandler<ParsedText>? Parsed;

    /// <summary>The syntax of the text as it stands, for whatever else reads it: the newest parse, carried through the
    /// edits made since by Roslyn's incremental parse, which reads again only around them, so nothing parses the whole
    /// file on the UI thread. Made once for each version of the text, on the UI thread, where the text changes.</summary>
    public ParsedText Current
    {
        get
        {
            if (current is null || currentVersion != version)
            {
                current = tree is not null && sinceTree.Count == 0 ? new ParsedText(tree, treeText!) : new ParsedText(Carried(), text.Snapshot);
                currentVersion = version;
            }
            return current;
        }
    }

    /// <summary>The newest parse when it is of the text as it stands, else null until one is (<see cref="Parsed"/>).</summary>
    public ParsedText? Newest => tree is not null && sinceTree.Count == 0 ? Current : null;

    public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot snapshot, int start, int length)
    {
        if (tree is null)
        {
            return [];
        }
        if (cachedVersion != version)
        {
            cached.Clear();
            asked.Clear();
            cachedVersion = version;
        }
        if (cached.TryGetValue((start, length), out SyntaxSpan[]? known))
        {
            return known;
        }
        (int First, int Last)? whole = WholeLines(snapshot, start, length);
        if (asked.Count < 64 && length > 0 && start + length <= snapshot.Length)
        {
            asked.Add(whole ?? (snapshot.GetLineFromOffset(start), snapshot.GetLineFromOffset(start + length - 1)));
        }
        SyntaxSpan[] result = whole is (int first, int last) ? ByLine(snapshot, first, last) : [.. Classified(start, length)];
        if (cached.Count >= 256)
        {
            cached.Clear();
        }
        cached[(start, length)] = result;
        return result;
    }

    /// <summary>A range's colours read from the tree: the range carried back through the edits since to the tree's text,
    /// classified there, and the colours carried forward again.</summary>
    private List<SyntaxSpan> Classified(int start, int length)
    {
        int from = start;
        int to = start + length;
        for (int i = sinceTree.Count - 1; i >= 0; i--)
        {
            from = CarriedSpans.Back(sinceTree[i].Edits, from, toEnd: false);
            to = CarriedSpans.Back(sinceTree[i].Edits, to, toEnd: true);
        }
        SyntaxNode root = tree!.GetRoot();
        // One character to the left, so a word being typed at its end is found and its colour carried over the new letters.
        from = sinceTree.Count > 0 ? from - 1 : from;
        TextSpan range = TextSpan.FromBounds(Math.Clamp(from, 0, root.FullSpan.End), Math.Clamp(to, 0, root.FullSpan.End));
        List<SyntaxSpan> spans = CSharpClassifier.Classify(root, range);
        Interlocked.Add(ref classified, range.Length);
        foreach ((_, IReadOnlyList<TextEdit> batch) in sinceTree)
        {
            CarriedSpans.Forward(spans, batch);
        }
        return spans;
    }

    /// <summary>The first and last line of a range of whole lines of the text as it stands (as a frame asks for its runs of
    /// lines), null for any other: part of a line (a wrapped row, a long line's window, a place) is classified as asked.</summary>
    private (int First, int Last)? WholeLines(TextSnapshot snapshot, int start, int length)
    {
        if (!ReferenceEquals(snapshot, text.Snapshot) || length <= 0 || start + length > snapshot.Length)
        {
            return null;
        }
        LineSpan first = snapshot.GetLine(snapshot.GetLineFromOffset(start));
        LineSpan last = snapshot.GetLine(snapshot.GetLineFromOffset(start + length - 1));
        return first.Start == start && last.EndIncludingBreak == start + length ? (first.Line, last.Line) : null;
    }

    /// <summary>The colours of whole lines, each line's as kept (<see cref="lines"/>) or, for a run of lines with none kept,
    /// classified at once and kept line by line.</summary>
    private SyntaxSpan[] ByLine(TextSnapshot snapshot, int first, int last)
    {
        if (linesVersion != version || lines.Count > MostLines)
        {
            lines.Clear();
            linesVersion = version;
        }
        List<SyntaxSpan> spans = [];
        for (int line = first; line <= last;)
        {
            if (Kept(snapshot.GetLine(line)) is { } kept)
            {
                Add(spans, kept, snapshot.GetLine(line).Start);
                line++;
                continue;
            }
            int run = line;
            while (line <= last && Kept(snapshot.GetLine(line)) is null)
            {
                line++;
            }
            LineSpan top = snapshot.GetLine(run);
            LineSpan bottom = snapshot.GetLine(line - 1);
            List<SyntaxSpan> found = Classified(top.Start, bottom.EndIncludingBreak - top.Start);
            for (int each = run; each < line; each++)
            {
                LineSpan of = snapshot.GetLine(each);
                SyntaxSpan[] pieces = Pieces(found, of.Start, of.EndIncludingBreak);
                lines[of.Start] = (of.EndIncludingBreak - of.Start, pieces);
                Add(spans, pieces, of.Start);
            }
        }
        return [.. spans];
    }

    /// <summary>A line's colours as kept for the text as it stands, relative to its start; null when none are kept.</summary>
    private SyntaxSpan[]? Kept(LineSpan line) =>
        lines.TryGetValue(line.Start, out (int Length, SyntaxSpan[] Spans) kept) && kept.Length == line.EndIncludingBreak - line.Start ? kept.Spans : null;

    /// <summary>The parts of colours that fall on a line, relative to its start: a comment over several lines is a piece on
    /// each.</summary>
    private static SyntaxSpan[] Pieces(List<SyntaxSpan> spans, int start, int end)
    {
        List<SyntaxSpan>? pieces = null;
        foreach (SyntaxSpan span in spans)
        {
            int from = Math.Max(span.Start, start);
            int to = Math.Min(span.Start + span.Length, end);
            if (to > from)
            {
                (pieces ??= []).Add(new SyntaxSpan(from - start, to - from, span.Kind));
            }
        }
        return pieces is null ? [] : [.. pieces];
    }

    private static void Add(List<SyntaxSpan> spans, SyntaxSpan[] pieces, int lineStart)
    {
        foreach (SyntaxSpan piece in pieces)
        {
            spans.Add(piece with { Start = piece.Start + lineStart });
        }
    }

    /// <summary>The lines' colours carried through an edit while the tree they were read from stands, which would read them
    /// the same: those of the lines it did not reach kept, moved with the text after it, so a key classifies again only the
    /// lines it changed rather than every line on screen (each key while a solution loads, the parse falling behind).</summary>
    private void CarryLines(IReadOnlyList<TextEdit> edits)
    {
        if (linesVersion != version || lines.Count == 0 || edits.Count == 0)
        {
            lines.Clear();
            linesVersion = version + 1;
            return;
        }
        Carry(lines, spareLines, edits);
        (lines, spareLines) = (spareLines, lines);
        linesVersion = version + 1;
    }

    /// <summary>Lines' colours carried through an edit into another set, those it reaches left out.</summary>
    private static void Carry(Dictionary<int, (int Length, SyntaxSpan[] Spans)> from, Dictionary<int, (int Length, SyntaxSpan[] Spans)> into, IReadOnlyList<TextEdit> edits)
    {
        int first = edits[0].Start;
        int last = edits[^1].Start + edits[^1].Length;
        int shift = 0;
        foreach (TextEdit edit in edits)
        {
            shift += edit.Text.Length - edit.Length;
        }
        into.Clear();
        foreach ((int start, (int Length, SyntaxSpan[] Spans) kept) in from)
        {
            if (start + kept.Length < first)
            {
                into[start] = kept;
            }
            else if (start > last)
            {
                into[start + shift] = kept;
            }
        }
    }

    /// <summary>The colours of the lines asked of the text before a parse, read from the parse on its worker, by where each
    /// line starts in the parsed text, as <see cref="lines"/> keeps them: what its landing hands the screen's next frame,
    /// which then classifies nothing again. A worker's.</summary>
    private static Dictionary<int, (int Length, SyntaxSpan[] Spans)> Read(SyntaxNode root, TextSnapshot text, (int First, int Last)[] warm)
    {
        Dictionary<int, (int Length, SyntaxSpan[] Spans)> read = [];
        int lineCount = text.LineCount;
        foreach ((int firstAsked, int lastAsked) in warm)
        {
            int first = Math.Min(firstAsked, lineCount - 1);
            int last = Math.Min(lastAsked, lineCount - 1);
            if (read.Count > MostLines || (read.ContainsKey(text.GetLineStart(first)) && read.ContainsKey(text.GetLineStart(last))))
            {
                continue;
            }
            LineSpan top = text.GetLine(first);
            LineSpan bottom = text.GetLine(last);
            List<SyntaxSpan> found = CSharpClassifier.Classify(root, TextSpan.FromBounds(top.Start, Math.Min(bottom.EndIncludingBreak, root.FullSpan.End)));
            for (int line = first; line <= last; line++)
            {
                LineSpan of = text.GetLine(line);
                read[of.Start] = (of.EndIncludingBreak - of.Start, Pieces(found, of.Start, of.EndIncludingBreak));
            }
        }
        return read;
    }

    /// <summary>How many characters the editor's asks have had classified from the tree on the UI thread: what drawing the
    /// lines on screen costs a key.</summary>
    public long CharactersClassified => Interlocked.Read(ref classified);

    /// <summary>A bracket and its pair (<see cref="BracePairs"/>), read from <see cref="Current"/> once the first parse has
    /// landed, so a frame never waits for one. Near or <paramref name="far"/>, the pair is read from the bracket's own node,
    /// never from the lines between them.</summary>
    public BracePair? PairAt(TextSnapshot snapshot, int offset, bool far = false) =>
        tree is not null && ReferenceEquals(snapshot, document.Snapshot) ? BracePairs.At(Current.Root, offset) : null;

    public void Dispose()
    {
        disposed = true;
        document.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        text = text.With(e.After, e.Edits);
        CarryLines(e.Edits);
        version++;
        if (sinceTree.Count == 0 || sinceTree[^1].Version == parsingVersion || !CarriedSpans.Join(sinceTree, version, e.Edits))
        {
            sinceTree.Add((version, e.Edits));
        }
        Parse();
    }

    private void Parse()
    {
        if (parsing || disposed)
        {
            return;
        }
        parsing = true;
        SnapshotText parsedText = text;
        SyntaxTree? previous = tree;
        FoldRegions? previousFolds = treeFolds;
        int parsedVersion = version;
        parsingVersion = parsedVersion;
        (int First, int Last)[] warm = [.. asked];
        BackgroundWork.Begin();
        Task<(SyntaxTree Tree, FoldRegions Folds, Dictionary<int, (int Length, SyntaxSpan[] Spans)> Lines)> parse = Task.Run(() =>
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            SyntaxTree parsed = previous is null ? CSharpSyntaxTree.ParseText(parsedText, Options) : previous.WithChangedText(parsedText);
            // The screen's lines classified here build the tree's nodes off the UI thread, which classifies them no more.
            Dictionary<int, (int Length, SyntaxSpan[] Spans)> read = Read(parsed.GetRoot(), parsedText.Snapshot, warm);
            FoldRegions folds = FoldRegions.Of(new ParsedText(parsed, parsedText.Snapshot), previous is null ? null : previousFolds);
            Interlocked.Add(ref parseBytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Interlocked.Increment(ref parses);
            return (Tree: parsed, Folds: folds, Lines: read);
        });
        running = parse;
        UnawaitedWork.Observe(parse.ContinueWith(done => OnUi(() =>
        {
            try
            {
                parsing = false;
                parsingVersion = -1;
                if (done.IsCompletedSuccessfully && !disposed)
                {
                    tree = done.Result.Tree;
                    treeText = parsedText.Snapshot;
                    treeFolds = done.Result.Folds;
                    parsedText.ForgetHistory();
                    sinceTree.RemoveAll(batch => batch.Version <= parsedVersion);
                    cached.Clear();
                    TakeLines(done.Result.Lines);
                    Changed?.Invoke(this, EventArgs.Empty);
                    if (parsedVersion == version)
                    {
                        Folds = done.Result.Folds.All;
                        FoldsChanged?.Invoke(this, EventArgs.Empty);
                        Parsed?.Invoke(this, Current);
                    }
                }
                if (parsedVersion != version)
                {
                    Parse();
                }
            }
            finally
            {
                BackgroundWork.End();
            }
        }), TaskScheduler.Default));
    }

    /// <summary>The lines the landing parse classified on its worker, as the lines kept for the text as it stands: carried
    /// through the edits made since it was asked for, which <see cref="sinceTree"/> holds once the parse has landed.</summary>
    private void TakeLines(Dictionary<int, (int Length, SyntaxSpan[] Spans)> read)
    {
        foreach ((_, IReadOnlyList<TextEdit> edits) in sinceTree)
        {
            if (read.Count == 0 || edits.Count == 0)
            {
                break;
            }
            Carry(read, spareLines, edits);
            (read, spareLines) = (spareLines, read);
        }
        spareLines = lines;
        lines = read;
        linesVersion = version;
    }

    /// <summary>The newest tree carried to the text as it stands. Asked before the first parse has landed, that parse is
    /// waited for rather than made twice.</summary>
    private SyntaxTree Carried()
    {
        SyntaxTree? basis = tree;
        if (basis is null && running is { } first)
        {
            ((IAsyncResult)first).AsyncWaitHandle.WaitOne();
            basis = first.IsCompletedSuccessfully ? first.Result.Tree : null;
        }
        return basis is null ? CSharpSyntaxTree.ParseText(text, Options) : basis.WithChangedText(text);
    }

    private void OnUi(Action action)
    {
        if (ui is null)
        {
            action();
        }
        else
        {
            ui.Post(_ => action(), null);
        }
    }

}
