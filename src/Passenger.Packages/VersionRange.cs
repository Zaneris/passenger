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
            return new VersionRange(least, true, null, false) { Floating = true };
        }
        return new VersionRange(range, true, null, false) { Bare = true };
    }

    /// <summary>Whether a version is in it; a floating one holds every version from its least.</summary>
    public bool Contains(string version)
    {
        int above = Min is null ? 1 : VersionOrder.Instance.Compare(version, Min);
        int below = Max is null ? -1 : VersionOrder.Instance.Compare(version, Max);
        return (above > 0 || (above == 0 && MinIncluded)) && (below < 0 || (below == 0 && MaxIncluded));
    }
}
