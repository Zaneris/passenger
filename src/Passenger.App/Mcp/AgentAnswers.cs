using System.Globalization;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Mcp;

/// <summary>What an agent's answer holds of what can run long, so it stays within what an agent's client takes: a debugged
/// value's first few thousand characters, a list's first entries, and the unversioned files of a folder git shows whole
/// gathered into that folder's one entry. Each cut says what it left out and how to read it. Pure.</summary>
public static class AgentAnswers
{
    /// <summary>How many characters of a value a debug answer shows.</summary>
    public const int ValueLength = 4000;

    /// <summary>How many entries a list of files or changes holds.</summary>
    public const int MostFiles = 200;

    /// <summary>How many files a sentence names.</summary>
    public const int MostNamed = 100;

    /// <summary>A value as a debug answer shows it: whole when short, else its first characters and "…", with words saying
    /// its length and the debug_evaluate that reads on, of <paramref name="expression"/> or, where none is given, of the
    /// value. A string's literal is cut between its escapes, its length and places the string's own.</summary>
    public static (string Shown, string? Cut) Value(string value, string? expression)
    {
        if (value.Length <= ValueLength)
        {
            return (value, null);
        }
        if (Literal(value, ValueLength - 1) is { } literal)
        {
            return (value[..literal.ShownEnd] + "…", Cut(literal.Length, literal.Shown, string.Empty));
        }
        int end = ValueLength - 1;
        if (char.IsLowSurrogate(value[end]) && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }
        return (value[..end] + "…", Cut(value.Length, end, ".ToString()"));

        // The part it offers to read next is a string, whose literal's quotes take two of what an answer shows.
        string Cut(int length, int shown, string text) => string.Create(CultureInfo.InvariantCulture,
            $"{length:N0} characters, the first {shown:N0} shown: debug_evaluate {Read(expression)}{text}.Substring({shown}, {Math.Min(ValueLength - 2, length - shown)}) reads on");
    }

    /// <summary>An expression as written before a member it is given: as it is where it names a variable or a member of one,
    /// else in parentheses; "of its " where there is none.</summary>
    private static string Read(string? expression) => expression is not { Length: > 0 } named ? "of its "
        : named.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '@' or '$' or '[' or ']') && !char.IsDigit(named[0]) ? named
        : $"({named})";

    /// <summary>A C# string literal's length as the string it writes, and how much of it is shown within
    /// <paramref name="most"/> characters: where in the literal the last whole character so shown ends (never inside an
    /// escape or between a pair's surrogates) and how many of the string's characters that is. Null where the value is no
    /// literal.</summary>
    private static (int Length, int ShownEnd, int Shown)? Literal(string value, int most)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return null;
        }
        int last = value.Length - 1;
        (int at, int length, int shownEnd, int shown) = (1, 0, 1, 0);
        while (at < last)
        {
            (int width, int count) = value[at] == '\\' ? Escape(value.AsSpan(at, last - at))
                : char.IsHighSurrogate(value[at]) && at + 1 < last && char.IsLowSurrogate(value[at + 1]) ? (2, 2)
                : (1, 1);
            if (width == 0)
            {
                return null;
            }
            at += width;
            length += count;
            if (at <= most)
            {
                (shownEnd, shown) = (at, length);
            }
        }
        return (length, shownEnd, shown);

        // An escape's width in the literal and how many characters it writes; no width where it is cut short.
        static (int Width, int Count) Escape(ReadOnlySpan<char> escape) => escape.Length < 2 ? (0, 0) : escape[1] switch
        {
            'u' => escape.Length >= 6 ? (6, 1) : (0, 0),
            'U' => escape.Length >= 10 && uint.TryParse(escape[2..10], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint point)
                ? (10, point > 0xFFFF ? 2 : 1) : (0, 0),
            'x' => (2 + Hex(escape[2..]), 1),
            _ => (2, 1),
        };

        static int Hex(ReadOnlySpan<char> digits)
        {
            int count = 0;
            while (count < 4 && count < digits.Length && char.IsAsciiHexDigit(digits[count]))
            {
                count++;
            }
            return count;
        }
    }

    /// <summary>A list's first <paramref name="most"/> entries, and words saying how many more it left out and how to read
    /// them (<paramref name="how"/>), null when it left none.</summary>
    public static (IReadOnlyList<T> Kept, string? More) Listed<T>(IReadOnlyList<T> items, int most, string noun, string how) =>
        items.Count <= most ? (items, null) : ([.. items.Take(most)], More(items.Count - most, noun, how));

    /// <summary>Names in a sentence, at most <see cref="MostNamed"/>, then how many more were left out and how to read them.</summary>
    public static string Named(IReadOnlyList<string> names, string noun, string how)
    {
        (IReadOnlyList<string> kept, string? more) = Listed(names, MostNamed, noun, how);
        return more is null ? string.Join(", ", kept) : $"{string.Join(", ", kept)}; {more}";
    }

    /// <summary>Words for what a list left out: "1,200 more files left out: …".</summary>
    public static string More(int left, string noun, string how) =>
        string.Create(CultureInfo.InvariantCulture, $"{left:N0} more {Plural.Noun(left, noun)} left out: {how}");

    /// <summary>The changes in <paramref name="scope"/> (the repository's root for every one), each unversioned file in a
    /// folder git shows whole (<paramref name="untracked"/>, as <see cref="Git.UntrackedFoldersAsync"/> lists them) gathered
    /// into one entry: that folder where it lies inside the scope, else, the scope being the folder or inside it, the
    /// scope's folder the file is in, as git's status of the scope shows it. A rename is the scope's where either of its
    /// paths is.</summary>
    public static IReadOnlyList<ListedChange> Gathered(IReadOnlyList<FileChange> changes, IReadOnlyCollection<string> untracked, string scope)
    {
        HashSet<string> whole = new(untracked, PathNames.Comparer);
        List<ListedChange> listed = [];
        Dictionary<string, int> folders = new(PathNames.Comparer);
        foreach (FileChange change in changes)
        {
            if (!PathNames.Within(change.Path, scope) && (change.From is not { } from || !PathNames.Within(from, scope)))
            {
                continue;
            }
            if ((change.Kind == ChangeKind.Unversioned && whole.Count > 0 ? FolderOf(change.Path, whole, scope) : null) is not { } folder)
            {
                listed.Add(new ListedChange(change, null));
            }
            else if (folders.TryGetValue(folder, out int at))
            {
                listed[at] = listed[at] with { Files = listed[at].Files + 1 };
            }
            else
            {
                folders[folder] = listed.Count;
                listed.Add(new ListedChange(new FileChange(folder, ChangeKind.Unversioned), 1));
            }
        }
        return listed;
    }

    /// <summary>The folder an unversioned file is gathered into within a scope: the folder git shows whole that holds it,
    /// where it lies inside the scope, else the scope's folder the file is in; null where it is shown alone.</summary>
    private static string? FolderOf(string path, HashSet<string> whole, string scope)
    {
        for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (!whole.Contains(folder))
            {
                continue;
            }
            if (PathNames.IsUnder(folder, scope))
            {
                return folder;
            }
            ReadOnlySpan<char> below = PathNames.Below(path, scope);
            int first = below.IndexOfAny(PathNames.Separators);
            return first < 0 ? null : Path.Join(scope.AsSpan().TrimEnd(PathNames.Separators), below[..first]);
        }
        return null;
    }
}

/// <summary>A change as vcs_status lists it: a file's, or a folder's that git shows whole, with how many unversioned files
/// it holds (null for a file).</summary>
public sealed record ListedChange(FileChange Change, int? Files);
