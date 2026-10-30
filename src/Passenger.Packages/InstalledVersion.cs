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

    /// <summary>The version a change to a bare <paramref name="version"/> writes: pinned alone ("[9.0.0]") where the
    /// project pins every framework's reference, so a change never turns a pin into a minimum unasked; else as given.</summary>
    public static string Written(string version, IReadOnlyList<InstalledVersion> at) =>
        at.Count > 0 && at.All(v => v.IsPinned) && VersionRange.Parse(version) is { Bare: true, Min: { } bare } ? $"[{bare}]" : version;
}
