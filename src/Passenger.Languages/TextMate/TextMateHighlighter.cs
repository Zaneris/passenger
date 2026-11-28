using System.Diagnostics;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Tint;

namespace Passenger.Languages.TextMate;

/// <summary>Colours a file by its language's TextMate grammar (XML, JSON, YAML, Markdown, HTML, CSS, JavaScript,
/// TypeScript, shell, Dockerfile, Python), off the UI thread. Each line keeps the state the grammar ended it in, so a key reads its
/// line again and goes on only while a line ends in another state than before (a comment opened, a fence closed); until a
/// pass lands, its colours are carried through the edits since. A long pass shows what it has read as it goes, and a
/// newer key ends it a little way on, the next pass going on from there.</summary>
public sealed partial class TextMateHighlighter : IHighlighter, IBraceMatcher, IDisposable
{
    /// <summary>A line longer than this (minified, generated) is left plain, read only for the state it ends in, once a pass
    /// within <see cref="LongLineTime"/>, and once more where that runs out.</summary>
    private const int LongestLine = 20_000;

    /// <summary>How much of the first long line a grammar's long rules read is read first with the time any line has, so
    /// the parse of their first patterns is not taken out of the line's own time.</summary>
    private const int WarmingLength = 1_000;

    /// <summary>Lines read under one hold of the grammar's lock, between looks at whether the text has moved on.</summary>
    private const int LinesAtATime = 128;

    /// <summary>Lines a pass reads before a newer key may end it: a screen's worth past the edit, at least.</summary>
    private const int LinesBeforeStopping = 512;

    /// <summary>The variable that gives <see cref="LineTime"/> in milliseconds.</summary>
    [IdentifierHome]
    public const string LineTimeVariable = "PASSENGER_GRAMMAR_LINE_MS";

    /// <summary>How often a long pass shows what it has read so far.</summary>
    private static readonly TimeSpan ShowEvery = TimeSpan.FromMilliseconds(60);

    private readonly TextDocument document;
    private readonly Grammar grammar;

    /// <summary>The edits made since <see cref="tableText"/>, with the lines each changed.</summary>
    private readonly List<Batch> since = [];
    private LineTable? table;
    private TextSnapshot? tableText;
    private int version;
    private bool running;
    private bool disposed;

    /// <summary>The pass under way's hold on the IDE's quiet, let go as it lands or as the highlighter is let go.</summary>
    private IDisposable? passHold;
    private long linesRead;
    private long linesOutOfTime;
    private long longLinesRead;
    private long longLinesOutOfTime;
    private int passes;
    private long workTicks;

    private TextMateHighlighter(TextDocument document, Grammar grammar)
    {
        this.document = document;
        this.grammar = grammar;
        document.Changed += OnChanged;
        Tokenize();
    }

    public event EventHandler? Changed;

    /// <summary>A highlighter of a file by a grammar carried here.</summary>
    internal static TextMateHighlighter For(GrammarFile grammar, TextDocument document) => new(document, Grammar.For(grammar));

    /// <summary>What colouring this file has cost off the UI thread: the lines the grammar read, the passes, and the time
    /// they took.</summary>
    public (long Lines, int Passes, TimeSpan Time) Worked =>
        (Interlocked.Read(ref linesRead), Volatile.Read(ref passes), Stopwatch.GetElapsedTime(0, Interlocked.Read(ref workTicks)));

    /// <summary>How long the grammar may read one line, checked between its searches: 1 s, or what
    /// <see cref="LineTimeVariable"/> gives; a value there that is no whole number of milliseconds above 0 is fatal.</summary>
    public static TimeSpan LineTime => EnvironmentValues.Milliseconds(LineTimeVariable, TimeSpan.FromSeconds(1));

    /// <summary>How long the grammar may read a line too long to colour for the state it ends in: a tenth of
    /// <see cref="LineTime"/>.</summary>
    public static TimeSpan LongLineTime => LineTime / 10;

    /// <summary>How many times a line ran out of <see cref="LineTime"/> twice over, read once more and out of it again: each
    /// kept the colours found and ended in the state it began in.</summary>
    public long LinesOutOfTime => Interlocked.Read(ref linesOutOfTime);

    /// <summary>How many times the grammar read a line too long to colour, each within <see cref="LongLineTime"/> and once
    /// more where that ran out, and how many of those ran out of it both times, ending in the state they began in.</summary>
    public (long Read, long OutOfTime) LongLines => (Interlocked.Read(ref longLinesRead), Interlocked.Read(ref longLinesOutOfTime));

    /// <summary>Why the grammar could not colour the file, when it could not.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Raised on the UI thread when the grammar fails to colour the file, which it then stops trying.</summary>
    public event EventHandler<Exception>? Failed;

    public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot snapshot, int start, int length)
    {
        if (table is not { } lines || tableText is not { } text)
        {
            return [];
        }
        int from = start;
        int to = start + length;
        for (int i = since.Count - 1; i >= 0; i--)
        {
            from = CarriedSpans.Back(since[i].Edits, from, toEnd: false);
            to = CarriedSpans.Back(since[i].Edits, to, toEnd: true);
        }
        // One character to the left, so a word being typed at its end is found and its colour carried over the new letters.
        from = Math.Clamp(since.Count > 0 ? from - 1 : from, 0, text.Length);
        to = Math.Clamp(to, from, text.Length);
        List<SyntaxSpan> spans = [];
        int last = Math.Min(text.GetLineFromOffset(Math.Max(from, to - 1)), lines.Count - 1);
        for (int line = text.GetLineFromOffset(from); line <= last; line++)
        {
            int lineStart = text.GetLineStart(line);
            foreach (SyntaxSpan span in lines[line].Spans)
            {
                int a = Math.Max(lineStart + span.Start, from);
                int b = Math.Min(lineStart + span.End, to);
                if (b > a)
                {
                    spans.Add(new SyntaxSpan(a, b - a, span.Kind));
                }
            }
        }
        foreach (Batch batch in since)
        {
            CarriedSpans.Forward(spans, batch.Edits);
        }
        return spans;
    }

    /// <summary>Whether the grammar has read the whole of <paramref name="text"/> as it stands, so the states lines end in are
    /// known for every line.</summary>
    public bool HasRead(TextSnapshot text) => table is { Complete: true } && ReferenceEquals(tableText, text) && since.Count == 0;

    /// <summary>The lines of <paramref name="text"/> that begin inside one of the grammar's strings, a Python f-string (whose
    /// string scopes open and close around each stretch of its text) or a Markdown code block, whose leading whitespace is
    /// their own; never a YAML plain scalar's next line, which YAML folds. A block scalar's string has no end to read and may
    /// open on its first line, so its lines are those ending in it under a line ending in the scalar; a line ending in an
    /// indented code block is the block's. Null until the grammar has read the whole text as it stands (<see cref="HasRead"/>).</summary>
    public IReadOnlySet<int>? StringLines(TextSnapshot text)
    {
        if (!HasRead(text) || table is not { } lines)
        {
            return null;
        }
        HashSet<int> inside = [];
        for (int line = 1; line < lines.Count; line++)
        {
            StateStack? before = lines[line - 1].End;
            if (InScope(before, IsRunningString) || (InScope(before, IsBlockScalar) && InScope(lines[line].End, IsBlockString)) || InScope(lines[line].End, IsIndentedCode))
            {
                inside.Add(line);
            }
        }
        return inside;
    }

    private static bool InScope(StateStack? state, Func<string, bool> matches)
    {
        for (AttributedScopeStack? scope = state?.Scopes; scope is not null; scope = scope.Parent)
        {
            if (matches(scope.ScopeName))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRunningString(string scope) => scope.StartsWith("markup.fenced_code", StringComparison.Ordinal)
        || scope.StartsWith("meta.fstring", StringComparison.Ordinal)
        || (scope.StartsWith("string.", StringComparison.Ordinal) && !IsBlockString(scope) && !scope.StartsWith("string.unquoted.plain", StringComparison.Ordinal));

    private static bool IsIndentedCode(string scope) => scope.StartsWith("markup.raw.block", StringComparison.Ordinal);

    private static bool IsBlockString(string scope) => scope.StartsWith("string.unquoted.block", StringComparison.Ordinal);

    private static bool IsBlockScalar(string scope) => scope.StartsWith("meta.scalar", StringComparison.Ordinal);

    public void Dispose()
    {
        disposed = true;
        document.Changed -= OnChanged;
        // A pass under way lands on nothing now, so nothing waits for it.
        passHold?.Dispose();
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        version++;
        TextSnapshot before = e.Before;
        int first = e.Edits.Count == 0 ? before.LineCount - 1 : before.GetLineFromOffset(e.Edits[0].Start);
        int tail = e.Edits.Count == 0 ? 0 : before.LineCount - 1 - before.GetLineFromOffset(e.Edits[^1].End);
        since.Add(new Batch(version, e.Edits, first, tail));
        Tokenize();
    }

    /// <summary>Starts a pass over what changed since the last, unless one is under way (it starts the next as it lands).</summary>
    private void Tokenize()
    {
        if (running || disposed || Failure is not null || (table is { Complete: true } && since.Count == 0))
        {
            return;
        }
        running = true;
        LineTable? old = table;
        TextSnapshot text = document.Snapshot;
        int passVersion = version;
        int first = old?.StaleFrom ?? 0;
        int tail = int.MaxValue;
        foreach (Batch batch in since)
        {
            first = Math.Min(first, batch.First);
            tail = Math.Min(tail, batch.Tail);
        }
        SynchronizationContext? landing = SynchronizationContext.Current;
        // Held from here, the pass's own start holding nothing, until what it coloured lands on the UI thread that asked.
        IDisposable hold = passHold = BackgroundWork.Scope();
        UnawaitedWork.Observe(BackgroundWork.Unheld(() => Pass(old, text, first, tail, passVersion, landing)).ContinueWith(done => OnUi(landing, () =>
        {
            try
            {
                running = false;
                if (disposed)
                {
                    return;
                }
                if (!done.IsCompletedSuccessfully)
                {
                    Failure = done.Exception?.InnerException ?? new OperationCanceledException();
                    Failed?.Invoke(this, Failure);
                    return;
                }
                Show(done.Result.Lines, text, passVersion, done.Result.Read);
                Tokenize();
            }
            finally
            {
                hold.Dispose();
            }
        }), TaskScheduler.Default));
    }

    /// <summary>Reads the lines of <paramref name="text"/> from <paramref name="first"/> on, with the state the line above
    /// ended in, until a line ends in the state its old line did with only unchanged lines after it (the last
    /// <paramref name="tail"/> lines are those of <paramref name="old"/>'s text), and takes the rest from the old table.</summary>
    private (LineTable Lines, (int From, int To)? Read) Pass(LineTable? old, TextSnapshot text, int first, int tail, int passVersion, SynchronizationContext? landing)
    {
        long began = Stopwatch.GetTimestamp();
        Tokenizer rules = grammar.Rules;
        TimeSpan limit = LineTime;
        TimeSpan longLimit = LongLineTime;
        int count = text.LineCount;
        int oldCount = old?.Count ?? 0;
        int delta = count - oldCount;
        first = old is null ? 0 : Math.Clamp(first, 0, Math.Min(Math.Min(count - 1, oldCount), old.StaleFrom));
        int convergeFrom = old is null ? int.MaxValue : count - Math.Min(tail, count);
        LineTableBuilder built = new();
        if (old is not null)
        {
            built.AddRange(old, 0, first);
        }
        StateStack? state = first == 0 ? null : old![first - 1].End;
        int staleFrom = count;
        int line = first;
        long shown = began;
        List<SyntaxSpan> scratch = [];
        char[] buffer = [];
        while (line < count)
        {
            int readFrom = line;
            bool converged = false;
            if (text.GetLine(line) is { Length: > LongestLine } longLine)
            {
                Interlocked.Increment(ref longLinesRead);
                (state, bool stopped) = ReadLong(grammar, text, longLine, state, limit, longLimit, ref buffer);
                if (stopped)
                {
                    Interlocked.Increment(ref longLinesOutOfTime);
                }
                built.Add(new TokenLine([], state));
                converged = Converged(++line);
            }
            else
            {
                int until = Math.Min(count, line + LinesAtATime);
                lock (grammar.Gate)
                {
                    while (!converged && line < until && text.GetLine(line) is { Length: <= LongestLine } span)
                    {
                        (state, bool stopped) = Read(grammar, rules, limit, text, span, state, scratch, ref buffer, includeText: false);
                        if (stopped)
                        {
                            Interlocked.Increment(ref linesOutOfTime);
                        }
                        built.Add(new TokenLine(scratch.Count == 0 ? [] : [.. scratch], state));
                        converged = Converged(++line);
                    }
                }
            }
            Interlocked.Add(ref linesRead, line - readFrom);
            if (converged || line >= count)
            {
                break;
            }
            if (line - first >= LinesBeforeStopping && (Volatile.Read(ref disposed) || Volatile.Read(ref version) != passVersion))
            {
                Rest(built, line);
                staleFrom = line;
                break;
            }
            if (Stopwatch.GetElapsedTime(shown) > ShowEvery)
            {
                // A long pass (a large file opening) shows the lines read so far, the screen's among them, as it goes.
                LineTable soFar = built.Build(line);
                LineTableBuilder partial = new();
                partial.AddRange(soFar, 0, soFar.Count);
                Rest(partial, line);
                LineTable progress = partial.Build(line);
                OnUi(landing, () => Show(progress, text, passVersion, null));
                shown = Stopwatch.GetTimestamp();
            }
        }
        Interlocked.Increment(ref passes);
        Interlocked.Add(ref workTicks, Stopwatch.GetTimestamp() - began);
        // The lines read, which alone may have taken other colours, unless the pass stopped short of where they converge.
        return (built.Build(staleFrom), staleFrom == count && old is not null ? (first, line) : null);

        // Whether the line before next, past those that changed, ends as its old line did: the rest then the old table's.
        bool Converged(int next)
        {
            int was = next - 1 - delta;
            if (next < convergeFrom || next >= count || was < 0 || was >= old!.StaleFrom || old[was].End is not { } end || !end.Equals(state))
            {
                return false;
            }
            built.AddRange(old, was + 1, oldCount - was - 1);
            staleFrom = Math.Max(next, old.StaleFrom + delta);
            return true;
        }

        // The lines past where a pass stopped keep what they had: blank where they changed, their old colours where not.
        void Rest(LineTableBuilder into, int from)
        {
            int blankTo = Math.Min(count, Math.Max(from, convergeFrom));
            into.AddBlank(blankTo - from);
            if (blankTo < count)
            {
                into.AddRange(old!, blankTo - delta, count - blankTo);
            }
        }
    }

    /// <summary>Takes a pass's colours, of <paramref name="text"/>, as the newest, and asks for the lines to be drawn again:
    /// those it read alone (<paramref name="read"/>, lines from and to) where nothing has been typed since.</summary>
    private void Show(LineTable lines, TextSnapshot text, int passVersion, (int From, int To)? read)
    {
        if (disposed)
        {
            return;
        }
        table = lines;
        tableText = text;
        since.RemoveAll(batch => batch.Version <= passVersion);
        ChangedWithin = read is ({ } from, { } to) && since.Count == 0 && ReferenceEquals(text, document.Snapshot)
            ? TextRange.FromBounds(text.GetLineStart(Math.Min(from, text.LineCount - 1)), to < text.LineCount ? text.GetLineStart(to) : text.Length)
            : null;
        Changed?.Invoke(this, EventArgs.Empty);
        ChangedWithin = null;
    }

    /// <summary>While <see cref="Changed"/> is raised, the lines the pass read anew, where they alone may have changed.</summary>
    public TextRange? ChangedWithin { get; private set; }

    /// <summary>One line's colours and the state it ends in, reusing the pass's buffer between lines. A line read past
    /// <paramref name="limit"/> is read once more, its grammar's patterns now parsed; past it again, it keeps the colours
    /// found and ends in the state it began in (<see cref="Began"/>), so a rule it was stopped inside never reaches the
    /// lines after it.</summary>
    private static (StateStack End, bool Stopped) Read(Grammar grammar, Tokenizer rules, TimeSpan limit, TextSnapshot text,
        LineSpan span, StateStack? state, List<SyntaxSpan> scratch, ref char[] buffer, bool includeText, int origin = 0)
    {
        if (buffer.Length < span.Length)
        {
            buffer = new char[Math.Max(span.Length, Math.Min(LongestLine, buffer.Length * 2))];
        }
        // Tint accepts the line without its break and appends it in a reusable buffer.
        text.CopyTo(span.Start, buffer.AsSpan(0, span.Length));
        ReadOnlySpan<EncodedToken> tokens = ReadTokens(rules, buffer.AsSpan(0, span.Length), state ?? StateStack.Initial, limit, out StateStack next);
        bool stopped = rules.StoppedEarly;
        scratch.Clear();
        for (int i = 0; i < tokens.Length; i++)
        {
            int start = origin + tokens[i].Start;
            int end = origin + Math.Min(i + 1 < tokens.Length ? tokens[i + 1].Start : span.Length, span.Length);
            TokenKind kind = grammar.KindOf(EncodedTokenAttributes.GetForeground(tokens[i].Metadata));
            if (end <= start || (!includeText && kind == TokenKind.Text))
            {
                continue;
            }
            if (scratch.Count > 0 && scratch[^1].Kind == kind && scratch[^1].End == start)
            {
                scratch[^1] = scratch[^1] with { Length = end - scratch[^1].Start };
            }
            else
            {
                scratch.Add(new SyntaxSpan(start, end - start, kind));
            }
        }
        return (stopped ? Began(grammar, state) : next, stopped);
    }

    /// <summary>The state a line longer than <see cref="LongestLine"/> ends in, read by the grammar's long rules within
    /// <paramref name="longLimit"/>, and once more past it, no hold of the grammar's own rules kept meanwhile; past that
    /// time twice, the state it began in (<see cref="Began"/>), so a rule it was stopped inside never reaches the lines
    /// after it.</summary>
    private static (StateStack End, bool Stopped) ReadLong(Grammar grammar, TextSnapshot text, LineSpan span,
        StateStack? state, TimeSpan limit, TimeSpan longLimit, ref char[] buffer)
    {
        if (buffer.Length < span.Length)
        {
            buffer = new char[span.Length];
        }
        text.CopyTo(span.Start, buffer.AsSpan(0, span.Length));
        StateStack before = state ?? StateStack.Initial;
        lock (grammar.LongGate)
        {
            Tokenizer rules = grammar.LongRules;
            if (!grammar.LongRulesWarm)
            {
                rules.TimeBudget = limit;
                _ = rules.TokenizeLineEncoded(buffer.AsSpan(0, WarmingLength), before, out _);
                grammar.LongRulesWarm = true;
            }
            _ = ReadTokens(rules, buffer.AsSpan(0, span.Length), before, longLimit, out StateStack next);
            if (!rules.StoppedEarly)
            {
                return (next, false);
            }
        }
        return (Began(grammar, state), true);
    }

    /// <summary>The first read may pay for parsing the scanners it reaches, so it gets one retry.</summary>
    private static ReadOnlySpan<EncodedToken> ReadTokens(Tokenizer rules, ReadOnlySpan<char> line, StateStack before, TimeSpan limit, out StateStack next)
    {
        rules.TimeBudget = limit;
        ReadOnlySpan<EncodedToken> tokens = rules.TokenizeLineEncoded(line, before, out next);
        return rules.StoppedEarly ? rules.TokenizeLineEncoded(line, before, out next) : tokens;
    }

    /// <summary>The state a line run out of its time ends in, the one it began in: for a first line, the state an empty
    /// first line ends in, which a first line read within its time ends in when it leaves nothing open, so the next pass
    /// that reads it so goes no further.</summary>
    private static StateStack Began(Grammar grammar, StateStack? state) => state ?? grammar.FirstLineEnd;

    /// <summary>Colours a fragment off the UI thread. Plain runs are kept so a host string's colour can be replaced.</summary>
    internal static List<SyntaxSpan> Fragment(GrammarFile file, string source, CancellationToken cancel)
    {
        Grammar grammar = Grammar.For(file);
        TextSnapshot text = TextSnapshot.From(source);
        TimeSpan limit = LineTime;
        TimeSpan longLimit = LongLineTime;
        List<SyntaxSpan> spans = [];
        List<SyntaxSpan> scratch = [];
        char[] buffer = [];
        StateStack? state = null;
        for (int i = 0; i < text.LineCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            LineSpan line = text.GetLine(i);
            if (line.Length > LongestLine)
            {
                state = ReadLong(grammar, text, line, state, limit, longLimit, ref buffer).End;
                spans.Add(new SyntaxSpan(line.Start, line.Length, TokenKind.Text));
                continue;
            }
            lock (grammar.Gate)
            {
                state = Read(grammar, grammar.Rules, limit, text, line, state, scratch, ref buffer, includeText: true, origin: line.Start).End;
            }
            spans.AddRange(scratch);
        }
        return spans;
    }

    /// <summary>Runs work where a pass was asked for: on the UI thread that asked, else at once.</summary>
    private static void OnUi(SynchronizationContext? landing, Action action)
    {
        if (landing is null)
        {
            action();
        }
        else
        {
            landing.Post(_ => action(), null);
        }
    }

    /// <summary>One change's edits, and the lines it touched: the first, the same in every text since, and how many at the
    /// end it left as they were.</summary>
    private sealed record Batch(int Version, IReadOnlyList<TextEdit> Edits, int First, int Tail);
}
