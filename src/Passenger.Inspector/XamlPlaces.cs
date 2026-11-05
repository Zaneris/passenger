using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>Which file an app's build recorded elements in is the XAML file the IDE asks of: the same file on the disk,
/// however the build's path reached it. Where the two paths can't be told so (an app built on another machine, whose
/// recorded path names nothing here), the one ending most alike.</summary>
internal static class XamlPlaces
{
    /// <summary>The recorded file that is <paramref name="asked"/>, or null when none is: another file of its name in another
    /// folder never is, while both are on this disk.</summary>
    public static string? FileOf(IEnumerable<string> recorded, string asked)
    {
        string name = Path.GetFileName(asked);
        List<string> named = [.. recorded.Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal)];
        if (named.Count == 0)
        {
            return null;
        }
        string canonical = PathNames.Canonical(asked);
        if (named.FirstOrDefault(f => PathNames.Same(PathNames.Canonical(f), canonical)) is { } same)
        {
            return same;
        }
        bool here = File.Exists(canonical);
        return named.Where(f => !here || !File.Exists(f))
            .OrderByDescending(f => SharedTail(f, asked, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => SharedTail(f, asked, StringComparison.Ordinal))
            .FirstOrDefault();
    }

    /// <summary>How many names two paths end with alike.</summary>
    private static int SharedTail(string a, string b, StringComparison comparison)
    {
        string[] first = a.Split('/', '\\');
        string[] second = b.Split('/', '\\');
        int shared = 0;
        while (shared < first.Length && shared < second.Length
            && string.Equals(first[^(shared + 1)], second[^(shared + 1)], comparison))
        {
            shared++;
        }
        return shared;
    }
}
