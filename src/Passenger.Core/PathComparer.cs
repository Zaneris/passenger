namespace Passenger.Core;

/// <summary>Paths compared as the folders holding their names tell letter case: two paths alike but for case name one place
/// where each folder holding a name that differs ignores case, and two where one tells case apart (a case-sensitive APFS
/// volume, a Windows folder made case-sensitive). A folder is asked only of a name differing in case alone, and once, so
/// paths that differ otherwise, or not at all, cost what comparing their characters does and make nothing. Hashed
/// ignoring case, which every answer agrees with.</summary>
public sealed class PathComparer : StringComparer
{
    /// <summary>How many folders' answers are kept; one more forgets them all, so however long a session, few are.</summary>
    private const int Kept = 1024;

    private readonly Func<string, bool?> ignoresCase;

    /// <summary>The folders asked and their answers, replaced whole and never changed, so read without a lock.</summary>
    private (string Folder, bool Ignores)[] answers = [];

    /// <param name="ignoresCaseIn">Whether a folder ignores case in its entries' names; null for one that is not there, for
    /// which the nearest folder above it that is answers. Where none is, case is ignored.</param>
    public PathComparer(Func<string, bool?> ignoresCaseIn) => ignoresCase = ignoresCaseIn;

    /// <summary>Whether two paths name one place.</summary>
    public bool Same(ReadOnlySpan<char> path, ReadOnlySpan<char> other) =>
        path.SequenceEqual(other) || path.Equals(other, StringComparison.OrdinalIgnoreCase) && CaseIgnoredAlong(path, other);

    /// <summary>Whether a path lies inside a folder, at any depth, as <see cref="PathNames.IsUnder(string, string)"/> takes
    /// it, the names down to the folder compared as their folders tell case.</summary>
    public bool IsUnder(ReadOnlySpan<char> path, ReadOnlySpan<char> folder) => PathNames.IsUnder(path, folder, this);

    /// <summary>Whether a folder ignores case in its entries' names, as it said when first asked; a relative path, which
    /// names no one folder, ignores it.</summary>
    public bool IgnoresCaseIn(ReadOnlySpan<char> folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (!Path.IsPathFullyQualified(folder))
        {
            return true;
        }
        while (!folder.IsEmpty)
        {
            foreach ((string asked, bool ignores) in Volatile.Read(ref answers))
            {
                if (folder.SequenceEqual(asked))
                {
                    return ignores;
                }
            }
            string named = folder.ToString();
            if (ignoresCase(named) is { } told)
            {
                (string, bool)[] now, next;
                do
                {
                    now = Volatile.Read(ref answers);
                    next = now.Length >= Kept ? [(named, told)] : [.. now, (named, told)];
                }
                while (Interlocked.CompareExchange(ref answers, next, now) != now);
                return told;
            }
            folder = Path.GetDirectoryName(folder);
        }
        return true;
    }

    public override bool Equals(string? x, string? y) => ReferenceEquals(x, y) || x is not null && y is not null && Same(x, y);

    public override int GetHashCode(string obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return string.GetHashCode(obj, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ignoring case, and paths alike but for case that name two places by their characters.</summary>
    public override int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? y is null ? 0 : -1 : 1;
        }
        int order = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : Same(x, y) ? 0 : string.CompareOrdinal(x, y);
    }

    /// <summary>Whether, of two paths alike but for case, every name differing lies in a folder that ignores case; a name
    /// with no folder before it (a drive, a relative path's first) ignores it.</summary>
    private bool CaseIgnoredAlong(ReadOnlySpan<char> path, ReadOnlySpan<char> other)
    {
        ReadOnlySpan<char> separators = PathNames.Separators;
        int name = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (separators.Contains(path[i]))
            {
                name = i + 1;
            }
            else if (path[i] != other[i])
            {
                if (name > 0 && !IgnoresCaseIn(path[..name]))
                {
                    return false;
                }
                int end = path[i..].IndexOfAny(separators);
                if (end < 0)
                {
                    return true;
                }
                i += end;
                name = i + 1;
            }
        }
        return true;
    }
}
