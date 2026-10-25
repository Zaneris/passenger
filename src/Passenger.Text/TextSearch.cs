using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace Passenger.Text;

/// <summary>What a search looks for: the text, and its three toggles.</summary>
public readonly record struct SearchQuery(string Text, bool MatchCase = false, bool WholeWords = false, bool Regex = false)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>Finding every match of a query in a snapshot, and the replacement each one takes. A regex
/// that does not parse, or one that runs too long, finds nothing and says why instead of throwing. The text is
/// searched with each "\r\n" read as "\n", as the editor's lines are, so `$` finds a CRLF line's end and `\s`
/// never takes half of its break; `^` is every line's start but the empty end after the last break.</summary>
public static class TextSearch
{
    /// <summary>The most matches a search reports, so a one-letter query in a huge file stays quick;
    /// the count says "1000+" past it.</summary>
    public const int MaxMatches = 1000;

    private static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(250);

    public static SearchResult FindAll(TextSnapshot snapshot, SearchQuery query, int limit = MaxMatches)
    {
        if (query.IsEmpty)
        {
            return SearchResult.None;
        }
        (string text, List<int> returns) = AsLines(snapshot.ToString());
        Regex? pattern;
        try
        {
            pattern = Build(query);
        }
        catch (ArgumentException error)
        {
            return new SearchResult([], false, error.Message);
        }
        List<TextRange> matches = [];
        // Kept whole, so a replacement's groups come from the match where it stood, its lookarounds and anchors held.
        List<Match>? found = query.Regex ? [] : null;
        bool crlf = snapshot.LineCount > 1 && snapshot.GetLine(0).BreakLength == 2;
        try
        {
            // An empty match counts (`^` is a line's start), and NextMatch steps on past it.
            for (Match match = pattern.Match(text); match.Success; match = match.NextMatch())
            {
                if (matches.Count == limit)
                {
                    return new SearchResult(matches, true, null) { Found = found, Crlf = crlf };
                }
                matches.Add(TextRange.FromBounds(Original(match.Index, returns), Original(match.Index + match.Length, returns)));
                found?.Add(match);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchResult(matches, true, "The search took too long and stopped.") { Found = found, Crlf = crlf };
        }
        return new SearchResult(matches, false, null) { Found = found, Crlf = crlf };
    }

    /// <summary>The text with each "\r\n" read as "\n", and where each '\r' left out was, in offsets of the text read.</summary>
    private static (string Text, List<int> Returns) AsLines(string text)
    {
        List<int> returns = [];
        if (!text.Contains("\r\n", StringComparison.Ordinal))
        {
            return (text, returns);
        }
        StringBuilder read = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                returns.Add(read.Length);
                continue;
            }
            read.Append(text[i]);
        }
        return (read.ToString(), returns);
    }

    /// <summary>An offset of the text read as lines in the text itself: past every '\r' left out before it, so a match
    /// that ends at a line's end ends before its whole break and one that takes the break takes both halves.</summary>
    private static int Original(int offset, List<int> returns)
    {
        int index = returns.BinarySearch(offset);
        return offset + (index >= 0 ? index : ~index);
    }

    /// <summary>The regex a query finds with, for text searched piece by piece (a table's cells); a pattern that does not
    /// parse throws an <see cref="ArgumentException"/> whose message says why.</summary>
    public static Regex Pattern(SearchQuery query) => Build(query);

    private static Regex Build(SearchQuery query)
    {
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        string pattern = query.Regex ? RiderCarets(query.Text) : System.Text.RegularExpressions.Regex.Escape(query.Text);
        if (query.WholeWords)
        {
            // A group of its own, so both ends of an alternation are whole words: cat|dog finds no "catalog" or "hotdog".
            pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        }
        if (query.Regex && pattern != query.Text)
        {
            // Read as typed first, so a problem quotes the person's pattern and a stray ')' cannot close the group above.
            _ = new Regex(query.Text, options);
        }
        return new Regex(pattern, options, RegexLimit);
    }

    /// <summary>The pattern with each `^` anchor read as a line's start, but not the empty end after the text's last
    /// break, so prefixing every line leaves a file's final newline as it was.</summary>
    private static string RiderCarets(string pattern)
    {
        if (!pattern.Contains('^', StringComparison.Ordinal))
        {
            return pattern;
        }
        StringBuilder read = new(pattern.Length + 16);
        int classes = 0;
        bool dash = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                // An escape is the character after it, or the two after \c (\c^ is a control character).
                int length = pattern[i + 1] == 'c' && i + 2 < pattern.Length ? 3 : 2;
                read.Append(pattern, i, length);
                i += length - 1;
                dash = false;
                continue;
            }
            if (c == '[' && (classes == 0 || dash))
            {
                // A class's own ^ negates it and a ] first in it is a literal; "-[" inside one subtracts a class.
                int start = i;
                if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                {
                    i++;
                }
                if (i + 1 < pattern.Length && pattern[i + 1] == ']')
                {
                    i++;
                }
                read.Append(pattern, start, i - start + 1);
                classes++;
            }
            else if (c == ']' && classes > 0)
            {
                read.Append(c);
                classes--;
            }
            else if (c == '^' && classes == 0)
            {
                read.Append(@"(?:^(?!\z)|\A)");
            }
            else
            {
                read.Append(c);
            }
            dash = c == '-' && classes > 0;
        }
        return read.ToString();
    }
}

/// <summary>A look at a file's bytes, before it is read as text, for what every match of a query holds in a row: its
/// text, or a regex's longest run of plain characters (<see cref="Literal"/>), cut at line breaks, which a text's reading
/// changes. Bytes the editor reads as UTF-8 without it hold no match and need no decoding. Matching case, the run's own
/// UTF-8 bytes are looked for; ignoring case, the bytes' characters are, as the query's regex reads them, so a character
/// only that casing takes for a letter (the Kelvin sign for k) still counts. One search's own: it keeps a buffer.</summary>
public sealed class MatchFilter
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private readonly byte[]? run;
    private readonly Regex? ignoringCase;
    private char[] chars = [];

    private MatchFilter(byte[]? run, Regex? ignoringCase)
    {
        this.run = run;
        this.ignoringCase = ignoringCase;
    }

    /// <summary>The filter for a query; null when its matches hold no run of characters to look for.</summary>
    public static MatchFilter? For(SearchQuery query)
    {
        string? held = query.IsEmpty ? null : Longest(query.Regex ? Literal(query.Text) : query.Text);
        if (held is null || held.Contains('\uFFFD', StringComparison.Ordinal))
        {
            // A replacement character stands in a text for bytes that are not UTF-8, which hold no such run.
            return null;
        }
        if (!query.MatchCase)
        {
            return new MatchFilter(null, new Regex(Regex.Escape(held), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Regex.InfiniteMatchTimeout));
        }
        try
        {
            return new MatchFilter(new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(held), null);
        }
        catch (EncoderFallbackException)
        {
            // Half of a surrogate pair is in no UTF-8.
            return null;
        }
    }

    /// <summary>Whether a file of these bytes may hold a match, read as <see cref="FileText.FromBytes"/> reads it with
    /// no encoding chosen: false only for UTF-8 without the run (after a UTF-8 BOM, or with none and every byte UTF-8) and
    /// for binary bytes, which are passed over anyway. A UTF-16 file, or 8-bit text, always may.</summary>
    public bool MayMatch(ReadOnlySpan<byte> file)
    {
        if (file.StartsWith(Utf8Bom))
        {
            return Holds(file[Utf8Bom.Length..], strict: false) ?? true;
        }
        if (FileText.Bom(file) is not null)
        {
            return true;
        }
        return Holds(file, strict: true) ?? !FileText.LooksBinary(file);
    }

    /// <summary>Whether UTF-8 holds the run; null when, <paramref name="strict"/>, not every byte is UTF-8 (else a byte
    /// that is not reads as a replacement character).</summary>
    private bool? Holds(ReadOnlySpan<byte> utf8, bool strict)
    {
        if (run is not null)
        {
            if (utf8.IndexOf(run) >= 0)
            {
                return true;
            }
            return !strict || Utf8.IsValid(utf8) ? false : null;
        }
        if (chars.Length < utf8.Length)
        {
            chars = new char[Math.Max(utf8.Length, chars.Length * 2)];
        }
        // UTF-8 makes no more characters than it has bytes.
        if (Utf8.ToUtf16(utf8, chars, out _, out int written, replaceInvalidSequences: !strict) != OperationStatus.Done)
        {
            return null;
        }
        return ignoringCase!.IsMatch(chars.AsSpan(0, written));
    }

    /// <summary>The longest part of a run between its line breaks, or null for none.</summary>
    private static string? Longest(string? held)
    {
        string? best = null;
        foreach (string part in held?.Split(['\r', '\n']) ?? [])
        {
            if (part.Length > (best?.Length ?? 0))
            {
                best = part;
            }
        }
        return best;
    }

    /// <summary>The longest run of characters that every match of a regex holds in a row, read along its top level: its
    /// plain characters and escaped punctuation, but for one a quantifier can make absent or repeat apart (a +
    /// ends the run after it); a group, a class, an anchor or another escape ends a run. Null when it has none, or reads
    /// as nothing looked into here: an alternation at the top, inline options, a comment, a class taken from a class, an
    /// escape of a character by its code (\x41), a property, a reference.</summary>
    private static string? Literal(string pattern)
    {
        if (pattern.Contains("(?#", StringComparison.Ordinal))
        {
            return null;
        }
        string best = string.Empty;
        StringBuilder held = new();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            switch (c)
            {
                case '|':
                    return null;
                case '(':
                    if (i + 2 < pattern.Length && pattern[i + 1] == '?' && "imnsx-".Contains(pattern[i + 2], StringComparison.Ordinal))
                    {
                        return null;
                    }
                    End();
                    i = AfterGroup(pattern, i);
                    if (i < 0)
                    {
                        return null;
                    }
                    i = AfterQuantifier(pattern, i);
                    continue;
                case '[':
                    End();
                    i = AfterClass(pattern, i);
                    if (i < 0)
                    {
                        return null;
                    }
                    i = AfterQuantifier(pattern, i);
                    continue;
                case '\\' when i + 1 < pattern.Length && !char.IsAscii(pattern[i + 1]):
                    return null;
                case '\\' when i + 1 < pattern.Length && (char.IsAsciiLetterOrDigit(pattern[i + 1]) || pattern[i + 1] == '_'):
                    if (!"dDwWsSbBAzZGtnrfvea".Contains(pattern[i + 1], StringComparison.Ordinal))
                    {
                        return null;
                    }
                    End();
                    i = AfterQuantifier(pattern, i + 2);
                    continue;
                case '\\' when i + 1 < pattern.Length:
                    c = pattern[i + 1];
                    i += 2;
                    break;
                case '\\':
                    return null;
                case '.' or '^' or '$' or '*' or '+' or '?' or '{' or ')' or ']' or '}':
                    End();
                    i = AfterQuantifier(pattern, i + 1);
                    continue;
                default:
                    i++;
                    break;
            }
            // A plain character: absent or apart under *, ? or a count, there under +, which the run ends after.
            int after = AfterQuantifier(pattern, i);
            if (c is '\r' or '\n' || (after > i && pattern[i] != '+'))
            {
                End();
            }
            else
            {
                held.Append(c);
                if (after > i)
                {
                    End();
                }
            }
            i = after;
        }
        End();
        return best.Length > 0 ? best : null;

        void End()
        {
            // Half of a surrogate pair a quantifier took from the other is no part of the run.
            int start = held.Length > 0 && char.IsLowSurrogate(held[0]) ? 1 : 0;
            int length = held.Length - start - (held.Length > start && char.IsHighSurrogate(held[^1]) ? 1 : 0);
            if (length > best.Length)
            {
                best = held.ToString(start, length);
            }
            held.Clear();
        }
    }

    /// <summary>Where a group opening at <paramref name="at"/> ends, past its ')'; -1 when it never does.</summary>
    private static int AfterGroup(string pattern, int at)
    {
        int depth = 0;
        for (int i = at; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '[':
                    i = AfterClass(pattern, i);
                    if (i < 0)
                    {
                        return -1;
                    }
                    i--;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }
                    break;
            }
        }
        return -1;
    }

    /// <summary>Where a class opening at <paramref name="at"/> ends, past its ']' (one first in it is its own); -1 when it
    /// never does or takes a class from itself ("-[").</summary>
    private static int AfterClass(string pattern, int at)
    {
        int i = at + 1;
        if (i < pattern.Length && pattern[i] == '^')
        {
            i++;
        }
        if (i < pattern.Length && pattern[i] == ']')
        {
            i++;
        }
        for (; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '[' when pattern[i - 1] == '-':
                    return -1;
                case ']':
                    return i + 1;
            }
        }
        return -1;
    }

    /// <summary>Past a quantifier at <paramref name="at"/> (*, +, ?, or a count as .NET reads one: {2}, {2,}, {2,5}) and
    /// the ? that makes it lazy; <paramref name="at"/> itself when there is none.</summary>
    private static int AfterQuantifier(string pattern, int at)
    {
        if (at >= pattern.Length)
        {
            return at;
        }
        int i = at;
        if (pattern[i] is '*' or '+' or '?')
        {
            i++;
        }
        else if (pattern[i] == '{')
        {
            int j = Digits(pattern, i + 1);
            if (j == i + 1 || j >= pattern.Length)
            {
                return at;
            }
            if (pattern[j] == ',')
            {
                j = Digits(pattern, j + 1);
            }
            if (j >= pattern.Length || pattern[j] != '}')
            {
                return at;
            }
            i = j + 1;
        }
        else
        {
            return at;
        }
        return i < pattern.Length && pattern[i] == '?' ? i + 1 : i;

        static int Digits(string pattern, int from)
        {
            while (from < pattern.Length && char.IsAsciiDigit(pattern[from]))
            {
                from++;
            }
            return from;
        }
    }
}

/// <summary>Every match (sorted), whether there were more than were kept, and a problem with the
/// query itself (a regex that does not parse) if there was one; and what each match is replaced with.</summary>
public sealed record SearchResult(IReadOnlyList<TextRange> Matches, bool Truncated, string? Problem)
{
    public static readonly SearchResult None = new([], false, null);

    /// <summary>A regex's matches as found in the whole text, one for each of <see cref="Matches"/>; null for plain
    /// text, which is replaced as typed.</summary>
    internal IReadOnlyList<Match>? Found { get; init; }

    /// <summary>Whether the text's breaks are "\r\n", as a break a group carries over is then written.</summary>
    internal bool Crlf { get; init; }

    /// <summary>What match <paramref name="index"/> is replaced with: the replacement as typed, or for a regex with its
    /// groups ($1, ${name}) filled in from that match where it stood, its lookarounds, anchors and \b as they held.</summary>
    public string Replacement(int index, string replacement)
    {
        if (Found is null)
        {
            return replacement;
        }
        string replaced = Found[index].Result(replacement);
        // A break a group carried over is the file's own, as the text it replaces had.
        return Crlf && replaced.Contains('\n', StringComparison.Ordinal)
            ? replaced.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : replaced;
    }

    /// <summary>Every match's replacement, as edits of the text searched.</summary>
    public IReadOnlyList<TextEdit> Replacements(string replacement) =>
        [.. Matches.Select((match, i) => new TextEdit(match.Start, match.Length, Replacement(i, replacement)))];

    /// <summary>The first match at or after <paramref name="offset"/>, wrapping to the first.</summary>
    public int NextFrom(int offset)
    {
        for (int i = 0; i < Matches.Count; i++)
        {
            if (Matches[i].Start >= offset)
            {
                return i;
            }
        }
        return Matches.Count > 0 ? 0 : -1;
    }

    /// <summary>The last match before <paramref name="offset"/>, wrapping to the last.</summary>
    public int PreviousFrom(int offset)
    {
        for (int i = Matches.Count - 1; i >= 0; i--)
        {
            if (Matches[i].Start < offset)
            {
                return i;
            }
        }
        return Matches.Count - 1;
    }
}
