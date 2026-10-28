using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace Passenger.Text;

/// <summary>What a search looks for: the text, and its three toggles.</summary>
public readonly record struct SearchQuery(string Text, bool MatchCase = false, bool WholeWords = false, bool Regex = false)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>Finding every match of a query in a snapshot, and the replacement each one takes. A regex that does not
/// parse finds nothing and says why instead of throwing, and one that runs past its time stops and says so. The text is
/// searched with each "\r\n" read as "\n", as the editor's lines are, so `$` finds a CRLF line's end and `\s`
/// never takes half of its break; `^` is every line's start but the empty end after the last break.</summary>
public static class TextSearch
{
    /// <summary>The most matches a search reports, so a one-letter query in a huge file stays quick;
    /// the count says "1000+" past it.</summary>
    public const int MaxMatches = 1000;

    /// <summary>How long a regex's search of a text may run on the UI thread, its matches together, so the window never
    /// waits on one long: a search that stops there is run again off the UI thread under <see cref="Apart"/>.</summary>
    public static readonly TimeSpan AtOnce = TimeSpan.FromMilliseconds(250);

    /// <summary>The variable that gives <see cref="Apart"/> in milliseconds.</summary>
    [IdentifierHome]
    public const string ApartVariable = "PASSENGER_REGEX_TIMEOUT_MS";

    /// <summary>How long a regex's search of a text may run off the UI thread, its matches together: far past the few
    /// hundred milliseconds a busy machine keeps a quick search waiting, so only a regex backtracking without end
    /// (`(a+)+b` over a long run of a's) stops. 5 s, or what <see cref="ApartVariable"/> gives; a value there that is no
    /// whole number of milliseconds above 0 is fatal.</summary>
    public static TimeSpan Apart => Environment.GetEnvironmentVariable(ApartVariable) is { Length: > 0 } given
        ? int.TryParse(given, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : throw new FormatException($"{ApartVariable} takes a whole number of milliseconds above 0, not '{given}'")
        : TimeSpan.FromSeconds(5);

    /// <summary>Every match of a query, a regex's search stopping past <paramref name="within"/> (<see cref="AtOnce"/> on
    /// the UI thread, <see cref="Apart"/> off it).</summary>
    public static SearchResult FindAll(TextSnapshot snapshot, SearchQuery query, TimeSpan within, int limit = MaxMatches)
    {
        if (query.IsEmpty)
        {
            return SearchResult.None;
        }
        SearchPattern pattern;
        try
        {
            pattern = SearchPattern.For(query, within);
        }
        catch (ArgumentException error)
        {
            return new SearchResult([], false, error.Message);
        }
        return FindAll(snapshot, pattern, limit);
    }

    /// <summary>Every match of a pattern read once for a search of many texts: plain text a block at a time where it lies,
    /// a regex over the text read as lines once while the snapshot stands (<see cref="Lines"/>).</summary>
    public static SearchResult FindAll(TextSnapshot snapshot, SearchPattern pattern, int limit = MaxMatches)
    {
        bool crlf = snapshot.LineCount > 1 && snapshot.GetLine(0).BreakLength == 2;
        if (pattern.Literal)
        {
            return Blocks(snapshot, pattern, limit, crlf) with { TextLength = snapshot.Length };
        }
        Lines lines = Lines.Of(snapshot);
        try
        {
            return Found(lines.Text, lines.Returns, pattern, limit, pattern.Groups ? new FoundText(pattern, snapshot, null) : null, crlf) with { TextLength = snapshot.Length };
        }
        finally
        {
            Lines.Keep(lines);
        }
    }

    /// <summary>Every match of a pattern in a text no document holds (a closed file's, read once), searched where it lies
    /// but by a regex in a text of "\r\n" breaks, which reads it as lines.</summary>
    public static SearchResult FindAll(string text, SearchPattern pattern, int limit = MaxMatches)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        bool crlf = newline > 0 && text[newline - 1] == '\r';
        FoundText? found = pattern.Groups ? new FoundText(pattern, null, text) : null;
        if (pattern.Literal || !text.Contains("\r\n", StringComparison.Ordinal))
        {
            return Found(text, [], pattern, limit, found, crlf) with { TextLength = text.Length };
        }
        Lines lines = Lines.Of(text);
        try
        {
            return Found(lines.Text, lines.Returns, pattern, limit, found, crlf) with { TextLength = text.Length };
        }
        finally
        {
            Lines.Keep(lines);
        }
    }

    /// <summary>A plain query's matches, each as long as its text, found a block at a time in a window from the shared pool
    /// that holds a character either side of the block's (all Words' lookarounds read) and its last match's end: one a
    /// block's end cuts, or one the window's end took for a word's, is found again whole in the next.</summary>
    private static SearchResult Blocks(TextSnapshot snapshot, SearchPattern pattern, int limit, bool crlf)
    {
        int length = snapshot.Length;
        int needle = pattern.Length;
        List<TextRange> matches = [];
        char[] window = ArrayPool<char>.Shared.Rent(Math.Min(length, Math.Max(1 << 14, 2 * (needle + 1))));
        // As much as the window holds beside a match cut at its end and the character after it.
        int block = Math.Max(1, window.Length - needle - 1);
        try
        {
            int next = 0;
            for (int start = 0; start < length; start += block)
            {
                int end = Math.Min(start + block, length);
                if (next >= end)
                {
                    continue;
                }
                int from = Math.Max(start - 1, 0);
                Span<char> read = window.AsSpan(0, Math.Min(end + needle, length) - from);
                snapshot.CopyTo(from, read);
                foreach (ValueMatch match in pattern.Regex.EnumerateMatches(read, Math.Max(start, next) - from))
                {
                    int at = from + match.Index;
                    if (at >= end)
                    {
                        break;
                    }
                    if (matches.Count == limit)
                    {
                        return new SearchResult(matches, true, null) { Crlf = crlf };
                    }
                    matches.Add(new TextRange(at, match.Length));
                    next = at + match.Length;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(window);
        }
        return new SearchResult(matches, false, null) { Crlf = crlf };
    }

    /// <summary>Every match of a pattern in a text read as lines, each placed in the text itself past the '\r's
    /// <paramref name="returns"/> says were left out before it; a regex's search stopping past its time.</summary>
    private static SearchResult Found(ReadOnlySpan<char> text, ReadOnlySpan<int> returns, SearchPattern pattern, int limit, FoundText? found, bool crlf)
    {
        List<TextRange> matches = [];
        int passed = 0;
        long started = Stopwatch.GetTimestamp();
        try
        {
            // An empty match counts (`^` is a line's start), and the next is looked for past it.
            foreach (ValueMatch match in pattern.Regex.EnumerateMatches(text))
            {
                if (matches.Count == limit)
                {
                    return new SearchResult(matches, true, null) { Found = found, Crlf = crlf };
                }
                int start = Original(match.Index, returns, ref passed);
                matches.Add(TextRange.FromBounds(start, Original(match.Index + match.Length, returns, ref passed)));
                // The regex gives each match its time, and the matches together have no more.
                if (pattern.Within is { } within && Stopwatch.GetElapsedTime(started) > within)
                {
                    return SearchResult.TimedOut(matches, found, crlf);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return SearchResult.TimedOut(matches, found, crlf);
        }
        return new SearchResult(matches, false, null) { Found = found, Crlf = crlf };
    }

    /// <summary>An offset of the text read as lines in the text itself: past every '\r' left out before it, so a match
    /// that ends at a line's end ends before its whole break and one that takes the break takes both halves. Offsets come
    /// in order, <paramref name="passed"/> counting the '\r's before the last.</summary>
    private static int Original(int offset, ReadOnlySpan<int> returns, ref int passed)
    {
        while (passed < returns.Length && returns[passed] < offset)
        {
            passed++;
        }
        return offset + passed;
    }

    /// <summary>A text read as lines, each "\r\n" as "\n", into arrays from the shared pool, with where each '\r' left
    /// out was in offsets of the text read. One is kept for the next search, which reads a snapshot again only once it is
    /// another, so a regex typed over a file copies it once; a search takes it for itself until it gives it back.</summary>
    internal sealed class Lines
    {
        private static Lines? kept;
        private readonly WeakReference<TextSnapshot?> read = new(null);
        private char[] chars = [];
        private int[] returns = [];
        private int length;
        private int count;

        public ReadOnlySpan<char> Text => chars.AsSpan(0, length);

        /// <summary>Where each '\r' left out was, as offsets of <see cref="Text"/>: where its '\n' stands.</summary>
        public ReadOnlySpan<int> Returns => returns.AsSpan(0, count);

        public static Lines Of(TextSnapshot snapshot)
        {
            Lines lines = Interlocked.Exchange(ref kept, null) ?? new Lines();
            if (!lines.read.TryGetTarget(out TextSnapshot? was) || !ReferenceEquals(was, snapshot))
            {
                lines.Hold(snapshot.Length);
                snapshot.CopyTo(0, lines.chars.AsSpan(0, snapshot.Length));
                lines.Fold(snapshot.Length, snapshot.LineCount - 1);
                lines.read.SetTarget(snapshot);
            }
            return lines;
        }

        public static Lines Of(string text)
        {
            Lines lines = Interlocked.Exchange(ref kept, null) ?? new Lines();
            lines.read.SetTarget(null);
            lines.Hold(text.Length);
            text.CopyTo(lines.chars);
            lines.Fold(text.Length, text.AsSpan().Count('\n'));
            return lines;
        }

        /// <summary>Gives the lines back for the next search; the arrays of any given back meanwhile go back to the pool.</summary>
        public static void Keep(Lines lines) => Interlocked.Exchange(ref kept, lines)?.Release();

        private void Hold(int size)
        {
            if (chars.Length < size)
            {
                Give(chars);
                chars = ArrayPool<char>.Shared.Rent(size);
            }
        }

        /// <summary>Moves the text after each "\r\n"'s '\r' back over it, a run between two breaks at a time.</summary>
        private void Fold(int size, int breaks)
        {
            Span<char> text = chars.AsSpan(0, size);
            count = 0;
            int at = text.IndexOf("\r\n");
            if (at < 0)
            {
                length = size;
                return;
            }
            if (returns.Length < breaks)
            {
                Give(returns);
                returns = ArrayPool<int>.Shared.Rent(breaks);
            }
            int write = at;
            while (at >= 0)
            {
                returns[count++] = write;
                int next = text[(at + 2)..].IndexOf("\r\n");
                int end = next < 0 ? size : at + 2 + next;
                text[(at + 1)..end].CopyTo(text[write..]);
                write += end - at - 1;
                at = next < 0 ? -1 : end;
            }
            length = write;
        }

        private void Release()
        {
            Give(chars);
            Give(returns);
            chars = [];
            returns = [];
            read.SetTarget(null);
        }

        private static void Give<T>(T[] array)
        {
            if (array.Length > 0)
            {
                ArrayPool<T>.Shared.Return(array);
            }
        }
    }

    /// <summary>The regex a query finds with; a pattern that does not parse throws an <see cref="ArgumentException"/> whose
    /// message says why. Plain text takes no time limit: a literal between one-character lookarounds never backtracks.</summary>
    internal static Regex Build(SearchQuery query, TimeSpan within)
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
        return new Regex(pattern, options, query.Regex ? within : Regex.InfiniteMatchTimeout);
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

/// <summary>A query read once for a search of many texts (a folder's files, a table's cells): its regex, and how long a
/// regex's search of each text may run. The patterns of the last few queries are kept, so a query searched again, as each
/// key typed in a file searches it, is read once.</summary>
public sealed class SearchPattern
{
    private static readonly Lock gate = new();
    private static readonly SearchPattern?[] recent = new SearchPattern?[4];

    private readonly SearchQuery query;
    private readonly TimeSpan made;
    private Regex? untimed;

    private SearchPattern(SearchQuery query, TimeSpan within)
    {
        Regex = TextSearch.Build(query, within);
        Groups = query.Regex;
        Within = query.Regex ? within : null;
        Literal = !query.Regex && query.Text.AsSpan().IndexOfAny('\r', '\n') < 0;
        Length = query.Text.Length;
        Walks = query.Regex && Regex.ToString().Contains(@"\G", StringComparison.Ordinal);
        this.query = query;
        made = within;
    }

    internal Regex Regex { get; }

    /// <summary>Whether a match fills a replacement's groups: the query is a regex.</summary>
    internal bool Groups { get; }

    /// <summary>How long a search of one text may run, its matches together; null for plain text, which has no limit.</summary>
    internal TimeSpan? Within { get; }

    /// <summary>Whether each match is the query's text in a line, as long as it (<see cref="Length"/>): plain text without
    /// a line break, which reads the same in a text's "\r\n" lines as in its "\n" ones.</summary>
    internal bool Literal { get; }

    /// <summary>The query's length, a <see cref="Literal"/> match's.</summary>
    internal int Length { get; }

    /// <summary>Whether a match is found again only by walking the matches before it: the regex has \G, which holds where
    /// the last match ended.</summary>
    internal bool Walks { get; }

    /// <summary>The regex with no time limit, finding again matches it found within it.</summary>
    internal Regex Untimed
    {
        get
        {
            Regex? regex = untimed;
            if (regex is null)
            {
                regex = new Regex(Regex.ToString(), Regex.Options, Regex.InfiniteMatchTimeout);
                regex = Interlocked.CompareExchange(ref untimed, regex, null) ?? regex;
            }
            return regex;
        }
    }

    /// <summary>The pattern a query finds with, a regex's search of a text stopping past <paramref name="within"/>; a
    /// regex that does not parse throws an <see cref="ArgumentException"/> whose message says why.</summary>
    public static SearchPattern For(SearchQuery query, TimeSpan within)
    {
        lock (gate)
        {
            for (int i = 0; i < recent.Length; i++)
            {
                if (recent[i] is { } kept && kept.query == query && kept.made == within)
                {
                    Recent(kept, i);
                    return kept;
                }
            }
        }
        SearchPattern read = new(query, within);
        lock (gate)
        {
            Recent(read, recent.Length - 1);
        }
        return read;
    }

    /// <summary>Puts a pattern first among those kept, those before <paramref name="at"/> moving down one.</summary>
    private static void Recent(SearchPattern pattern, int at)
    {
        for (int i = at; i > 0; i--)
        {
            recent[i] = recent[i - 1];
        }
        recent[0] = pattern;
    }

    /// <summary>Whether a text holds a match; a regex running past its time throws a <see cref="RegexMatchTimeoutException"/>.</summary>
    public bool IsMatch(string text) => Regex.IsMatch(text);
}

/// <summary>The text a regex's matches were found in, for the groups a replacement fills in: read as lines into a string,
/// and each match found again where it stood, its lookarounds, anchors and \b as they held, once a replacement asks for
/// one. Read on one thread at a time.</summary>
internal sealed class FoundText(SearchPattern pattern, TextSnapshot? snapshot, string? text)
{
    private string? lines;

    /// <summary>Where each '\r' left out of <see cref="lines"/> was in the text itself, in order.</summary>
    private int[] returns = [];

    /// <summary>Where each '\n' of <see cref="lines"/> after no '\r' is, in order, once a replacement asks.</summary>
    private int[]? lone;

    /// <summary>Every match in order up to the last asked for, for a pattern that <see cref="SearchPattern.Walks"/>.</summary>
    private List<Match>? walked;

    public Regex Regex => pattern.Regex;

    /// <summary>The text read as lines, each "\r\n" as "\n": what a match's groups and a replacement's $_ are of.</summary>
    public string Lines => lines ??= Read();

    /// <summary>An offset of the text itself in <see cref="Lines"/>: back past each '\r' left out before it.</summary>
    public int InLines(int offset)
    {
        _ = Lines;
        int before = Array.BinarySearch(returns, offset);
        return offset - (before >= 0 ? before : ~before);
    }

    /// <summary>How many '\n's of <see cref="Lines"/> from <paramref name="from"/> to <paramref name="to"/> stand after
    /// no '\r'.</summary>
    public int Lone(int from, int to)
    {
        int[] at = lone ??= Lones(Lines);
        int first = Array.BinarySearch(at, from);
        int last = Array.BinarySearch(at, to);
        return (last >= 0 ? last : ~last) - (first >= 0 ? first : ~first);
    }

    /// <summary>Match <paramref name="index"/>, found at <paramref name="found"/> in the text itself.</summary>
    public Match Match(int index, TextRange found)
    {
        string read = Lines;
        if (pattern.Walks)
        {
            walked ??= [];
            while (walked.Count <= index)
            {
                walked.Add(walked.Count == 0 ? pattern.Untimed.Match(read) : walked[^1].NextMatch());
            }
            return walked[index];
        }
        return pattern.Untimed.Match(read, InLines(found.Start));
    }

    private string Read()
    {
        if (text is not null && !text.Contains("\r\n", StringComparison.Ordinal))
        {
            return text;
        }
        TextSearch.Lines read = text is { } whole ? TextSearch.Lines.Of(whole) : TextSearch.Lines.Of(snapshot ?? TextSnapshot.Empty);
        try
        {
            ReadOnlySpan<int> left = read.Returns;
            returns = new int[left.Length];
            for (int i = 0; i < left.Length; i++)
            {
                returns[i] = left[i] + i;
            }
            return new string(read.Text);
        }
        finally
        {
            TextSearch.Lines.Keep(read);
        }
    }

    private static int[] Lones(string text)
    {
        List<int> found = [];
        for (int at = text.IndexOf('\n', StringComparison.Ordinal); at >= 0; at = text.IndexOf('\n', at + 1))
        {
            if (at == 0 || text[at - 1] != '\r')
            {
                found.Add(at);
            }
        }
        return [.. found];
    }
}

/// <summary>A regex's replacement read as <see cref="Match.Result"/> reads it, and what it makes of each match: runs of
/// its own characters, and what each '$' stands for, a group ($1, ${name}, $&amp; the match, $+ the last), the text
/// before or after the match ($`, $') or the whole text ($_), each a slice of the text read as lines, so a match's
/// replacement is measured before it is made and made in one copy. In a text of "\r\n" breaks, each '\n' it makes
/// after no '\r' is written as "\r\n", the file's own.</summary>
internal sealed class ReplacementText
{
    private static readonly Regex WordCharacter = new(@"[\w\u200C\u200D]");

    private readonly FoundText found;
    private readonly Part[] parts;
    private readonly bool crlf;

    /// <summary>Whether a part takes a group a match must be found again for: any but the match itself.</summary>
    private readonly bool groups;

    /// <summary>Whether a part takes from the text: any but the replacement's own characters.</summary>
    private readonly bool sliced;

    /// <summary>What every match is replaced with alike, for a replacement that takes nothing from the text.</summary>
    private readonly string? alike;

    public ReplacementText(FoundText found, string replacement, bool crlf)
    {
        // .NET reads it first, so one it refuses (a group's number past int.MaxValue) throws as Match.Result does.
        _ = found.Regex.Replace(string.Empty, replacement, 0);
        this.found = found;
        this.crlf = crlf;
        parts = Read(replacement, found.Regex);
        groups = parts.Any(p => p is { Kind: Kind.Group, Group: > 0 });
        sliced = parts.Any(p => p.Kind != Kind.Own);
        alike = sliced ? null : Make(0, default);
    }

    private enum Kind
    {
        Own,
        Group,
        Before,
        After,
        Whole,
    }

    /// <summary>A run of the replacement's own characters (<see cref="Own"/>), or what a '$' stands for.</summary>
    private readonly record struct Part(Kind Kind, int Group, string Own);

    /// <summary>How long match <paramref name="index"/>'s replacement is, found at <paramref name="match"/> in the text
    /// itself, its slices counted, never copied.</summary>
    public long Length(int index, TextRange match)
    {
        if (alike is not null)
        {
            return alike.Length;
        }
        (Match? again, int start, int end) = Place(index, match);
        return Measure(again, start, end);
    }

    /// <summary>Match <paramref name="index"/>'s replacement, found at <paramref name="match"/> in the text itself.</summary>
    public string Make(int index, TextRange match)
    {
        if (alike is not null)
        {
            return alike;
        }
        (Match? again, int start, int end) = Place(index, match);
        return string.Create(checked((int)Measure(again, start, end)), (this, again, start, end),
            static (into, made) => made.Item1.Write(into, made.again, made.start, made.end));
    }

    /// <summary>A match found again for its groups, when a part takes one, and where it stands in the lines.</summary>
    private (Match? Again, int Start, int End) Place(int index, TextRange match) => !sliced ? (null, 0, 0)
        : (groups ? found.Match(index, match) : null, found.InLines(match.Start), found.InLines(match.End));

    private long Measure(Match? again, int start, int end)
    {
        long length = 0;
        char last = '\0';
        foreach (Part part in parts)
        {
            ReadOnlySpan<char> piece = Piece(part, again, start, end, out int at);
            if (piece.IsEmpty)
            {
                continue;
            }
            length += piece.Length;
            if (crlf)
            {
                length += at < 0 ? Lone(piece, last) : (piece[0] == '\n' && last != '\r' ? 1 : 0) + found.Lone(at + 1, at + piece.Length);
                last = piece[^1];
            }
        }
        return length;
    }

    private void Write(Span<char> into, Match? again, int start, int end)
    {
        int written = 0;
        char last = '\0';
        foreach (Part part in parts)
        {
            ReadOnlySpan<char> piece = Piece(part, again, start, end, out _);
            if (piece.IsEmpty)
            {
                continue;
            }
            int at = 0;
            while (crlf && piece[at..].IndexOf('\n') is var next and >= 0)
            {
                piece.Slice(at, next).CopyTo(into[written..]);
                written += next;
                if ((at + next > 0 ? piece[at + next - 1] : last) != '\r')
                {
                    into[written++] = '\r';
                }
                into[written++] = '\n';
                at += next + 1;
            }
            piece[at..].CopyTo(into[written..]);
            written += piece.Length - at;
            last = piece[^1];
        }
    }

    /// <summary>What a part makes of a match from <paramref name="start"/> to <paramref name="end"/> of the lines, and
    /// where in them it stands (-1 for the replacement's own characters).</summary>
    private ReadOnlySpan<char> Piece(Part part, Match? again, int start, int end, out int at)
    {
        switch (part.Kind)
        {
            case Kind.Own:
                at = -1;
                return part.Own;
            case Kind.Whole:
                at = 0;
                return found.Lines;
            case Kind.Before:
                at = 0;
                return found.Lines.AsSpan(0, start);
            case Kind.After:
                at = end;
                return found.Lines.AsSpan(end);
            case Kind.Group when part.Group == 0:
                at = start;
                return found.Lines.AsSpan(start, end - start);
            default:
                Group group = again!.Groups[part.Group];
                at = group.Index;
                return group.Success ? group.ValueSpan : [];
        }
    }

    /// <summary>How many '\n's of the replacement's own characters stand after no '\r', <paramref name="last"/> the
    /// character before them.</summary>
    private static int Lone(ReadOnlySpan<char> own, char last)
    {
        int count = 0;
        for (int i = 0; i < own.Length; i++)
        {
            if (own[i] == '\n' && (i > 0 ? own[i - 1] : last) != '\r')
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>The replacement's parts, each '$' read as .NET reads it: a group by a number or name the regex has
    /// (${...} braced), $$, $&amp;, $`, $', $+ or $_, else the '$' is its own character.</summary>
    private static Part[] Read(string replacement, Regex regex)
    {
        int[] numbers = regex.GetGroupNumbers();
        List<Part> parts = [];
        StringBuilder own = new();
        int i = 0;
        while (i < replacement.Length)
        {
            char c = replacement[i++];
            if (c != '$')
            {
                own.Append(c);
                continue;
            }
            (Part? part, int after) = Dollar(replacement, i, regex, numbers);
            if (part is not { } read)
            {
                own.Append('$');
                // $$ is one '$'; a '$' that stands for nothing is its own, and what follows it is read on.
                i = after;
                continue;
            }
            if (own.Length > 0)
            {
                parts.Add(new Part(Kind.Own, 0, own.ToString()));
                own.Clear();
            }
            parts.Add(read);
            i = after;
        }
        if (own.Length > 0)
        {
            parts.Add(new Part(Kind.Own, 0, own.ToString()));
        }
        return [.. parts];
    }

    /// <summary>What the '$' before <paramref name="at"/> stands for and where the replacement goes on: null for its own
    /// character, going on at <paramref name="at"/> (past the second '$' of $$).</summary>
    private static (Part? Part, int After) Dollar(string replacement, int at, Regex regex, int[] numbers)
    {
        if (at == replacement.Length)
        {
            return (null, at);
        }
        int i = at;
        char c = replacement[i];
        bool braced = c == '{' && i + 1 < replacement.Length;
        if (braced)
        {
            c = replacement[++i];
        }
        if (char.IsAsciiDigit(c))
        {
            int number = 0;
            while (i < replacement.Length && char.IsAsciiDigit(replacement[i]))
            {
                number = checked((number * 10) + (replacement[i++] - '0'));
            }
            if ((!braced || (i < replacement.Length && replacement[i++] == '}')) && numbers.Contains(number))
            {
                return (new Part(Kind.Group, number, string.Empty), i);
            }
            return (null, at);
        }
        if (braced)
        {
            int start = i;
            while (i < replacement.Length && WordCharacter.IsMatch(replacement.AsSpan(i, 1)))
            {
                i++;
            }
            string name = replacement[start..i];
            if (name.Length > 0 && i < replacement.Length && replacement[i++] == '}' && regex.GroupNumberFromName(name) is var number and >= 0)
            {
                return (new Part(Kind.Group, number, string.Empty), i);
            }
            return (null, at);
        }
        Part? read = c switch
        {
            '&' => new Part(Kind.Group, 0, string.Empty),
            '`' => new Part(Kind.Before, 0, string.Empty),
            '\'' => new Part(Kind.After, 0, string.Empty),
            '+' => new Part(Kind.Group, numbers.Max(), string.Empty),
            '_' => new Part(Kind.Whole, 0, string.Empty),
            _ => null,
        };
        return (read, read is null ? (c == '$' ? at + 1 : at) : at + 1);
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

    /// <summary>What a regex's search that ran past its time says, its matches those found before it stopped.</summary>
    public const string TookTooLong = "The search took too long and stopped.";

    /// <summary>Whether a regex ran past its time and the search stopped, its matches only those found before.</summary>
    public bool Stopped { get; private init; }

    internal static SearchResult TimedOut(List<TextRange> matches, FoundText? found, bool crlf) =>
        new(matches, true, TookTooLong) { Found = found, Crlf = crlf, Stopped = true };

    /// <summary>Where a regex's matches were found, each found again there for its groups; null for plain text, which is
    /// replaced as typed.</summary>
    internal FoundText? Found { get; init; }

    /// <summary>Whether the text's breaks are "\r\n", as a break a group carries over is then written.</summary>
    internal bool Crlf { get; init; }

    /// <summary>The length of the text searched.</summary>
    public int TextLength { get; init; }

    /// <summary>The longest text, in characters, that replacing every match makes of a shorter one: past it a replacement
    /// is refused before any of it is made (a regex's $_ is the whole text again for each match).</summary>
    public const long MostReplaced = 100_000_000;

    /// <summary>What match <paramref name="index"/> is replaced with: the replacement as typed, or for a regex with its
    /// groups ($1, ${name}) filled in from that match where it stood, its lookarounds, anchors and \b as they held.</summary>
    public string Replacement(int index, string replacement) =>
        Found is null ? replacement : new ReplacementText(Found, replacement, Crlf).Make(index, Matches[index]);

    /// <summary>How long the text searched would be with every match replaced, worked out from the matches and what the
    /// replacement takes of the text for each, none of it made.</summary>
    public long ReplacedLength(string replacement)
    {
        long length = TextLength;
        ReplacementText? replacing = Found is null ? null : new(Found, replacement, Crlf);
        for (int i = 0; i < Matches.Count; i++)
        {
            TextRange match = Matches[i];
            length += (replacing?.Length(i, match) ?? replacement.Length) - match.Length;
        }
        return length;
    }

    /// <summary>Whether replacing every match would make the text longer than <see cref="MostReplaced"/> and than it
    /// was (<see cref="ReplacedLength"/>).</summary>
    public bool TooLong(long replaced) => replaced > Math.Max(MostReplaced, TextLength);

    /// <summary>Every match's replacement, as edits of the text searched.</summary>
    public IReadOnlyList<TextEdit> Replacements(string replacement)
    {
        ReplacementText? replacing = Found is null ? null : new(Found, replacement, Crlf);
        TextEdit[] edits = new TextEdit[Matches.Count];
        for (int i = 0; i < edits.Length; i++)
        {
            TextRange match = Matches[i];
            edits[i] = new TextEdit(match.Start, match.Length, replacing?.Make(i, match) ?? replacement);
        }
        return edits;
    }

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
