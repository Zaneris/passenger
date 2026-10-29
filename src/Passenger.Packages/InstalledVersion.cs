namespace Passenger.Packages;

/// <summary>What choosing a version does to a project's references of a package: moves one up (an update), only down (a
/// downgrade), or nothing, each of its frameworks having that version already.</summary>
public enum PackageMove
{
    None,
    Update,
    Downgrade,
}

/// <summary>A package as a project references it for one of its target frameworks (null for a project of one): the
/// version the project gives, null where central package management gives none.</summary>
public sealed record InstalledVersion(string? Framework, string? Given)
{
    /// <summary>The version in force, which is shown, compared and audited; null where the project gives none.</summary>
    public string? Version => Given;

    /// <summary>What a change of every framework's reference to <paramref name="version"/> is: an update where it raises
    /// any one's version in force, a downgrade where it lowers one and raises none, else none.</summary>
    public static PackageMove Move(string version, IReadOnlyList<InstalledVersion> at)
    {
        int[] orders = [.. at.Where(v => v.Version is not null).Select(v => VersionOrder.Instance.Compare(version, v.Version))];
        return orders.Any(o => o > 0) ? PackageMove.Update : orders.Any(o => o < 0) ? PackageMove.Downgrade : PackageMove.None;
    }
}
