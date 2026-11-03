using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages;

/// <summary>Colours of what names are (a member, a local, a class), read elsewhere for a version of the text and given
/// here (<see cref="Set"/>), over a grammar's colours, which they replace where they reach. Those given are carried
/// through every edit made since, as a range is drawn, so they never lag a key nor flash plain; a set that says what is
/// already shown, carried, raises nothing, so the rows drawn stand and a key costs what it changed. A UI-thread object.</summary>
public sealed class ColourLayer : IHighlighter, IBraceMatcher, IDisposable
{
    private readonly IHighlighter syntax;
    private readonly TextDocument document;

    /// <summary>Each change since the colours given, with the version of the text it made.</summary>
    private readonly List<(int Version, IReadOnlyList<TextEdit> Edits)> history = [];

    private SyntaxSpan[] spans = [];
    private int version = -1;

    /// <summary>The colours last answered for each range of the text drawn, with the grammar's they were made from and how
    /// many sets of colours had been given: a range asked again (a frame's rows kept, each checked again) is answered
    /// with no new list while neither has changed. Of one text at a time.</summary>
    private readonly Dictionary<(int Start, int Length), (IReadOnlyList<SyntaxSpan> Parsed, int Given, IReadOnlyList<SyntaxSpan> Merged)> answered = [];
    private TextSnapshot? answeredFor;
    private int given;

    public ColourLayer(IHighlighter syntax, TextDocument document)
    {
        this.syntax = syntax;
        this.document = document;
        syntax.Changed += OnSyntaxChanged;
        document.Changed += OnTextChanged;
    }

    public event EventHandler? Changed;

    /// <summary>The grammar's colours this layers over, which stand alone once it is let go.</summary>
    public IHighlighter Syntax => syntax;

    /// <summary>The grammar's word that it kept every colour it had given, while it is raised.</summary>
    public bool KeptColours { get; private set; }

    /// <summary>Colours read for a version of the text, in order of their starts, and whether they differ from those
    /// given before as the edits since carry them (<see cref="Same"/>, worked out where they were read); ignored where of
    /// an older version than those shown. Raises <see cref="Changed"/> only where they differ.</summary>
    public void Set(SyntaxSpan[] colours, int at, bool differs)
    {
        if (at < version || at > document.Version)
        {
            return;
        }
        spans = colours;
        version = at;
        given++;
        int done = 0;
        while (done < history.Count && history[done].Version <= at)
        {
            done++;
        }
        history.RemoveRange(0, done);
        if (differs)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether colours given for a text, carried through the edits that made another (sorted, in the first's
    /// text), say what colours read for the other say. Pure, for any thread.</summary>
    public static bool Same(SyntaxSpan[] before, IReadOnlyList<TextEdit> edits, SyntaxSpan[] after)
    {
        List<SyntaxSpan> carried = [.. before];
        CarriedSpans.Forward(carried, edits, Word);
        return carried.Count == after.Length && System.Runtime.InteropServices.CollectionsMarshal.AsSpan(carried).SequenceEqual(after);
    }

    public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length)
    {
        IReadOnlyList<SyntaxSpan> parsed = syntax.GetSpans(text, start, length);
        if (spans.Length == 0)
        {
            return parsed;
        }
        if (!ReferenceEquals(answeredFor, text))
        {
            answered.Clear();
            answeredFor = text;
        }
        if (answered.TryGetValue((start, length), out (IReadOnlyList<SyntaxSpan> Parsed, int Given, IReadOnlyList<SyntaxSpan> Merged) kept)
            && kept.Given == given && Alike(kept.Parsed, parsed))
        {
            return kept.Merged;
        }
        IReadOnlyList<SyntaxSpan> merged = Merged(parsed, Carried(spans, start, start + length));
        answered[(start, length)] = (parsed, given, merged);
        return merged;
    }

    private static bool Alike(IReadOnlyList<SyntaxSpan> a, IReadOnlyList<SyntaxSpan> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The grammar's colours with the names' over them, a grammar's span a name lies in cut around it.</summary>
    private static IReadOnlyList<SyntaxSpan> Merged(IReadOnlyList<SyntaxSpan> parsed, List<SyntaxSpan> named)
    {
        if (named.Count == 0)
        {
            return parsed;
        }
        List<SyntaxSpan> merged = new(parsed.Count + named.Count);
        int n = 0;
        for (int p = 0; p < parsed.Count; p++)
        {
            SyntaxSpan span = parsed[p];
            while (n < named.Count && named[n].End <= span.Start)
            {
                n++;
            }
            // A grammar's span a name lies in is cut around it: the name's colour, the rest the grammar's.
            int from = span.Start;
            for (int i = n; i < named.Count && named[i].Start < span.End; i++)
            {
                if (named[i].Start > from)
                {
                    merged.Add(new SyntaxSpan(from, named[i].Start - from, span.Kind));
                }
                from = Math.Max(from, named[i].End);
            }
            if (from < span.End)
            {
                merged.Add(new SyntaxSpan(from, span.End - from, span.Kind));
            }
        }
        merged.AddRange(named);
        merged.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return merged;
    }

    public void Showing(TextSnapshot text, int start, int length) => syntax.Showing(text, start, length);

    public BracePair? PairAt(TextSnapshot text, int offset, bool far = false) => (syntax as IBraceMatcher)?.PairAt(text, offset, far);

    public BracePair? TagPairAt(TextSnapshot text, int caret, bool far = false) => (syntax as IBraceMatcher)?.TagPairAt(text, caret, far);

    public void Dispose()
    {
        syntax.Changed -= OnSyntaxChanged;
        document.Changed -= OnTextChanged;
    }

    private void OnSyntaxChanged(object? sender, EventArgs e)
    {
        KeptColours = syntax.KeptColours;
        Changed?.Invoke(this, EventArgs.Empty);
        KeptColours = false;
    }

    private void OnTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (spans.Length == 0)
        {
            return;
        }
        if (history.Count == 0 || history[^1].Version == version || !CarriedSpans.Join(history, e.Version, e.Edits))
        {
            history.Add((e.Version, e.Edits));
        }
    }

    /// <summary>The colours given near a range of the text as it stands: found in their own text by halving, then carried
    /// through each edit since.</summary>
    private List<SyntaxSpan> Carried(SyntaxSpan[] all, int start, int end)
    {
        int last = history.Count;
        int from = start;
        int to = end;
        for (int i = last - 1; i >= 0; i--)
        {
            from = CarriedSpans.Back(history[i].Edits, from, toEnd: false);
            to = CarriedSpans.Back(history[i].Edits, to, toEnd: true) + 1;
        }
        int low = Sorted.FirstAfter(all, static s => s.End, from - 1);
        int high = low;
        while (high < all.Length && all[high].Start <= to)
        {
            high++;
        }
        List<SyntaxSpan> found = new(high - low);
        for (int i = low; i < high; i++)
        {
            found.Add(all[i]);
        }
        for (int i = 0; i < last; i++)
        {
            CarriedSpans.Forward(found, history[i].Edits, Word);
        }
        return found;
    }

    /// <summary>Whether text typed inside a name keeps it one: letters, digits and underscores.</summary>
    private static bool Word(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }
}
