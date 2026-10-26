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
            return Blocks(snapshot, pattern, limit, crlf);
        }
        Lines lines = Lines.Of(snapshot);
        try
        {
            return Found(lines.Text, lines.Returns, pattern, limit, pattern.Groups ? new FoundText(pattern, snapshot, null) : null, crlf);
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
            return Found(text, [], pattern, limit, found, crlf);
        }
        Lines lines = Lines.Of(text);
        try
        {
            return Found(lines.Text, lines.Returns, pattern, limit, found, crlf);
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

    /// <summary>Every match in order up to the last asked for, for a pattern that <see cref="SearchPattern.Walks"/>.</summary>
    private List<Match>? walked;

    /// <summary>Match <paramref name="index"/>, found at <paramref name="found"/> in the text itself.</summary>
    public Match Match(int index, TextRange found)
    {
        string read = lines ??= Read();
        if (pattern.Walks)
        {
            walked ??= [];
            while (walked.Count <= index)
            {
                walked.Add(walked.Count == 0 ? pattern.Untimed.Match(read) : walked[^1].NextMatch());
            }
            return walked[index];
        }
        int before = Array.BinarySearch(returns, found.Start);
        return pattern.Untimed.Match(read, found.Start - (before >= 0 ? before : ~before));
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

    /// <summary>What match <paramref name="index"/> is replaced with: the replacement as typed, or for a regex with its
    /// groups ($1, ${name}) filled in from that match where it stood, its lookarounds, anchors and \b as they held.</summary>
    public string Replacement(int index, string replacement)
    {
        if (Found is null)
        {
            return replacement;
        }
        // Only a '$' asks for what the match holds; a replacement without one is its own result.
        string replaced = replacement.Contains('$', StringComparison.Ordinal) ? Found.Match(index, Matches[index]).Result(replacement) : replacement;
        // A break a group carried over is the file's own, as the text it replaces had.
        return Crlf && replaced.Contains('\n', StringComparison.Ordinal)
            ? replaced.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : replaced;
    }

    /// <summary>Every match's replacement, as edits of the text searched.</summary>
    public IReadOnlyList<TextEdit> Replacements(string replacement)
    {
        TextEdit[] edits = new TextEdit[Matches.Count];
        for (int i = 0; i < edits.Length; i++)
        {
            TextRange match = Matches[i];
            edits[i] = new TextEdit(match.Start, match.Length, Replacement(i, replacement));
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
