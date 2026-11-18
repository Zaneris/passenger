using Passenger.Core;

namespace Passenger.Packages;

/// <summary>What fixes the vulnerable versions of a package a project gets only through others: a reference of its own at
/// the least version above each that no known advisory affects, in the frameworks that get one (none named where it is
/// every framework's, so the reference is the project's whole), and the uses it fixes.</summary>
public sealed record TransitiveFix(string Id, string Version, IReadOnlyList<string> Frameworks, IReadOnlyList<TransitiveUse> Fixes)
{
    /// <summary>The fix of a project's uses of a package (<paramref name="brought"/>, those its references leave,
    /// <see cref="Left"/>), among the versions <paramref name="offered"/>: the newest of the vulnerable uses' fixes, for
    /// every framework with such a use, the project's whole where it references the package in none and every framework of
    /// its own (<paramref name="frameworks"/>) has one; null where no use is vulnerable or none can be fixed.</summary>
    public static TransitiveFix? Of(string id, IReadOnlyList<TransitiveUse> brought, IReadOnlyList<InstalledVersion> referenced, IReadOnlyList<string> frameworks,
        VulnerabilityData known, IReadOnlyList<string> offered)
    {
        (TransitiveUse Use, string Fix)[] fixable = [.. brought.Where(b => known.Of(id, b.Version).Count > 0)
            .Select(b => (Use: b, Fix: known.FixedVersion(id, b.Version, offered)))
            .Where(f => f.Fix is not null).Select(f => (f.Use, f.Fix!))];
        if (fixable.Length == 0)
        {
            return null;
        }
        string version = fixable.Select(f => f.Fix).Max(VersionOrder.Instance)!;
        TransitiveUse[] fixes = [.. fixable.Select(f => f.Use)];
        return new TransitiveFix(id, version, FrameworksOf(fixes, referenced, frameworks), fixes);
    }

    /// <summary>The frameworks a fix of some uses references the package in: each use's, or none (the project's whole) where
    /// a use names none or the project references the package in none and every framework of its own has a use.</summary>
    public static IReadOnlyList<string> FrameworksOf(IReadOnlyList<TransitiveUse> fixing, IReadOnlyList<InstalledVersion> referenced, IReadOnlyList<string> frameworks)
    {
        string[] named = [.. fixing.Select(u => u.Framework).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        bool whole = fixing.Any(u => u.Framework is null)
            || (referenced.Count == 0 && frameworks.Count > 0 && frameworks.All(f => named.Contains(f, StringComparer.OrdinalIgnoreCase)));
        return whole ? [] : named;
    }

    /// <summary>A project's uses of a package through others in the frameworks where none of its references stands (one read
    /// with no framework, of a project of one or not evaluated, stands for every framework's).</summary>
    public static IReadOnlyList<TransitiveUse> Left(IReadOnlyList<TransitiveUse> uses, IReadOnlyList<InstalledVersion> referenced) =>
        [.. uses.Where(u => !referenced.Any(r => r.Framework is null || u.Framework is null || r.Framework.Equals(u.Framework, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>Where it puts the reference, as a change and Undo name it: " for net10.0", or nothing for the project's whole.</summary>
    public string Where => Within(Frameworks);

    /// <summary>Where a reference in frameworks goes, as a change names it: " for net10.0", or nothing for none.</summary>
    public static string Within(IReadOnlyList<string> frameworks) => frameworks.Count == 0 ? string.Empty : $" for {string.Join(", ", frameworks)}";
}
