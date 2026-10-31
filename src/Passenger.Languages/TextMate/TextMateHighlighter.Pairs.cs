using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages.TextMate;

/// <summary>Brackets and tags in pairs, read from the colours of the text as it stands once a pass has caught up with it:
/// a bracket of the code (plain text, not in a string or a comment) pairs with the one that closes it, and an XML or HTML
/// tag's name with its start or end tag's.</summary>
public sealed partial class TextMateHighlighter
{
    /// <summary>How far Move Caret to Matching Brace's search for a pair goes, in lines from where it began, before it gives up.</summary>
    private const int FarthestLines = 50_000;

    /// <summary>How far the pair of the bracket the caret touches, or of the tag it is in, is looked for, in lines from where
    /// the search began, as every caret move asks for it: about a millisecond's reading, where a whole file's would be a lag
    /// at each key.</summary>
    private const int NearLines = 2_000;

    /// <summary>A line's characters, copied here to be read without a string each.</summary>
    private char[] lineChars = new char[256];

    /// <summary>Which line of which text <see cref="lineChars"/> holds, so the tag search reading one line for its bracket,
    /// its name and its end copies it once.</summary>
    private (TextSnapshot? Text, int Line, int Start, int Length) copied;

    /// <summary>How many lines the searches for a bracket's or a tag's pair have read: what a caret move costs.</summary>
    public long LinesPaired { get; private set; }

    public BracePair? PairAt(TextSnapshot text, int offset, bool far = false)
    {
        if (!Settled(text) || offset < 0 || offset >= text.Length)
        {
            return null;
        }
        int which = "([{)]}".IndexOf(text[offset], StringComparison.Ordinal);
        if (which < 0 || KindAt(text, offset) != TokenKind.Text)
        {
            return null;
        }
        bool opener = which < 3;
        char open = "([{"[which % 3];
        char close = ")]}"[which % 3];
        int step = opener ? 1 : -1;
        int depth = 0;
        int line = text.GetLineFromOffset(offset);
        int reach = far ? FarthestLines : NearLines;
        for (int at = line; at >= 0 && at < table!.Count && Math.Abs(at - line) < reach; at += step)
        {
            ReadOnlySpan<char> chars = LineChars(text, at, out int lineStart);
            SyntaxSpan[] spans = table[at].Spans;
            for (int column = at == line ? offset - lineStart : opener ? 0 : chars.Length - 1; column >= 0 && column < chars.Length; column += step)
            {
                char c = chars[column];
                if ((c == open || c == close) && KindIn(spans, column) == TokenKind.Text)
                {
                    depth += (c == open) == opener ? 1 : -1;
                    if (depth == 0)
                    {
                        return new BracePair(new TextRange(offset, 1), new TextRange(lineStart + column, 1));
                    }
                }
            }
        }
        return null;
    }

    public BracePair? TagPairAt(TextSnapshot text, int caret, bool far = false)
    {
        if (grammar.Tags == Tags.None || !Settled(text) || caret < 0 || caret > text.Length)
        {
            return null;
        }
        int line = text.GetLineFromOffset(caret);
        ReadOnlySpan<char> chars = LineChars(text, line, out int lineStart);
        int column = caret - lineStart;
        int start = column;
        while (start > 0 && MarkupNames.IsNameChar(chars[start - 1]))
        {
            start--;
        }
        int end = column;
        while (end < chars.Length && MarkupNames.IsNameChar(chars[end]))
        {
            end++;
        }
        bool closing;
        if (start == end || !MarkupNames.IsNameStart(chars[start]))
        {
            return null;
        }
        else if (start >= 1 && chars[start - 1] == '<')
        {
            closing = false;
        }
        else if (start >= 2 && chars[start - 1] == '/' && chars[start - 2] == '<')
        {
            closing = true;
        }
        else
        {
            return null;
        }
        string name = chars[start..end].ToString();
        int bracket = lineStart + start - (closing ? 2 : 1);
        if (KindAt(text, bracket) != TokenKind.Tag)
        {
            return null;
        }
        TextRange touched = new(lineStart + start, end - start);
        int reach = far ? FarthestLines : NearLines;
        TextRange? matching = closing ? StartTagOf(text, bracket, line, name, reach) : EndTagOf(text, lineStart + end, line, name, reach);
        return matching is { } found ? new BracePair(touched, found) : null;
    }

    /// <summary>The name of the end tag that closes the start tag on <paramref name="line"/> whose name ends at
    /// <paramref name="afterName"/>, looked for no more than <paramref name="reach"/> lines below that line.</summary>
    private TextRange? EndTagOf(TextSnapshot text, int afterName, int line, string name, int reach)
    {
        int farthest = line + reach;
        if (TagEnd(text, afterName, line, farthest) is not { } own || own.SelfClosing || Void(name))
        {
            return null;
        }
        int depth = 1;
        (int from, int fromLine) = (own.At + 1, own.Line);
        while (Find(text, from, fromLine, forward: true, '<', farthest) is { } at)
        {
            if (TagAt(text, at.At, at.Line) is not { } tag)
            {
                (from, fromLine) = (at.At + 1, at.Line);
                continue;
            }
            if (tag.Closing)
            {
                if (--depth == 0)
                {
                    return SameName(NameOf(text, tag), name) ? new TextRange(tag.Start, tag.Length) : null;
                }
                (from, fromLine) = (tag.Start, tag.Line);
                continue;
            }
            if (TagEnd(text, tag.Start + tag.Length, tag.Line, farthest) is not { } end)
            {
                return null;
            }
            if (!end.SelfClosing && !Void(text, tag))
            {
                depth++;
            }
            (from, fromLine) = (end.At + 1, end.Line);
        }
        return null;
    }

    /// <summary>The name of the start tag the end tag at <paramref name="bracket"/>, on <paramref name="line"/>, closes,
    /// looked for no more than <paramref name="reach"/> lines above that line.</summary>
    private TextRange? StartTagOf(TextSnapshot text, int bracket, int line, string name, int reach)
    {
        int farthest = line - reach;
        int depth = 1;
        (int from, int fromLine) = (bracket - 1, line);
        while (from >= 0 && Find(text, from, fromLine, forward: false, '<', farthest) is { } at)
        {
            (from, fromLine) = (at.At - 1, at.Line);
            if (TagAt(text, at.At, at.Line) is not { } tag)
            {
                continue;
            }
            if (tag.Closing)
            {
                depth++;
                continue;
            }
            // A start tag before the end tag ends before it too.
            if (TagEnd(text, tag.Start + tag.Length, tag.Line, line) is not { } end || end.SelfClosing || Void(text, tag))
            {
                continue;
            }
            if (--depth == 0)
            {
                return SameName(NameOf(text, tag), name) ? new TextRange(tag.Start, tag.Length) : null;
            }
        }
        return null;
    }

    /// <summary>The start or end tag whose bracket is at <paramref name="at"/>, on <paramref name="line"/>: where its name
    /// starts and how long it is; null for what else begins with one (a comment, a declaration, a processing instruction).</summary>
    private (int Start, int Length, int Line, bool Closing)? TagAt(TextSnapshot text, int at, int line)
    {
        ReadOnlySpan<char> chars = LineChars(text, line, out int lineStart);
        int column = at - lineStart + 1;
        bool closing = column < chars.Length && chars[column] == '/';
        int start = closing ? column + 1 : column;
        int end = start;
        while (end < chars.Length && MarkupNames.IsNameChar(chars[end]))
        {
            end++;
        }
        return end > start && MarkupNames.IsNameStart(chars[start]) ? (lineStart + start, end - start, line, closing) : null;
    }

    /// <summary>A tag's name, read where it stands.</summary>
    private ReadOnlySpan<char> NameOf(TextSnapshot text, (int Start, int Length, int Line, bool Closing) tag)
    {
        ReadOnlySpan<char> chars = LineChars(text, tag.Line, out int lineStart);
        return chars.Slice(tag.Start - lineStart, tag.Length);
    }

    /// <summary>The bracket that ends the tag whose name ends at <paramref name="from"/>, on <paramref name="line"/>, past
    /// its attributes' values, up to line <paramref name="farthest"/>.</summary>
    private (int At, int Line, bool SelfClosing)? TagEnd(TextSnapshot text, int from, int line, int farthest)
    {
        if (Find(text, from, line, forward: true, '>', farthest) is not { } found)
        {
            return null;
        }
        ReadOnlySpan<char> chars = LineChars(text, found.Line, out int lineStart);
        return (found.At, found.Line, found.At > lineStart && chars[found.At - lineStart - 1] == '/');
    }

    /// <summary>The nearest <paramref name="target"/> of the tag's colour from <paramref name="from"/> on (or back), and
    /// its line, up to line <paramref name="farthest"/>: every search for one tag's pair is held to the lines near where it
    /// began. <paramref name="line"/> is the line <paramref name="from"/> is on, or the one it is just past the end (or
    /// before the start) of, so no offset is turned into its line again.</summary>
    private (int At, int Line)? Find(TextSnapshot text, int from, int line, bool forward, char target, int farthest)
    {
        LineTable lines = table!;
        int step = forward ? 1 : -1;
        int last = forward ? Math.Min(farthest, lines.Count - 1) : Math.Max(farthest, 0);
        for (int at = line; forward ? at <= last : at >= last; at += step)
        {
            ReadOnlySpan<char> chars = LineChars(text, at, out int lineStart);
            SyntaxSpan[] spans = lines[at].Spans;
            int column = at == line ? Math.Min(from - lineStart, chars.Length - (forward ? 0 : 1)) : forward ? 0 : chars.Length - 1;
            while (column >= 0 && column < chars.Length)
            {
                int found = forward ? chars[column..].IndexOf(target) : chars[..(column + 1)].LastIndexOf(target);
                if (found < 0)
                {
                    break;
                }
                column = forward ? column + found : found;
                if (KindIn(spans, column) == TokenKind.Tag)
                {
                    return (lineStart + column, at);
                }
                column += step;
            }
        }
        return null;
    }

    private bool Void(ReadOnlySpan<char> name) => grammar.Tags == Tags.Html && MarkupNames.IsVoid(name);

    private bool Void(TextSnapshot text, (int Start, int Length, int Line, bool Closing) tag) => grammar.Tags == Tags.Html && MarkupNames.IsVoid(NameOf(text, tag));

    private bool SameName(ReadOnlySpan<char> a, string b) => a.Equals(b, grammar.Tags == Tags.Html ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Whether the colours are those of <paramref name="text"/>, every line's read from the line above's.</summary>
    private bool Settled(TextSnapshot text) => table is { Complete: true } && since.Count == 0 && ReferenceEquals(tableText, text);

    private TokenKind KindAt(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(offset);
        return KindIn(table![line].Spans, offset - text.GetLineStart(line));
    }

    /// <summary>The kind of the character at a column of a line, from its spans (sorted, plain text left out).</summary>
    private static TokenKind KindIn(SyntaxSpan[] spans, int column)
    {
        int at = Sorted.FirstAfter(spans, static s => s.End, column);
        return at < spans.Length && spans[at].Start <= column ? spans[at].Kind : TokenKind.Text;
    }

    private ReadOnlySpan<char> LineChars(TextSnapshot text, int line, out int lineStart)
    {
        if (!ReferenceEquals(copied.Text, text) || copied.Line != line)
        {
            LinesPaired++;
            LineSpan span = text.GetLine(line);
            if (lineChars.Length < span.Length)
            {
                lineChars = new char[Math.Max(span.Length, lineChars.Length * 2)];
            }
            text.CopyTo(span.Start, lineChars.AsSpan(0, span.Length));
            copied = (text, line, span.Start, span.Length);
        }
        lineStart = copied.Start;
        return lineChars.AsSpan(0, copied.Length);
    }
}
