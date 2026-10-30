using System.Diagnostics;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Tint;

namespace Passenger.Languages.TextMate;

/// <summary>Colours a file by its language's TextMate grammar (XML, JSON, YAML, Markdown, HTML, CSS, JavaScript,
/// TypeScript, shell, Dockerfile), off the UI thread. Each line keeps the state the grammar ended it in, so a key reads its
/// line again and goes on only while a line ends in another state than before (a comment opened, a fence closed); until a
/// pass lands, its colours are carried through the edits since. A long pass shows what it has read as it goes, and a
/// newer key ends it a little way on, the next pass going on from there.</summary>
public sealed partial class TextMateHighlighter : IHighlighter, IBraceMatcher, IDisposable
{
    /// <summary>A line longer than this (minified, generated) is left plain, read only for the state it ends in, once a pass
    /// within <see cref="LongLineTime"/>.</summary>
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
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;

    /// <summary>The edits made since <see cref="tableText"/>, with the lines each changed.</summary>
    private readonly List<Batch> since = [];
    private LineTable? table;
    private TextSnapshot? tableText;
    private int version;
    private bool running;
    private bool disposed;
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

    /// <summary>How many times the grammar read a line too long to colour, each once within <see cref="LongLineTime"/>, and
    /// how many of those ran out of it, ending in the state they began in.</summary>
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

    public void Dispose()
    {
        disposed = true;
        document.Changed -= OnChanged;
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
        // Held from here, the pass's own start holding nothing, until what it coloured lands on the UI thread.
        BackgroundWork.Begin();
        UnawaitedWork.Observe(BackgroundWork.Unheld(() => Pass(old, text, first, tail, passVersion)).ContinueWith(done => OnUi(() =>
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
                Show(done.Result, text, passVersion);
                Tokenize();
            }
            finally
            {
                BackgroundWork.End();
            }
        }), TaskScheduler.Default));
    }

    /// <summary>Reads the lines of <paramref name="text"/> from <paramref name="first"/> on, with the state the line above
    /// ended in, until a line ends in the state its old line did with only unchanged lines after it (the last
    /// <paramref name="tail"/> lines are those of <paramref name="old"/>'s text), and takes the rest from the old table.</summary>
    private LineTable Pass(LineTable? old, TextSnapshot text, int first, int tail, int passVersion)
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
                state = EndOfLong(text, longLine, state, limit, longLimit, ref buffer);
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
                        TokenLine read = Read(rules, limit, text, span, state, scratch, ref buffer);
                        built.Add(read);
                        state = read.End;
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
                OnUi(() => Show(progress, text, passVersion));
                shown = Stopwatch.GetTimestamp();
            }
        }
        Interlocked.Increment(ref passes);
        Interlocked.Add(ref workTicks, Stopwatch.GetTimestamp() - began);
        return built.Build(staleFrom);

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

    /// <summary>Takes a pass's colours, of <paramref name="text"/>, as the newest, and asks for the lines to be drawn again.</summary>
    private void Show(LineTable lines, TextSnapshot text, int passVersion)
    {
        if (disposed)
        {
            return;
        }
        table = lines;
        tableText = text;
        since.RemoveAll(batch => batch.Version <= passVersion);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One line's colours and the state it ends in, reusing the pass's buffer between lines. A line read past
    /// <paramref name="limit"/> is read once more, its grammar's patterns now parsed; past it again, it keeps the colours
    /// found and ends in the state it began in, so a rule it was stopped inside never reaches the lines after it.</summary>
    private TokenLine Read(Tokenizer rules, TimeSpan limit, TextSnapshot text, LineSpan span, StateStack? state, List<SyntaxSpan> scratch, ref char[] buffer)
    {
        if (buffer.Length < span.Length)
        {
            buffer = new char[Math.Max(span.Length, Math.Min(LongestLine, buffer.Length * 2))];
        }
        // Tint accepts the line without its break and appends it in a reusable buffer.
        text.CopyTo(span.Start, buffer.AsSpan(0, span.Length));
        rules.TimeBudget = limit;
        StateStack before = state ?? StateStack.Initial;
        ReadOnlySpan<EncodedToken> tokens = rules.TokenizeLineEncoded(buffer.AsSpan(0, span.Length), before, out StateStack next);
        if (rules.StoppedEarly)
        {
            tokens = rules.TokenizeLineEncoded(buffer.AsSpan(0, span.Length), before, out next);
            if (rules.StoppedEarly)
            {
                Interlocked.Increment(ref linesOutOfTime);
                next = before;
            }
        }
        scratch.Clear();
        for (int i = 0; i < tokens.Length; i++)
        {
            int start = tokens[i].Start;
            int end = Math.Min(i + 1 < tokens.Length ? tokens[i + 1].Start : span.Length, span.Length);
            TokenKind kind = grammar.KindOf(EncodedTokenAttributes.GetForeground(tokens[i].Metadata));
            if (end <= start || kind == TokenKind.Text)
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
        return new TokenLine(scratch.Count == 0 ? [] : [.. scratch], next);
    }

    /// <summary>The state a line longer than <see cref="LongestLine"/> ends in, read once by the grammar's long rules within
    /// <paramref name="longLimit"/>, no hold of the grammar's own rules kept meanwhile; past that time, the state it began
    /// in, so a rule it was stopped inside never reaches the lines after it.</summary>
    private StateStack EndOfLong(TextSnapshot text, LineSpan span, StateStack? state, TimeSpan limit, TimeSpan longLimit, ref char[] buffer)
    {
        if (buffer.Length < span.Length)
        {
            buffer = new char[span.Length];
        }
        text.CopyTo(span.Start, buffer.AsSpan(0, span.Length));
        StateStack before = state ?? StateStack.Initial;
        Interlocked.Increment(ref longLinesRead);
        lock (grammar.LongGate)
        {
            Tokenizer rules = grammar.LongRules;
            if (!grammar.LongRulesWarm)
            {
                rules.TimeBudget = limit;
                _ = rules.TokenizeLineEncoded(buffer.AsSpan(0, WarmingLength), before, out _);
                grammar.LongRulesWarm = true;
            }
            rules.TimeBudget = longLimit;
            _ = rules.TokenizeLineEncoded(buffer.AsSpan(0, span.Length), before, out StateStack next);
            if (!rules.StoppedEarly)
            {
                return next;
            }
        }
        Interlocked.Increment(ref longLinesOutOfTime);
        return before;
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

    /// <summary>One change's edits, and the lines it touched: the first, the same in every text since, and how many at the
    /// end it left as they were.</summary>
    private sealed record Batch(int Version, IReadOnlyList<TextEdit> Edits, int First, int Tail);
}
