using System.Text.RegularExpressions;

namespace Passenger.App.Web;

/// <summary>A semver range as npm reads one (a package.json's <c>engines.node</c>): alternatives between "||", each met when
/// every comparator in it is (an operator and the version after it, with or without a space between), or a hyphen range
/// ("18 - 22", both ends in). A version given in part stands for every version it names, as npm's X-ranges do ("22",
/// "22.x", "&lt;=22" taking 22.9.1). A comparator this cannot read is taken as met, so only a range that says so turns a
/// version away.</summary>
public static partial class NpmRange
{
    /// <summary>Whether a version is in the range.</summary>
    public static bool Meets(Version version, string range) => range.Split("||").Any(alternative => MeetsAll(version, alternative.Trim()));

    private static bool MeetsAll(Version version, string alternative) => Hyphen().Match(alternative) is { Success: true } hyphen
        ? Meets(version, ">=", hyphen.Groups["from"].Value) && Meets(version, "<=", hyphen.Groups["to"].Value)
        : Spaced().Replace(alternative, "${op}").Split(' ', StringSplitOptions.RemoveEmptyEntries).All(part =>
            Comparator().Match(part) is not { Success: true } read || Meets(version, read.Groups["op"].Value, read.Groups["version"].Value));

    private static bool Meets(Version version, string op, string given)
    {
        if (Partial().Match(given) is not { Success: true } read || Number(read.Groups["major"]) is not int major)
        {
            return true;
        }
        int? minor = Number(read.Groups["minor"]);
        int? patch = minor is null ? null : Number(read.Groups["patch"]);
        Version least = new(major, minor ?? 0, patch ?? 0);
        // The versions a part names end before this one: the next major's, or the next minor's.
        Version past = minor is null ? new(major + 1, 0, 0) : new(major, minor.Value + 1, 0);
        return op switch
        {
            ">=" => version >= least,
            ">" => patch is null ? version >= past : version > least,
            "<" => version < least,
            "<=" => patch is null ? version < past : version <= least,
            "~" or "~>" => version >= least && version < past,
            "^" => version >= least && version < Caret(major, minor, patch),
            _ => patch is null ? version >= least && version < past : version == least,
        };
    }

    /// <summary>Where a caret range ends: past the first part that is not zero, or the last one given.</summary>
    private static Version Caret(int major, int? minor, int? patch) =>
        major > 0 || minor is null ? new(major + 1, 0, 0)
        : minor > 0 || patch is null ? new(0, minor.Value + 1, 0)
        : new(0, 0, patch.Value + 1);

    private static int? Number(Group group) => group.Success && int.TryParse(group.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : null;

    [GeneratedRegex(@"^(?<from>\S+)\s+-\s+(?<to>\S+)$")]
    private static partial Regex Hyphen();

    [GeneratedRegex(@"(?<op>>=|<=|>|<|=|\^|~>?)\s+")]
    private static partial Regex Spaced();

    [GeneratedRegex(@"^(?<op>>=|<=|>|<|=|\^|~>?)?(?<version>.+)$")]
    private static partial Regex Comparator();

    /// <summary>A version whole or in part, a part that is x or * standing for any (the parts after it too), its prerelease
    /// and build passed over.</summary>
    [GeneratedRegex(@"^v?(?<major>\d+|[xX*])(?:\.(?<minor>\d+|[xX*])(?:\.(?<patch>\d+|[xX*]))?)?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Partial();
}
