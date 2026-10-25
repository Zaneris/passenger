using System.Buffers;

namespace Passenger.Text;

/// <summary>How well a typed query names a file: the file's own name (exact, then prefix, then CamelHumps initials like
/// "CE" for CodeEditor, then anywhere in it, then its letters close together and in order), or its path when the query
/// has a slash ('/' and '\' alike). Higher is better; null is no match. A name is read where it is in its path and a
/// path's slashes made one on the stack, nothing allocated, since this is asked of every file in the folder for every
/// key; a file's letters, read once (<see cref="PathLetters"/>), pass over one missing a query's with one AND.</summary>
public static class FuzzyMatch
{
    /// <summary>The longest query or path whose slashes are made one on the stack; a longer one takes a pooled buffer.</summary>
    private const int OnStack = 256;

    public static int? Score(string query, string relativePath)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        if (query.Contains('/') || query.Contains('\\'))
        {
            return Slashed(query, relativePath);
        }
        ReadOnlySpan<char> name = Path.GetFileName(relativePath.AsSpan());
        // Each way of matching below finds the query's letters in the name in order: most names fail this one look.
        if (!Within(query, name))
        {
            return null;
        }
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(name);
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase) || stem.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 - relativePath.Length / 8;
        }
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 900 - name.Length - relativePath.Length / 8;
        }
        if (Humps(query, stem))
        {
            return 850 - name.Length - relativePath.Length / 8;
        }
        int at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return 700 - at - name.Length - relativePath.Length / 8;
        }
        // Letters in order only once there are enough to mean something: two found anywhere match nearly every file.
        if (query.Length >= 3 && InOrder(query, name) is { } nameSpread && nameSpread <= name.Length / 2)
        {
            return 500 - nameSpread - name.Length;
        }
        return null;
    }

    /// <summary>The score of <see cref="Score(string, string)"/>, for a file whose letters were read as the folder was
    /// indexed: null at once when its name, or its path for a query with a slash, lacks one of <paramref name="wanted"/>
    /// (the query's own, <see cref="Wanted"/>).</summary>
    public static int? Score(string query, ulong wanted, string relativePath, PathLetters letters)
    {
        ulong held = query.Contains('/') || query.Contains('\\') ? letters.Path : letters.Name;
        return (wanted & ~held) != 0 ? null : Score(query, relativePath);
    }

    /// <summary>The ASCII letters (either case) and digits of a query, a bit each, of which every file it matches holds
    /// each somewhere (<see cref="Letters"/>).</summary>
    public static ulong Wanted(string query)
    {
        ulong bits = 0;
        foreach (char c in query)
        {
            bits |= Bit(c);
        }
        return bits;
    }

    /// <summary>The ASCII letters and digits a text holds, a bit each: its own, and those another of its characters is
    /// taken for as each way of matching ignores case (the Kelvin sign is a k: its lower case, or its upper case, is the
    /// letter), so a file without one of a query's never matches it.</summary>
    public static ulong Letters(ReadOnlySpan<char> text)
    {
        ulong bits = 0;
        foreach (char c in text)
        {
            bits |= char.IsAscii(c) ? Bit(c) : Bit(char.ToLowerInvariant(c)) | Bit(char.ToUpperInvariant(c));
        }
        return bits;
    }

    /// <summary>An ASCII letter's bit, the same in either case, or a digit's; none for any other character.</summary>
    private static ulong Bit(char c) => c switch
    {
        >= 'a' and <= 'z' => 1UL << (c - 'a'),
        >= 'A' and <= 'Z' => 1UL << (c - 'A'),
        >= '0' and <= '9' => 1UL << (26 + c - '0'),
        _ => 0,
    };

    /// <summary>A query with a slash scored along the whole path, each '\' in either read as '/'.</summary>
    private static int? Slashed(string query, string relativePath)
    {
        char[]? rentedQuery = null;
        char[]? rentedPath = null;
        try
        {
            Span<char> asked = query.Length <= OnStack ? stackalloc char[query.Length] : (rentedQuery = ArrayPool<char>.Shared.Rent(query.Length)).AsSpan(0, query.Length);
            query.AsSpan().Replace(asked, '\\', '/');
            scoped ReadOnlySpan<char> path = relativePath;
            if (relativePath.Contains('\\'))
            {
                Span<char> normalized = relativePath.Length <= OnStack ? stackalloc char[relativePath.Length] : (rentedPath = ArrayPool<char>.Shared.Rent(relativePath.Length)).AsSpan(0, relativePath.Length);
                relativePath.AsSpan().Replace(normalized, '\\', '/');
                path = normalized;
            }
            if (path.Contains(asked, StringComparison.OrdinalIgnoreCase))
            {
                return 600 - path.Length;
            }
            return InOrder(asked, path) is { } spread ? 300 - spread - path.Length / 4 : null;
        }
        finally
        {
            if (rentedQuery is not null)
            {
                ArrayPool<char>.Shared.Return(rentedQuery);
            }
            if (rentedPath is not null)
            {
                ArrayPool<char>.Shared.Return(rentedPath);
            }
        }
    }

    /// <summary>How well a typed word names a completion item's label, which is no path ('/', '\' and '.' are its own
    /// characters): a prefix in its own case, then in any case, then CamelHumps from its first word ("SB" for
    /// StringBuilder, "gTOD" for GetTypeOrDefault), then from a later one, then two letters or more anywhere in it;
    /// within each band the shorter label first. Higher is better; null is no match. Nothing is copied, as completion
    /// asks this of every item on every key.</summary>
    public static int? Label(string query, string label)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        if (label.StartsWith(query, StringComparison.Ordinal))
        {
            return 4000 - label.Length;
        }
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3000 - label.Length;
        }
        if (!Within(query, label))
        {
            return null;
        }
        if (Humps(query, label))
        {
            // The humps take the label's first word whenever its first letter is the query's.
            return (char.ToLowerInvariant(label[0]) == char.ToLowerInvariant(query[0]) ? 2000 : 1500) - label.Length;
        }
        if (query.Length >= 2 && label.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 - label.Length;
        }
        return null;
    }

    /// <summary>Whether the query's letters are in the name in order, a letter matching in either case (as each way of
    /// matching above takes it); a query with a surrogate pair, which ignoring case compares whole, always is.</summary>
    private static bool Within(string query, ReadOnlySpan<char> name)
    {
        foreach (char c in query)
        {
            if (char.IsSurrogate(c))
            {
                return true;
            }
        }
        int q = 0;
        for (int i = 0; i < name.Length && q < query.Length; i++)
        {
            char a = name[i];
            char b = query[q];
            bool same = a == b || (char.IsAscii(a) && char.IsAscii(b)
                ? (a | 0x20) == (b | 0x20) && char.IsAsciiLetter(a)
                : char.ToLowerInvariant(a) == char.ToLowerInvariant(b) || char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
            if (same)
            {
                q++;
            }
        }
        return q == query.Length;
    }

    /// <summary>Whether the query's letters are the starts of the name's words in order: "CE" and
    /// "CoEd" for CodeEditor, "fm" for fuzzy_match.</summary>
    private static bool Humps(string query, ReadOnlySpan<char> name)
    {
        int q = 0;
        for (int start = 0; start < name.Length && q < query.Length; start++)
        {
            if (!Starts(name, start))
            {
                continue;
            }
            int i = start;
            if (char.ToLowerInvariant(name[i]) != char.ToLowerInvariant(query[q]))
            {
                continue;
            }
            // A hump may take more than its first letter: "CoEd" is Co + Ed.
            while (q < query.Length && i < name.Length && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(query[q])
                   && (i == start || !Starts(name, i)))
            {
                q++;
                i++;
            }
        }
        return q == query.Length && query.Length > 1;
    }

    /// <summary>Whether a letter or digit starts a word: the first character, one after a separator,
    /// or a capital after a non-capital or before lowercase (the A in IPAddress).</summary>
    private static bool Starts(ReadOnlySpan<char> name, int i) =>
        (i == 0 || char.IsUpper(name[i]) && (!char.IsUpper(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]))
            || name[i - 1] is '_' or '-' or '.' or ' ') && char.IsLetterOrDigit(name[i]);

    /// <summary>How spread out the query's letters are when found in order, or null if they are not.</summary>
    private static int? InOrder(ReadOnlySpan<char> query, ReadOnlySpan<char> text)
    {
        int q = 0;
        int first = -1;
        int last = -1;
        for (int i = 0; i < text.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[q]))
            {
                if (first < 0)
                {
                    first = i;
                }
                last = i;
                q++;
            }
        }
        return q == query.Length ? last - first + 1 - query.Length : null;
    }
}

/// <summary>The letters and digits of a file's name and of its whole path (<see cref="FuzzyMatch.Letters"/>), read once as
/// the folder is indexed.</summary>
public readonly record struct PathLetters(ulong Name, ulong Path)
{
    public static PathLetters Of(string relativePath) =>
        new(FuzzyMatch.Letters(System.IO.Path.GetFileName(relativePath.AsSpan())), FuzzyMatch.Letters(relativePath));
}
