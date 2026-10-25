using System.Buffers;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages.Markup;

/// <summary>What folds in a file no parser reads (XML and the project files, JSON), found by one scan of its text off the
/// UI thread, a chunk of the rope at a time with nothing copied: one scan at a time, the keys typed while one runs read
/// by one more scan of the newest text. A scan of 20,000 lines takes a few milliseconds and allocates the list of the
/// regions it finds.</summary>
public sealed class FoldScan : IFoldFinder, IDisposable
{
    private readonly TextDocument document;
    private readonly Func<TextSnapshot, int, List<FoldRegion>> find;
    private int version;
    private bool scanning;
    private bool disposed;
    private long scanBytes;
    private long scanTicks;
    private int scans;

    private FoldScan(TextDocument document, Func<TextSnapshot, int, List<FoldRegion>> find)
    {
        this.document = document;
        this.find = find;
        document.Changed += OnChanged;
        UnawaitedWork.Observe(ScanAsync());
    }

    /// <summary>An XML file's folds: each element over several lines, folded after its start tag's line to read
    /// <c>&lt;PropertyGroup&gt;...&lt;/PropertyGroup&gt;</c>, and each comment over several lines.</summary>
    public static FoldScan Xml(TextDocument document) => new(document, new XmlFolds().Find);

    /// <summary>A JSON file's folds: each object and array over several lines, and each block comment.</summary>
    public static FoldScan Json(TextDocument document) => new(document, JsonFolds.Find);

    public IReadOnlyList<FoldRegion> Folds { get; private set; } = [];

    public event EventHandler? FoldsChanged;

    /// <summary>What the scans allocated on their worker, how many there were and how long they took: what typing costs off
    /// the UI thread.</summary>
    public (long Bytes, int Scans, TimeSpan Time) Worked =>
        (Interlocked.Read(ref scanBytes), Volatile.Read(ref scans), System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref scanTicks)));

    public void Dispose()
    {
        disposed = true;
        document.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        version++;
        if (!scanning)
        {
            UnawaitedWork.Observe(ScanAsync());
        }
    }

    /// <summary>Scans until a scan's text is still the document's, then tells of its regions. Never inside the change
    /// that started it, even when a small file's scan is done before it is awaited: the document's later listeners have
    /// yet to hear of the change, and the first scan's listener of being added.</summary>
    private async Task ScanAsync()
    {
        scanning = true;
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            while (!disposed)
            {
                int scanned = version;
                TextSnapshot text = document.Snapshot;
                int count = Folds.Count;
                IReadOnlyList<FoldRegion> found = await BackgroundWork.Run(() => Scan(text, count))
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
                if (disposed)
                {
                    return;
                }
                if (scanned == version)
                {
                    Folds = found;
                    FoldsChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
        }
        finally
        {
            scanning = false;
        }
    }

    /// <summary>The regions of a text, in a list made for as many as the text before had (and a few more), so a key typed
    /// allocates the list once and copies it never.</summary>
    private List<FoldRegion> Scan(TextSnapshot text, int count)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<FoldRegion> regions = find(text, count + 16);
        // In order of where they start, the widest first of those starting on one line.
        regions.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        Interlocked.Add(ref scanTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        Interlocked.Add(ref scanBytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Interlocked.Increment(ref scans);
        return regions;
    }

    /// <summary>Where a bracket or an element opened: its line, that line's end once the scan has read past it (where its
    /// region starts, its placeholder after the line's code), and the element's name in the scan's buffer or the bracket.</summary>
    private struct Opened
    {
        public int Line;
        public int LineEnd;
        public int NameStart;
        public int NameLength;
        public char Bracket;
    }

    /// <summary>What every scan shares: the offset and line being read, and the ends of the lines things opened on,
    /// filled as the scan reads each line's break.</summary>
    private abstract class Scanner(int capacity)
    {
        protected readonly List<FoldRegion> found = new(capacity);
        protected Opened[] stack = new Opened[32];
        protected int depth;
        protected int pos;
        protected int line;
        protected char previous;

        /// <summary>The stack's entries from here on opened on the line being read.</summary>
        private int pendingFrom;

        /// <summary>The line end of what is being read that opened on the line being read (a tag, a comment), once known.</summary>
        protected int markupLineEnd = -1;

        public List<FoldRegion> Read(TextSnapshot text)
        {
            text.VisitChunks(0, text.Length, chunk =>
            {
                ReadOnlySpan<char> span = chunk.Span;
                int i = 0;
                while (i < span.Length)
                {
                    // Most of a file is text, values and attributes, passed over a vector at a time to what matters there.
                    int skip = Skip(span[i..]);
                    if (skip != 0)
                    {
                        skip = skip < 0 ? span.Length - i : skip;
                        previous = span[i + skip - 1];
                        pos += skip;
                        i += skip;
                        continue;
                    }
                    char c = span[i];
                    if (c == '\n')
                    {
                        Break();
                    }
                    Next(c);
                    previous = c;
                    pos++;
                    i++;
                }
            });
            return found;
        }

        /// <summary>How many characters from the start of <paramref name="rest"/> mean nothing in the state the scan is in
        /// (none of them a line break), as <c>IndexOfAny</c> gives it: -1 for all of them.</summary>
        protected abstract int Skip(ReadOnlySpan<char> rest);

        protected abstract void Next(char c);

        protected void Push(Opened opened)
        {
            if (depth == stack.Length)
            {
                Array.Resize(ref stack, depth * 2);
            }
            stack[depth++] = opened;
        }

        /// <summary>Drops the stack to a depth, the line ends still to come with it.</summary>
        protected void PopTo(int to)
        {
            depth = to;
            pendingFrom = Math.Min(pendingFrom, depth);
        }

        /// <summary>A region from the end of the line something opened on to the character being read, when it is on a
        /// later line.</summary>
        protected void Close(int openLine, int openLineEnd, string placeholder, FoldKind kind)
        {
            if (line > openLine && openLineEnd >= 0)
            {
                found.Add(new FoldRegion(openLineEnd, pos + 1, placeholder, Kind: kind));
            }
        }

        private void Break()
        {
            int end = previous == '\r' ? pos - 1 : pos;
            for (int i = pendingFrom; i < depth; i++)
            {
                if (stack[i].LineEnd < 0)
                {
                    stack[i].LineEnd = end;
                }
            }
            pendingFrom = depth;
            if (markupLineEnd < 0)
            {
                markupLineEnd = end;
            }
            line++;
        }
    }

    /// <summary>The XML scan, which knows no schema: start and end tags paired by name (an end tag with no start tag open
    /// is passed over, and the start tags it would skip are dropped), self-closing tags, comments, CDATA, processing
    /// instructions and declarations, attributes' quotes respected. Tag names are kept in one buffer, never as strings; a
    /// placeholder is made once for each name.</summary>
    private sealed class XmlFolds
    {
        private readonly Dictionary<string, string> closers = new(StringComparer.Ordinal);

        public List<FoldRegion> Find(TextSnapshot text, int capacity) => new XmlScanner(this, capacity).Read(text);

        /// <summary>The placeholder of an element named so: its end tag after the dots.</summary>
        private string Closer(ReadOnlySpan<char> name)
        {
            Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup = closers.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!lookup.TryGetValue(name, out string? closer))
            {
                closer = $"...</{name}>";
                if (closers.Count < 4096)
                {
                    closers[name.ToString()] = closer;
                }
            }
            return closer;
        }

        private enum State
        {
            Text,
            Open,
            StartName,
            Attributes,
            EndName,
            EndRest,
            Bang,
            BangDash,
            Comment,
            Cdata,
            Declaration,
            Instruction,
        }

        private sealed class XmlScanner(XmlFolds owner, int capacity) : Scanner(capacity)
        {
            private static readonly SearchValues<char> InText = SearchValues.Create("<\n");
            private static readonly SearchValues<char> InTag = SearchValues.Create("\"'>/\n");
            private static readonly SearchValues<char> InDoubleQuotes = SearchValues.Create("\"\n");
            private static readonly SearchValues<char> InSingleQuotes = SearchValues.Create("'\n");
            private static readonly SearchValues<char> InComment = SearchValues.Create("->\n");
            private static readonly SearchValues<char> InCdata = SearchValues.Create("]>\n");
            private static readonly SearchValues<char> InDeclaration = SearchValues.Create("[]>\n");
            private static readonly SearchValues<char> InInstruction = SearchValues.Create(">\n");

            private State state;
            private char[] names = new char[256];
            private int namesLength;
            private int nameStart;
            private int markupLine;
            private char quote;
            private bool selfClosing;
            private int run;
            private int brackets;

            protected override int Skip(ReadOnlySpan<char> rest)
            {
                int skip = state switch
                {
                    State.Text => rest.IndexOfAny(InText),
                    State.Attributes => rest.IndexOfAny(quote switch { '"' => InDoubleQuotes, '\'' => InSingleQuotes, _ => InTag }),
                    State.Comment => rest.IndexOfAny(InComment),
                    State.Cdata => rest.IndexOfAny(InCdata),
                    State.Declaration => rest.IndexOfAny(InDeclaration),
                    State.Instruction => rest.IndexOfAny(InInstruction),
                    _ => 0,
                };
                if (skip != 0)
                {
                    // What was passed over ends a tag's '/' and a comment's run of '-' (or CDATA's of ']') before it.
                    selfClosing = false;
                    run = 0;
                }
                return skip;
            }

            protected override void Next(char c)
            {
                switch (state)
                {
                    case State.Text:
                        if (c == '<')
                        {
                            markupLine = line;
                            markupLineEnd = -1;
                            state = State.Open;
                        }
                        break;
                    case State.Open:
                        nameStart = namesLength;
                        if (c == '/')
                        {
                            state = State.EndName;
                        }
                        else if (c == '!')
                        {
                            state = State.Bang;
                        }
                        else if (c == '?')
                        {
                            state = State.Instruction;
                        }
                        else if (MarkupNames.IsNameStart(c))
                        {
                            Append(c);
                            state = State.StartName;
                        }
                        else
                        {
                            state = State.Text;
                        }
                        break;
                    case State.StartName:
                        if (MarkupNames.IsNameChar(c))
                        {
                            Append(c);
                            break;
                        }
                        quote = '\0';
                        selfClosing = false;
                        state = State.Attributes;
                        Attribute(c);
                        break;
                    case State.Attributes:
                        Attribute(c);
                        break;
                    case State.EndName:
                        if (MarkupNames.IsNameChar(c))
                        {
                            Append(c);
                        }
                        else if (c == '>')
                        {
                            EndTag();
                        }
                        else
                        {
                            state = State.EndRest;
                        }
                        break;
                    case State.EndRest:
                        if (c == '>')
                        {
                            EndTag();
                        }
                        break;
                    case State.Bang:
                        run = 0;
                        brackets = 0;
                        state = c switch
                        {
                            '-' => State.BangDash,
                            '[' => State.Cdata,
                            _ => State.Declaration,
                        };
                        break;
                    case State.BangDash:
                        state = c == '-' ? State.Comment : State.Declaration;
                        break;
                    case State.Comment:
                        if (c == '>' && run >= 2)
                        {
                            Close(markupLine, markupLineEnd, "...-->", FoldKind.Comment);
                            state = State.Text;
                        }
                        run = c == '-' ? run + 1 : 0;
                        break;
                    case State.Cdata:
                        if (c == '>' && run >= 2)
                        {
                            state = State.Text;
                        }
                        run = c == ']' ? run + 1 : 0;
                        break;
                    case State.Declaration:
                        if (MarkupNames.EndsDeclaration(c, ref brackets))
                        {
                            state = State.Text;
                        }
                        break;
                    case State.Instruction:
                        if (c == '>' && previous == '?')
                        {
                            state = State.Text;
                        }
                        break;
                }
            }

            /// <summary>A start tag's attributes, read to its end: an element opened, or one closed by its own <c>/&gt;</c>.</summary>
            private void Attribute(char c)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    return;
                }
                if (c is '"' or '\'')
                {
                    quote = c;
                    selfClosing = false;
                    return;
                }
                if (c != '>')
                {
                    selfClosing = c == '/';
                    return;
                }
                state = State.Text;
                if (selfClosing)
                {
                    Close(markupLine, markupLineEnd, "... />", FoldKind.Tag);
                    namesLength = nameStart;
                    return;
                }
                Push(new Opened { Line = markupLine, LineEnd = markupLineEnd, NameStart = nameStart, NameLength = namesLength - nameStart });
            }

            /// <summary>An end tag read: the innermost element open of its name closed, those open inside it dropped.</summary>
            private void EndTag()
            {
                state = State.Text;
                ReadOnlySpan<char> name = names.AsSpan(nameStart, namesLength - nameStart);
                for (int k = depth - 1; k >= 0; k--)
                {
                    Opened open = stack[k];
                    if (names.AsSpan(open.NameStart, open.NameLength).SequenceEqual(name))
                    {
                        Close(open.Line, open.LineEnd, owner.Closer(name), FoldKind.Tag);
                        PopTo(k);
                        namesLength = open.NameStart;
                        return;
                    }
                }
                namesLength = nameStart;
            }

            private void Append(char c)
            {
                if (namesLength == names.Length)
                {
                    Array.Resize(ref names, names.Length * 2);
                }
                names[namesLength++] = c;
            }
        }
    }

    /// <summary>The JSON scan: braces and brackets outside strings paired, a closer that pairs with nothing open passed
    /// over, and the comments JSON with comments allows (appsettings.json, tsconfig.json).</summary>
    private static class JsonFolds
    {
        public static List<FoldRegion> Find(TextSnapshot text, int capacity) => new JsonScanner(capacity).Read(text);

        private enum State
        {
            Value,
            String,
            Escape,
            Slash,
            LineComment,
            BlockComment,
            BlockStar,
        }

        private sealed class JsonScanner(int capacity) : Scanner(capacity)
        {
            private static readonly SearchValues<char> InValue = SearchValues.Create("\"/{}[]\n");
            private static readonly SearchValues<char> InString = SearchValues.Create("\"\\\n");
            private static readonly SearchValues<char> InBlockComment = SearchValues.Create("*\n");

            private State state;
            private int commentLine;

            protected override int Skip(ReadOnlySpan<char> rest) => state switch
            {
                State.Value => rest.IndexOfAny(InValue),
                State.String => rest.IndexOfAny(InString),
                State.LineComment => rest.IndexOf('\n'),
                State.BlockComment => rest.IndexOfAny(InBlockComment),
                _ => 0,
            };

            protected override void Next(char c)
            {
                switch (state)
                {
                    case State.Value:
                        Value(c);
                        break;
                    case State.String:
                        // A break ends a string JSON never lets run on, so one left open spoils no more than its line.
                        state = c == '\\' ? State.Escape : c is '"' or '\n' ? State.Value : State.String;
                        break;
                    case State.Escape:
                        state = State.String;
                        break;
                    case State.Slash:
                        if (c == '/')
                        {
                            state = State.LineComment;
                        }
                        else if (c == '*')
                        {
                            commentLine = line;
                            markupLineEnd = -1;
                            state = State.BlockComment;
                        }
                        else
                        {
                            state = State.Value;
                            Value(c);
                        }
                        break;
                    case State.LineComment:
                        state = c == '\n' ? State.Value : State.LineComment;
                        break;
                    case State.BlockComment:
                        state = c == '*' ? State.BlockStar : State.BlockComment;
                        break;
                    case State.BlockStar:
                        if (c == '/')
                        {
                            Close(commentLine, markupLineEnd, "...*/", FoldKind.Comment);
                            state = State.Value;
                        }
                        else
                        {
                            state = c == '*' ? State.BlockStar : State.BlockComment;
                        }
                        break;
                }
            }

            private void Value(char c)
            {
                switch (c)
                {
                    case '"':
                        state = State.String;
                        break;
                    case '/':
                        state = State.Slash;
                        break;
                    case '{' or '[':
                        Push(new Opened { Line = line, LineEnd = -1, Bracket = c });
                        break;
                    case '}' or ']':
                        char opener = c == '}' ? '{' : '[';
                        for (int k = depth - 1; k >= 0; k--)
                        {
                            if (stack[k].Bracket == opener)
                            {
                                Close(stack[k].Line, stack[k].LineEnd, c == '}' ? "...}" : "...]", FoldKind.Code);
                                PopTo(k);
                                break;
                            }
                        }
                        break;
                }
            }
        }
    }
}
