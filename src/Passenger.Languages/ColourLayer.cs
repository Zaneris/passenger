using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages;

/// <summary>What a colour layer shows as its text stands: the colours last given it and each change of the text since, in
/// turn, which it carries them through to draw. A copy, for any thread.</summary>
public sealed record ShownColours(SyntaxSpan[] Colours, IReadOnlyList<IReadOnlyList<TextEdit>> Since)
{
    public static readonly ShownColours None = new([], []);
}

/// <summary>Colours of what names are (a member, a local, a class), read elsewhere for a version of the text and given
/// here (<see cref="Set"/>), over a grammar's colours (<see cref="CarriedColours"/>). A set that says what is already
/// shown, carried, raises nothing, and one that differs says where, so only the rows it reached are drawn again and a key
/// costs what it changed.</summary>
public sealed class ColourLayer : CarriedColours
{
    private readonly TextDocument document;

    private SyntaxSpan[] spans = [];
    private int version = -1;

    /// <summary>The colours last answered for each range of the text drawn, with the grammar's they were made from and how
    /// many sets of colours had been given: a range asked again (a frame's rows kept, each checked again) is answered
    /// with no new list while neither has changed. Of one text at a time.</summary>
    private readonly Dictionary<(int Start, int Length), (IReadOnlyList<SyntaxSpan> Parsed, int Given, IReadOnlyList<SyntaxSpan> Merged)> answered = [];
    private TextSnapshot? answeredFor;
    private int given;

    public ColourLayer(IHighlighter syntax, TextDocument document)
        : base(syntax, document)
    {
        this.document = document;
    }

    /// <summary>What the layer shows now, for colours read for this text to be compared with where they are read.</summary>
    public ShownColours Shown => new(spans, ChangesSince(version));

    /// <summary>Colours read for a version of the text, in order of their starts, and where they differ from what was shown
    /// of that version (<see cref="Differing"/>, worked out where they were read; null, the same); ignored where of an older
    /// version than those shown. Raises <see cref="CarriedColours.Changed"/> only where they differ, saying
    /// where (<see cref="CarriedColours.ChangedWithin"/>) where nothing has been typed since they were read.</summary>
    public void Set(SyntaxSpan[] colours, int at, TextRange? differ)
    {
        if (at < version || at > document.Version)
        {
            return;
        }
        spans = colours;
        version = at;
        given++;
        Forget(at);
        if (differ is { } range)
        {
            Raise(Carrying == 0 ? range : null);
        }
    }

    /// <summary>Where what a layer showed of a text differs from colours read for that text: from the first that differs to
    /// the last; null where none does. Pure, for any thread.</summary>
    public static TextRange? Differing(ShownColours shown, SyntaxSpan[] after)
    {
        List<SyntaxSpan> carried = [.. shown.Colours];
        foreach (IReadOnlyList<TextEdit> edits in shown.Since)
        {
            CarriedSpans.Forward(carried, edits, Word);
        }
        int low = 0;
        while (low < carried.Count && low < after.Length && carried[low] == after[low])
        {
            low++;
        }
        if (low == carried.Count && low == after.Length)
        {
            return null;
        }
        int high = carried.Count - 1;
        int highAfter = after.Length - 1;
        while (high >= low && highAfter >= low && carried[high] == after[highAfter])
        {
            high--;
            highAfter--;
        }
        int start = Math.Min(low < carried.Count ? carried[low].Start : int.MaxValue, low < after.Length ? after[low].Start : int.MaxValue);
        int end = Math.Max(high >= low ? carried[high].End : 0, highAfter >= low ? after[highAfter].End : 0);
        return TextRange.FromBounds(start, Math.Max(start, end));
    }

    public override IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot snapshot, int start, int length)
    {
        IReadOnlyList<SyntaxSpan> parsed = Syntax.GetSpans(snapshot, start, length);
        if (spans.Length == 0)
        {
            return parsed;
        }
        if (!ReferenceEquals(answeredFor, snapshot))
        {
            answered.Clear();
            answeredFor = snapshot;
        }
        if (answered.TryGetValue((start, length), out (IReadOnlyList<SyntaxSpan> Parsed, int Given, IReadOnlyList<SyntaxSpan> Merged) kept)
            && kept.Given == given && Alike(kept.Parsed, parsed))
        {
            return kept.Merged;
        }
        IReadOnlyList<SyntaxSpan> merged = Over(parsed, Carried(spans, version, start, start + length));
        answered[(start, length)] = (parsed, given, merged);
        return merged;
    }

    protected override bool Holds(int version) => version == this.version;

    /// <summary>Carries nothing until colours are given.</summary>
    protected override void OnTextChanged(DocumentChangedEventArgs e)
    {
        if (spans.Length > 0)
        {
            base.OnTextChanged(e);
        }
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
}
