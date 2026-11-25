using Passenger.Core;

namespace Passenger.Packages;

/// <summary>A NuGet version range as a project or an advisory writes one: a bare version (<see cref="Bare"/>, that or
/// later), an interval in brackets, a bracket including its end and a parenthesis leaving it out, an end left empty
/// unbounded ("[1.0, 2.0)", "(, 1.4.1)"), one version alone ("[8.0.0]"), or a floating version ("8.*", "1.0.0-beta.*"),
/// its least where its star stands. Its ends compare in <see cref="VersionOrder"/>.</summary>
public sealed record VersionRange(string? Min, bool MinIncluded, string? Max, bool MaxIncluded)
{
    /// <summary>Whether it is written as a bare version: that version or later.</summary>
    public bool Bare { get; init; }

    /// <summary>Whether it floats: restore takes the newest version where its star stands.</summary>
    public bool Floating { get; init; }

    /// <summary>A floating version as written ("8.*", "1.0.0-beta.*"); null for any other range.</summary>
    public string? Float { get; init; }

    /// <summary>Whether it is one version alone, a pin ("[8.0.0]").</summary>
    public bool IsExact => !Floating && Min is not null && Max is not null && MinIncluded && MaxIncluded && VersionOrder.Instance.Compare(Min, Max) == 0;

    /// <summary>A range in NuGet's notation; null for text that is none (empty, an unclosed bracket, three ends).</summary>
    public static VersionRange? Parse(string text)
    {
        string range = text.Trim();
        if (range.Length == 0)
        {
            return null;
        }
        if (range[0] is '[' or '(')
        {
            if (range.Length < 2 || range[^1] is not (']' or ')') || range[1..^1].Split(',') is not { Length: 1 or 2 } ends)
            {
                return null;
            }
            string from = ends[0].Trim();
            string to = ends[^1].Trim();
            // One version alone is written only "[1.0]".
            if (ends.Length == 1 && (from.Length == 0 || range[0] != '[' || range[^1] != ']'))
            {
                return null;
            }
            return new VersionRange(from.Length > 0 ? from : null, range[0] == '[', to.Length > 0 ? to : null, range[^1] == ']');
        }
        if (range.IndexOf('*', StringComparison.Ordinal) is int star and >= 0)
        {
            string least = range[..star].TrimEnd('.');
            least = least.Length == 0 ? "0" : least.EndsWith('-') ? $"{least}0" : least;
            return new VersionRange(least, true, null, false) { Floating = true, Float = range };
        }
        return new VersionRange(range, true, null, false) { Bare = true };
    }

    /// <summary>Whether restore may resolve it to a version: one in the interval, or for a floating version one its star
    /// lets vary, the numbers before it the same ("8.*" takes 8.1.0, never 9.0.0), and a prerelease only where it floats a
    /// label ("8.*-*") or names its start ("1.0.0-rc.*" takes 1.0.0-rc.2 and 1.0.0).</summary>
    public bool Takes(string version)
    {
        if (Float is not { } written)
        {
            return Contains(version);
        }
        (string release, string? label) = Parts(version);
        (string floatRelease, string? floatLabel) = Parts(written);
        string[] wanted = floatRelease.Split('.');
        string[] has = release.Split('.');
        for (int i = 0; i < Math.Max(wanted.Length, has.Length); i++)
        {
            string want = i < wanted.Length ? wanted[i] : "0";
            string part = i < has.Length ? has[i] : "0";
            if (want.IndexOf('*', StringComparison.Ordinal) is int star and >= 0)
            {
                if (!part.StartsWith(want[..star], StringComparison.Ordinal))
                {
                    return false;
                }
                break;
            }
            if (!(int.TryParse(want, out int a) && int.TryParse(part, out int b) ? a == b : want.Equals(part, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        return label is null || floatLabel is { } floated && label.StartsWith(floated.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A version's release and its prerelease label (null where it has none), its build metadata left out.</summary>
    private static (string Release, string? Label) Parts(string version)
    {
        string plain = version.Split('+')[0].Trim();
        int dash = plain.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (plain, null) : (plain[..dash], plain[(dash + 1)..]);
    }

    /// <summary>Whether a version is in it; a floating one holds every version from its least.</summary>
    public bool Contains(string version)
    {
        int above = Min is null ? 1 : VersionOrder.Instance.Compare(version, Min);
        int below = Max is null ? -1 : VersionOrder.Instance.Compare(version, Max);
        return (above > 0 || (above == 0 && MinIncluded)) && (below < 0 || (below == 0 && MaxIncluded));
    }
}
