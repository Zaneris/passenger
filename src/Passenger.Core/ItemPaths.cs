using System.Text;

namespace Passenger.Core;

/// <summary>A path as a project file says it, so that a folder of any legal name reads back as that folder: MSBuild takes
/// '%', ';', '$', '@', '*' and '?' for more than themselves unless written %XX. A glob differs: an Include, Remove or Update
/// unescapes one twice, and one escaped wildcard makes all of it a plain name fitting no file, so a '*' or '?' of its folder
/// stays a wildcard, finding that folder and any other it fits. Pure.</summary>
public static class ItemPaths
{
    /// <summary>What a glob's folder escapes: all but the wildcards, which it can't.</summary>
    private const string InGlob = "%;$@";

    /// <summary>A file or folder as an item's value or an Import names it.</summary>
    public static string Named(string path) => Escaped(path, InGlob + "*?", twice: false);

    /// <summary>A value as it goes between a condition's quotes, which a single quote would close.</summary>
    public static string InCondition(string value) => value.Replace("'", "%27", StringComparison.Ordinal);

    /// <summary>All that lies under a folder, as the glob of an Include, Remove or Update names it.</summary>
    public static string Under(string folder) => $"{Escaped(folder, InGlob, twice: true)}/**";

    /// <summary>A value with each %XX read as its character, as MSBuild reads it; a '%' of no such pair stays.</summary>
    public static string Unescaped(string written)
    {
        if (!written.Contains('%'))
        {
            return written;
        }
        StringBuilder read = new(written.Length);
        for (int at = 0; at < written.Length; at++)
        {
            if (written[at] == '%' && at + 2 < written.Length && char.IsAsciiHexDigit(written[at + 1]) && char.IsAsciiHexDigit(written[at + 2]))
            {
                read.Append((char)Convert.ToInt32(written.Substring(at + 1, 2), 16));
                at += 2;
            }
            else
            {
                read.Append(written[at]);
            }
        }
        return read.ToString();
    }

    /// <summary>The one path a value leads with (a glob's by its folder); null for a list or anything MSBuild expands. An
    /// Exclude and an Import read a glob only <paramref name="once"/>.</summary>
    public static ItemPath? Read(string written, bool once = false)
    {
        if (written.Contains(';') || Expands(written))
        {
            return null;
        }
        int wild = written.IndexOfAny(['*', '?']);
        bool glob = wild >= 0 && !EscapesWildcard(written);
        string head = (glob ? written[..Math.Max(0, written.LastIndexOfAny(['\\', '/'], wild))] : written).TrimEnd('\\', '/');
        bool twice = glob && !once;
        return new ItemPath(twice ? Unescaped(Unescaped(head)) : Unescaped(head), written[head.Length..], glob, twice);
    }

    internal static string Escaped(string path, string special, bool twice)
    {
        StringBuilder written = new(path.Length);
        foreach (char c in path)
        {
            if (special.Contains(c))
            {
                written.Append(c == '%' && twice ? "%2525" : $"%{(int)c:X2}");
            }
            else
            {
                written.Append(c);
            }
        }
        return written.ToString();
    }

    internal static string FolderOfGlob(string folder, bool twice) => Escaped(folder, InGlob, twice);

    private static bool Expands(string written)
    {
        for (int at = 0; at + 1 < written.Length; at++)
        {
            if (written[at] is '$' or '@' or '%' && written[at + 1] == '(')
            {
                return true;
            }
        }
        return false;
    }

    private static bool EscapesWildcard(string written) =>
        written.Contains("%2A", StringComparison.OrdinalIgnoreCase) || written.Contains("%3F", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a value of a project file says (<see cref="ItemPaths.Read"/>): its path, unescaped; the rest of it as
/// written, a glob's wildcards or a folder's last slash; and how a new path is written in its place.</summary>
public sealed record ItemPath(string Path, string Rest, bool Glob, bool Twice)
{
    /// <summary>All the value says as MSBuild reads it, a glob's wildcards kept.</summary>
    public string Whole => Path + (Twice ? ItemPaths.Unescaped(ItemPaths.Unescaped(Rest)) : ItemPaths.Unescaped(Rest));

    /// <summary>The value naming another path, as this one is written.</summary>
    public string At(string path) => (Glob ? ItemPaths.FolderOfGlob(path, Twice) : ItemPaths.Named(path)) + Rest;
}
