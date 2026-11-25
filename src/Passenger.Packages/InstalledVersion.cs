using Passenger.Core;

namespace Passenger.Packages;

/// <summary>What choosing a version does to a project's references of a package: moves one up (an update), only down (a
/// downgrade), one up and another down (a project of several frameworks, every framework's moved together), or nothing,
/// each of its frameworks having that version already.</summary>
public enum PackageMove
{
    None,
    Update,
    Downgrade,
    UpdateAndDowngrade,
}

/// <summary>A package as a project references it for one of its target frameworks (null for a project of one): the
/// version or range the project gives (null where central package management gives none), and, for a range, the version
/// its restore resolved for that framework, once restored.</summary>
public sealed record InstalledVersion(string? Framework, string? Given, string? Resolved = null)
{
    /// <summary>What the project gives, read as a range; null where it gives nothing that reads as one.</summary>
    public VersionRange? Range => Given is null ? null : VersionRange.Parse(Given);

    /// <summary>The version in force, which is shown, compared and audited: restore's, else the range's least (a bare
    /// version's own, a pin's one); null where nothing says (a floating version not restored, no version given).</summary>
    public string? Version => Resolved ?? (Range is { Floating: false } range ? range.Min : null);

    /// <summary>The version in force as a project's line says it, else what the project gives ("8.*").</summary>
    public string? Shown => Version ?? Given;

    /// <summary>Whether the project pins one version alone ("[8.0.0]").</summary>
    public bool IsPinned => Range?.IsExact == true;

    /// <summary>Whether the project gives a range or a floating version, whose version in force its restore says.</summary>
    public bool IsRanged => Range is { Bare: false, IsExact: false };

    /// <summary>Whether a move to <paramref name="version"/> keeps to what the project gives: any version where it gives a
    /// bare one (that or later) or none, else only one its range or floating version takes.</summary>
    public bool Takes(string version) => Range is not { Bare: false } range || range.Takes(version);

    /// <summary>The newest of <paramref name="newestFirst"/> that every framework's reference takes; null where none is.</summary>
    public static string? Newest(IEnumerable<string> newestFirst, IReadOnlyList<InstalledVersion> at) => newestFirst.FirstOrDefault(v => at.All(r => r.Takes(v)));

    /// <summary>What a change of every framework's reference to <paramref name="version"/> is: an update where it raises
    /// a version in force (a floating one's least before it is restored) and lowers none, a downgrade where it lowers one
    /// and raises none, both where it does each, else none.</summary>
    public static PackageMove Move(string version, IReadOnlyList<InstalledVersion> at)
    {
        int[] orders = [.. at.Select(v => Order(version, v)).OfType<int>()];
        return (orders.Any(o => o > 0), orders.Any(o => o < 0)) switch
        {
            (true, true) => PackageMove.UpdateAndDowngrade,
            (true, false) => PackageMove.Update,
            (false, true) => PackageMove.Downgrade,
            _ => PackageMove.None,
        };
    }

    /// <summary>The references a change of every framework's to <paramref name="version"/> moves down, in their order.</summary>
    public static IReadOnlyList<InstalledVersion> Lowered(string version, IReadOnlyList<InstalledVersion> at) => [.. at.Where(v => Order(version, v) < 0)];

    /// <summary>How a version stands to a reference's version in force; null where nothing says.</summary>
    private static int? Order(string version, InstalledVersion at) =>
        (at.Version ?? at.Range?.Min) is { } now ? VersionOrder.Instance.Compare(version, now) : null;

    /// <summary>The version a change to a bare <paramref name="version"/> writes, so a change never turns what the project
    /// gives into a minimum unasked: pinned alone ("[9.0.0]") where it pins every framework's reference; where every one
    /// gives the same range taking the version, a floating version as it is (its restore takes the version) and an interval
    /// from the version on ("[1.0.0, 2.0.0)" moved to 1.1.0 is "[1.1.0, 2.0.0)"); else as given.</summary>
    public static string Written(string version, IReadOnlyList<InstalledVersion> at)
    {
        if (at.Count == 0 || VersionRange.Parse(version) is not { Bare: true, Min: { } bare })
        {
            return version;
        }
        if (at.All(v => v.IsPinned))
        {
            return $"[{bare}]";
        }
        if (at.Select(v => v.Given).Distinct(StringComparer.Ordinal).Count() == 1 && at[0] is { Given: { } given, Range: { Bare: false } range } && range.Takes(bare))
        {
            return range.Floating ? given : $"[{bare}, {range.Max}{(range.Max is not null && range.MaxIncluded ? ']' : ')')}";
        }
        return version;
    }
}
