namespace Passenger.Core;

/// <summary>SemVer 2's precedence of versions, NuGet's packages' and the .NET SDKs' alike: by their numbers (1.0 is 1.0.0.0), a prerelease before
/// its release, and prerelease labels dot by dot, numbers by value and below words, words as NuGet compares them,
/// ignoring case. Build metadata after a + takes no part.</summary>
public sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    public static bool IsPrerelease(string version) => Label(version) is not null;

    /// <summary>A version's prerelease label (rc.2 of 9.0.0-rc.2+abc), null for a release.</summary>
    public static string? Label(string version) => Release(version.Trim()).Split('-', 2) is [_, { } label] ? label : null;

    /// <summary>A version's first number (9 of 9.0.1 and 9.0.0-rc.2); null for a version that starts with none.</summary>
    public static int? Major(string version) =>
        int.TryParse(Release(version.Trim()).Split('-', 2)[0].Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
            ? major : null;

    /// <summary>Whether two versions share their first two numbers, as a patch release shares its minor version's (0.4.1
    /// and 0.4.0).</summary>
    public static bool SameMinor(string x, string y) => Parse(x).Numbers is var a && Parse(y).Numbers is var b && a[0] == b[0] && a[1] == b[1];

    /// <summary>The folder of these whose name is the newest version (<see cref="NewestFirst"/>); null for none.</summary>
    public static string? Newest(IEnumerable<string> folders) => NewestFirst(folders).FirstOrDefault();

    /// <summary>The folders named by versions (a runtime's, a pack's, a tool's), newest first: a release above its release
    /// candidates and previews, which share its numbers. A name beginning with no digit is no version and left out; two
    /// names of one version (1.0, 1.0.0) go by their names, ordinal, so the order they are listed in never decides.</summary>
    public static IReadOnlyList<string> NewestFirst(IEnumerable<string> folders) =>
        [.. folders.Select(f => (Folder: f, Name: Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))))
            .Where(f => f.Name.Length > 0 && char.IsAsciiDigit(f.Name[0]))
            .OrderByDescending(f => f.Name, Instance)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.Folder)];

    public int Compare(string? x, string? y)
    {
        (long[] a, string[]? aLabel) = Parse(x ?? string.Empty);
        (long[] b, string[]? bLabel) = Parse(y ?? string.Empty);
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }
        if (aLabel is null || bLabel is null)
        {
            // A release is above its prereleases.
            return (aLabel is null).CompareTo(bLabel is null);
        }
        for (int i = 0; i < Math.Min(aLabel.Length, bLabel.Length); i++)
        {
            int order = CompareIdentifiers(aLabel[i], bLabel[i]);
            if (order != 0)
            {
                return order;
            }
        }
        return aLabel.Length.CompareTo(bLabel.Length);
    }

    private static int CompareIdentifiers(string a, string b)
    {
        bool aNumber = a.Length > 0 && a.All(char.IsAsciiDigit);
        bool bNumber = b.Length > 0 && b.All(char.IsAsciiDigit);
        if (aNumber && bNumber)
        {
            // Digits of any length, compared by value: the longer number without leading zeros is the larger.
            string aDigits = a.TrimStart('0');
            string bDigits = b.TrimStart('0');
            return aDigits.Length != bDigits.Length ? aDigits.Length.CompareTo(bDigits.Length) : string.CompareOrdinal(aDigits, bDigits);
        }
        if (aNumber != bNumber)
        {
            return aNumber ? -1 : 1;
        }
        return StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }

    private static string Release(string version) => version.Split('+', 2)[0];

    /// <summary>A version's four numbers, those it leaves out 0, and its prerelease label's identifiers, null for a release.</summary>
    private static (long[] Numbers, string[]? Label) Parse(string version)
    {
        string[] parts = Release(version.Trim()).Split('-', 2);
        long[] numbers = new long[4];
        string[] given = parts[0].Split('.');
        for (int i = 0; i < Math.Min(given.Length, numbers.Length); i++)
        {
            numbers[i] = long.TryParse(given[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long number) ? number : 0;
        }
        return (numbers, parts.Length > 1 ? parts[1].Split('.') : null);
    }
}
