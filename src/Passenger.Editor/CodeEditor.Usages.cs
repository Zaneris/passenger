using Avalonia;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What the caret lights up: the uses of the symbol it rests on, from the host (writes in a colour of their
/// own, each marked on the stripe), and the bracket it touches with its pair, from a highlighter that knows the
/// language's brackets (<see cref="IBraceMatcher"/>). An edit takes the uses away, so none is ever left on text that
/// changed under it, unless they were kept (Highlight Usages in File), which move with the text until Escape.</summary>
public sealed partial class CodeEditor
{
    /// <summary>The pair at the primary caret and what it was found in, found again only when one of those changed.</summary>
    private (TextSnapshot? Text, int Caret, IHighlighter? By, BracePair? Pair) braces;

    /// <summary>The uses of the symbol at the caret, carried with the text while they are kept.</summary>
    private readonly AnchoredList<UsageHighlight> usages = new(u => u.Range, (u, range) => u with { Range = range });

    /// <summary>The uses of the symbol at the caret (sorted), from the host.</summary>
    public IReadOnlyList<UsageHighlight> UsageHighlights => usages.Items;

    /// <summary>Whether the uses shown were asked to stay (Highlight Usages in File): they move with edits rather than go,
    /// until Escape or the host takes them away.</summary>
    public bool UsagesKept { get; private set; }

    public void SetUsageHighlights(IReadOnlyList<UsageHighlight> uses, bool keep = false)
    {
        if (uses.Count == 0 && usages.Count == 0)
        {
            // None for none: the stripe's marks are worked out again only for a change.
            UsagesKept = false;
            return;
        }
        usages.Set(document, [.. uses.OrderBy(u => u.Range.Start)]);
        UsagesKept = keep && uses.Count > 0;
        InvalidateVisual();
    }

    /// <summary>The bracket the primary caret touches and its pair, which are highlighted; null with none.</summary>
    public BracePair? MatchedBraces
    {
        get
        {
            TextSnapshot text = document.Snapshot;
            int caret = selections.Primary.Caret;
            if (!ReferenceEquals(braces.Text, text) || braces.Caret != caret || !ReferenceEquals(braces.By, highlighter))
            {
                braces = (text, caret, highlighter, BracesAt(text, caret));
            }
            return braces.Pair;
        }
    }

    /// <summary>The bracket a caret touches and its pair. Of two brackets it stands between, the one it is outside of
    /// (after a closer, before an opener); the syntax is read only beside a bracket's character. Away from a bracket,
    /// the name of the tag the caret is in and its pair's.</summary>
    private BracePair? BracesAt(TextSnapshot text, int caret)
    {
        if (highlighter is not IBraceMatcher matcher)
        {
            return null;
        }
        foreach ((int at, bool opener) in (ReadOnlySpan<(int, bool)>)[(caret - 1, false), (caret, true), (caret, false), (caret - 1, true)])
        {
            if (at >= 0 && at < text.Length && IsBracket(text[at], opener) && matcher.PairAt(text, at) is { } pair)
            {
                return pair;
            }
        }
        return matcher.TagPairAt(text, caret);
    }

    private static bool IsBracket(char c, bool opener) => opener ? c is '(' or '[' or '{' or '<' : c is ')' or ']' or '}' or '>';

    /// <summary>New colours may come with a pair the highlighter could not tell before (its first parse had not landed):
    /// whether the pair at the caret is another now.</summary>
    private bool BracesChanged()
    {
        BracePair? before = braces.Pair;
        braces = default;
        return MatchedBraces != before;
    }

    /// <summary>The uses go with an edit unless kept, which their ranges carry through it.</summary>
    private void FollowUsages()
    {
        if (usages.Count > 0 && !UsagesKept)
        {
            usages.Clear();
        }
    }

    /// <summary>Move Caret to Matching Brace: each caret at a bracket (the one after it, else the one before) goes
    /// before its pair, so going again comes back; one in a tag's name goes to its pair's name.</summary>
    private void MatchBrace()
    {
        TextSnapshot text = document.Snapshot;
        IBraceMatcher? matcher = highlighter as IBraceMatcher;
        BracePair? PairBeside(int caret) =>
            (caret < text.Length && IsBracket(text[caret]) ? matcher?.PairAt(text, caret, far: true) : null)
            ?? (caret > 0 && IsBracket(text[caret - 1]) ? matcher?.PairAt(text, caret - 1, far: true) : null)
            ?? matcher?.TagPairAt(text, caret, far: true);
        document.BreakUndoGroup();
        SetSelections(selections.With(s => PairBeside(s.Caret) is { } pair ? Selection.At(pair.Matching.Start) : s), reveal: true);
    }

    private static bool IsBracket(char c) => IsBracket(c, opener: true) || IsBracket(c, opener: false);

    /// <summary>The uses on a line, behind its text and its selection: each the whole line high.</summary>
    private void DrawUsages(DrawingContext context, LineView view, Gap[] gaps, double left, double y)
    {
        RowSpan span = view.Span;
        IReadOnlyList<UsageHighlight> uses = UsageHighlights;
        int low = 0;
        int high = uses.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (uses[middle].Range.End <= span.Start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (int i = low; i < uses.Count && uses[i].Range.Start <= span.End; i++)
        {
            FillRange(context, view, gaps, left, y, uses[i].Range, uses[i].IsWrite ? palette.UsageWrite : palette.UsageRead);
        }
    }

    /// <summary>The bracket at the caret and its pair, where either is on this line.</summary>
    private void DrawBraces(DrawingContext context, LineView view, Gap[] gaps, double left, double y, BracePair pair)
    {
        foreach (TextRange bracket in new[] { pair.Touched, pair.Matching })
        {
            if (bracket.Start >= view.Span.Start && bracket.Start < view.Span.End)
            {
                FillRange(context, view, gaps, left, y, bracket, palette.MatchedBrace);
            }
        }
    }

    private void FillRange(DrawingContext context, LineView view, Gap[] gaps, double left, double y, TextRange range, IBrush brush)
    {
        RowSpan span = view.Span;
        int from = Math.Max(range.Start, span.Start) - span.Start;
        int to = Math.Min(range.End, span.End) - span.Start;
        double x1 = left + XWith(view, gaps, from);
        double x2 = left + XWith(view, gaps, to, afterHints: false);
        if (x2 > x1)
        {
            context.FillRectangle(brush, new Rect(x1, y, x2 - x1, metrics.LineHeight));
        }
    }
}
