namespace Passenger.Core;

/// <summary>SemVer 2's precedence of versions, NuGet's packages' and the .NET SDKs' alike: by their numbers (1.0 is 1.0.0.0), a prerelease before
/// its release, and prerelease labels dot by dot, numbers by value and below words, words as NuGet compares them,
/// ignoring case. Build metadata after a + takes no part.</summary>
public sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    public static bool IsPrerelease(string version) => Release(version).Contains('-', StringComparison.Ordinal);

    /// <summary>A version's first number (9 of 9.0.1 and 9.0.0-rc.2); null for a version that starts with none.</summary>
    public static int? Major(string version) =>
        int.TryParse(Release(version.Trim()).Split('-', 2)[0].Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
            ? major : null;

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
