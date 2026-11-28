using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The elements of a list written between brackets, in any language whose highlighter knows its brackets: the
/// parts of the innermost (), [] or {} around a caret that holds two or more, parted by its commas outside strings,
/// comments and the brackets inside it, each without the white space around it. Move Element moves them where the
/// language gives the editor no syntax of its own.</summary>
public static class ListElements
{
    /// <summary>How far before a caret a list's opening bracket is looked for.</summary>
    private const int Reach = 20_000;

    /// <summary>The element at a caret and the one beside it on the left or right, of the innermost list around it that has
    /// several; null where there is none, or the element has none beside it that way.</summary>
    public static (TextRange Moving, TextRange Beside)? Spans(TextSnapshot text, IHighlighter? highlighter, int caret, bool right)
    {
        if (highlighter is not IBraceMatcher matcher)
        {
            return null;
        }
        caret = Math.Clamp(caret, 0, text.Length);
        for (int at = caret - 1; at >= Math.Max(0, caret - Reach); at--)
        {
            char c = text[at];
            if (c is ')' or ']' or '}')
            {
                // A list closed before the caret is passed over whole.
                if (matcher.PairAt(text, at, far: true) is { } closed && closed.Matching.Start < at)
                {
                    at = closed.Matching.Start;
                }
                continue;
            }
            if (c is not ('(' or '[' or '{') || matcher.PairAt(text, at, far: true) is not { } pair || pair.Matching.Start < caret)
            {
                continue;
            }
            (List<TextRange> elements, List<int> commas) = Parts(text, highlighter, matcher, at, pair.Matching.Start);
            if (elements.Count < 2)
            {
                continue;
            }
            int index = commas.Count(comma => comma < caret);
            int other = right ? index + 1 : index - 1;
            return other >= 0 && other < elements.Count && elements[index].Length > 0 && elements[other].Length > 0 ? (elements[index], elements[other]) : null;
        }
        return null;
    }

    /// <summary>The parts of a list between its brackets, each trimmed of white space, and the commas of the code that part
    /// them.</summary>
    private static (List<TextRange> Parts, List<int> Commas) Parts(TextSnapshot text, IHighlighter highlighter, IBraceMatcher matcher, int open, int close)
    {
        IReadOnlyList<SyntaxSpan> spans = highlighter.GetSpans(text, open, close - open);
        List<int> commas = [];
        for (int at = open + 1; at < close; at++)
        {
            char c = text[at];
            if (c is '(' or '[' or '{' && matcher.PairAt(text, at, far: true) is { } inner && inner.Matching.Start > at && inner.Matching.Start < close)
            {
                at = inner.Matching.Start;
            }
            else if (c == ',' && !InWords(spans, at))
            {
                commas.Add(at);
            }
        }
        List<TextRange> parts = [];
        int start = open + 1;
        foreach (int end in (IEnumerable<int>)[.. commas, close])
        {
            int from = start;
            int to = end;
            while (from < to && char.IsWhiteSpace(text[from]))
            {
                from++;
            }
            while (to > from && char.IsWhiteSpace(text[to - 1]))
            {
                to--;
            }
            parts.Add(TextRange.FromBounds(from, to));
            start = end + 1;
        }
        return (parts, commas);
    }

    /// <summary>Whether an offset is inside a string or a comment, where a comma parts nothing.</summary>
    private static bool InWords(IReadOnlyList<SyntaxSpan> spans, int offset) =>
        spans.Any(s => s.Start <= offset && offset < s.End && (s.Kind is TokenKind.String or TokenKind.StringEscape or TokenKind.Comment || TokenKinds.InDocComment(s.Kind)));
}
