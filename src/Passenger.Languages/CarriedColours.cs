using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages;

/// <summary>Colours read elsewhere for a version of the text (a language server's, an analyzer's of what names are) over a
/// parser's, which they replace where they reach: a parser's span one lies in is cut around it, the rest keeping the
/// parser's colour (an attribute's value around the expression it holds). They are carried through every edit made since
/// they were read, as a range is drawn, so they never lag a key nor flash plain. The parser's word of where its colours
/// changed passes through as this one's, and brackets and tags stay the parser's to pair. A UI-thread object.</summary>
public abstract class CarriedColours : IHighlighter, IBraceMatcher, IDisposable
{
    private readonly IHighlighter syntax;
    private readonly TextDocument text;

    /// <summary>Each change since the oldest colours held (shown, or of a version still asked for), with the version of
    /// the text it made.</summary>
    private readonly List<(int Version, IReadOnlyList<TextEdit> Edits)> history = [];

    protected CarriedColours(IHighlighter syntax, TextDocument text)
    {
        this.syntax = syntax;
        this.text = text;
        syntax.Changed += OnSyntaxChanged;
        text.Changed += OnDocumentChanged;
    }

    public event EventHandler? Changed;

    /// <summary>The parser's colours these lie over, which stand alone once they are let go.</summary>
    public IHighlighter Syntax => syntax;

    /// <summary>The parser's word that it kept every colour it had given, while it is raised: those over it are as they
    /// were.</summary>
    public bool KeptColours { get; private set; }

    /// <summary>While <see cref="Changed"/> is raised, where the colours it was raised for differ: the parser's own range,
    /// or where those over it changed; null where that is not known.</summary>
    public TextRange? ChangedWithin { get; private set; }

    /// <summary>How many changes the colours held are carried through to the text as it stands, each range drawn going
    /// through each: a run of typing in one place is one, however long the colours take to come.</summary>
    public int Carrying => history.Count;

    public abstract IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot snapshot, int start, int length);

    public virtual void Showing(TextSnapshot snapshot, int start, int length) => syntax.Showing(snapshot, start, length);

    /// <summary>The parser's pair: brackets are its to tell.</summary>
    public BracePair? PairAt(TextSnapshot snapshot, int offset, bool far = false) => (syntax as IBraceMatcher)?.PairAt(snapshot, offset, far);

    /// <summary>The parser's tag pair, a Razor file's markup's.</summary>
    public BracePair? TagPairAt(TextSnapshot snapshot, int caret, bool far = false) => (syntax as IBraceMatcher)?.TagPairAt(snapshot, caret, far);

    public virtual void Dispose()
    {
        syntax.Changed -= OnSyntaxChanged;
        text.Changed -= OnDocumentChanged;
    }

    /// <summary>Whether colours held (shown, or asked for) are of the version of the text a change made, which keeps the
    /// changes after it apart from it.</summary>
    protected abstract bool Holds(int version);

    /// <summary>Takes a change of the text into those the colours are carried through, joined to the change before when no
    /// colours held are of the text that one made.</summary>
    protected virtual void OnTextChanged(DocumentChangedEventArgs e)
    {
        if (history.Count == 0 || Holds(history[^1].Version) || !CarriedSpans.Join(history, e.Version, e.Edits))
        {
            history.Add((e.Version, e.Edits));
        }
    }

    /// <summary>Lets go of the changes that made a version of the text or one before it: no colours held are of those.</summary>
    protected void Forget(int through)
    {
        int done = 0;
        while (done < history.Count && history[done].Version <= through)
        {
            done++;
        }
        history.RemoveRange(0, done);
    }

    /// <summary>How many changes the colours read for a version of the text are carried through.</summary>
    protected int CarriedThrough(int version) => history.Count - After(version);

    /// <summary>Each change made since a version of the text, in turn.</summary>
    protected IReadOnlyList<IReadOnlyList<TextEdit>> ChangesSince(int version) => [.. history.Skip(After(version)).Select(h => h.Edits)];

    /// <summary>Raises <see cref="Changed"/> for the colours over the parser's, which differ within a range, or anywhere where
    /// none is given.</summary>
    protected void Raise(TextRange? within)
    {
        ChangedWithin = within;
        Changed?.Invoke(this, EventArgs.Empty);
        ChangedWithin = null;
    }

    /// <summary>A range of a version of the text, carried through every change since to the text as it stands.</summary>
    protected TextRange Carried(TextRange range, int version)
    {
        for (int i = After(version); i < history.Count; i++)
        {
            range = TextEditing.MapRange(range, history[i].Edits);
        }
        return range;
    }

    /// <summary>The colours read for a version of the text, in order of their starts, near a range of the text as it
    /// stands: found in their own text by halving, then carried through every change since.</summary>
    protected List<SyntaxSpan> Carried(SyntaxSpan[] all, int version, int start, int end)
    {
        int first = After(version);
        int from = start;
        int to = end;
        for (int i = history.Count - 1; i >= first; i--)
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
        for (int i = first; i < history.Count; i++)
        {
            CarriedSpans.Forward(found, history[i].Edits, Word);
        }
        return found;
    }

    /// <summary>The parser's colours with others over them, in order, apart and of the same text: a parser's span one lies
    /// in is cut around it, its colour, the rest the parser's.</summary>
    protected static IReadOnlyList<SyntaxSpan> Over(IReadOnlyList<SyntaxSpan> parsed, List<SyntaxSpan> colours)
    {
        if (colours.Count == 0)
        {
            return parsed;
        }
        List<SyntaxSpan> merged = new(parsed.Count + colours.Count);
        int n = 0;
        for (int p = 0; p < parsed.Count; p++)
        {
            SyntaxSpan span = parsed[p];
            while (n < colours.Count && colours[n].End <= span.Start)
            {
                n++;
            }
            int from = span.Start;
            for (int i = n; i < colours.Count && colours[i].Start < span.End; i++)
            {
                if (colours[i].Start > from)
                {
                    merged.Add(new SyntaxSpan(from, colours[i].Start - from, span.Kind));
                }
                from = Math.Max(from, colours[i].End);
            }
            if (from < span.End)
            {
                merged.Add(new SyntaxSpan(from, span.End - from, span.Kind));
            }
        }
        merged.AddRange(colours);
        merged.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return merged;
    }

    /// <summary>Whether text typed inside a coloured name keeps it one: letters, digits and underscores, never what would
    /// split it.</summary>
    protected static bool Word(string typed)
    {
        foreach (char c in typed)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Where the changes made after a version of the text begin among those held.</summary>
    private int After(int version)
    {
        int first = 0;
        while (first < history.Count && history[first].Version <= version)
        {
            first++;
        }
        return first;
    }

    private void OnSyntaxChanged(object? sender, EventArgs e)
    {
        KeptColours = syntax.KeptColours;
        ChangedWithin = syntax.ChangedWithin;
        Changed?.Invoke(this, EventArgs.Empty);
        KeptColours = false;
        ChangedWithin = null;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => OnTextChanged(e);
}
