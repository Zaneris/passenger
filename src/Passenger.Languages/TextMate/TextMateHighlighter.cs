using System.Diagnostics;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;

namespace Passenger.Languages.TextMate;

/// <summary>Colours a file by its language's TextMate grammar (XML, JSON, YAML, Markdown, HTML, CSS, JavaScript,
/// TypeScript, shell, Dockerfile), off the UI thread. Each line keeps the state the grammar ended it in, so a key reads its
/// line again and goes on only while a line ends in another state than before (a comment opened, a fence closed); until a
/// pass lands, its colours are carried through the edits since. A long pass shows what it has read as it goes, and a
/// newer key ends it a little way on, the next pass going on from there.</summary>
public sealed partial class TextMateHighlighter : IHighlighter, IBraceMatcher, IDisposable
{
    /// <summary>A line longer than this (minified, generated) is left plain and its state carried over.</summary>
    private const int LongestLine = 20_000;

    /// <summary>Lines read under one hold of the grammar's lock, between looks at whether the text has moved on.</summary>
    private const int LinesAtATime = 128;

    /// <summary>Lines a pass reads before a newer key may end it: a screen's worth past the edit, at least.</summary>
    private const int LinesBeforeStopping = 512;

    private static readonly TimeSpan LineLimit = TimeSpan.FromSeconds(1);

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
        BackgroundWork.Begin();
        UnawaitedWork.Observe(Task.Run(() => Pass(old, text, first, tail, passVersion)).ContinueWith(done => OnUi(() =>
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
        IGrammar rules = grammar.Rules;
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
        IStateStack? state = first == 0 ? null : old![first - 1].End;
        int staleFrom = count;
        int line = first;
        long shown = began;
        List<SyntaxSpan> scratch = [];
        while (line < count)
        {
            int until = Math.Min(count, line + LinesAtATime);
            int readFrom = line;
            bool converged = false;
            lock (grammar.Gate)
            {
                for (; line < until; line++)
                {
                    TokenLine read = Read(rules, text, line, state, scratch);
                    built.Add(read);
                    state = read.End;
                    int next = line + 1;
                    int was = next - 1 - delta;
                    if (next >= convergeFrom && next < count && was >= 0 && was < old!.StaleFrom && old[was].End is { } end && end.Equals(state))
                    {
                        built.AddRange(old, was + 1, oldCount - was - 1);
                        staleFrom = Math.Max(next, old.StaleFrom + delta);
                        line = next;
                        converged = true;
                        break;
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

    /// <summary>One line's colours and the state it ends in.</summary>
    private TokenLine Read(IGrammar rules, TextSnapshot text, int line, IStateStack? state, List<SyntaxSpan> scratch)
    {
        LineSpan span = text.GetLine(line);
        if (span.Length > LongestLine)
        {
            return new TokenLine([], state);
        }
        // The grammar reads a line with its break, which it would otherwise copy the line to add.
        char[] chars = new char[span.Length + 1];
        text.CopyTo(span.Start, chars.AsSpan(0, span.Length));
        chars[span.Length] = '\n';
        ITokenizeLineResult2 result = rules.TokenizeLine2(new LineText(chars.AsMemory()), state, LineLimit);
        int[] tokens = result.Tokens;
        scratch.Clear();
        for (int i = 0; i < tokens.Length; i += 2)
        {
            int start = tokens[i];
            int end = Math.Min(i + 2 < tokens.Length ? tokens[i + 2] : span.Length, span.Length);
            TokenKind kind = grammar.KindOf(EncodedTokenAttributes.GetForeground(tokens[i + 1]));
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
        return new TokenLine(scratch.Count == 0 ? [] : [.. scratch], result.RuleStack);
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
