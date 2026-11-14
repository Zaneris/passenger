using System.Text.Json;
using Passenger.Core;

namespace Passenger.Packages;

/// <summary>A package version its owner deprecated, as a feed's registration gives it: the reasons as the feed names them
/// (NuGet's Legacy, CriticalBugs and Other), the owner's own words, and the package to use instead with the versions of it
/// they name, a NuGet range ("*" any).</summary>
public sealed record Deprecation(IReadOnlyList<string> Reasons, string Message, string? Alternative, string? AlternativeRange)
{
    /// <summary>Why, in words: each reason once, NuGet's said plainly, another as the feed wrote it.</summary>
    public string Why =>
        string.Join(", ", Reasons.Select(Reason).Distinct(StringComparer.OrdinalIgnoreCase)) is { Length: > 0 } why ? why : "no reason given";

    /// <summary>The versions of the package to use instead in words ("2.0.0 or later", "2.1.0", "[2.0.0, 3.0.0)"); null
    /// for any version, or where the owner names no package.</summary>
    public string? AlternativeVersions => Alternative is null || AlternativeRange is not { Length: > 0 } given || given.Trim() == "*" ? null
        : VersionRange.Parse(given) switch
        {
            { IsExact: true } pinned => pinned.Min,
            { Floating: false, Min: { } least, MinIncluded: true, Max: null } => $"{least} or later",
            _ => given.Trim(),
        };

    /// <summary>The package to use instead with its versions ("Passenger.Modern", "Passenger.Modern 2.0.0 or later");
    /// null where the owner names none.</summary>
    public string? Instead => Alternative is not { } id ? null : AlternativeVersions is { } versions ? $"{id} {versions}" : id;

    private static string Reason(string reason)
    {
        string named = reason.Trim();
        return named.Equals("Legacy", StringComparison.OrdinalIgnoreCase) ? "legacy, no longer maintained"
            : named.Equals("CriticalBugs", StringComparison.OrdinalIgnoreCase) ? "critical bugs"
            : named.Equals("Other", StringComparison.OrdinalIgnoreCase) ? "other"
            : named;
    }

    /// <summary>A registration's deprecation object read; null where there is none, or what is there is no object.</summary>
    internal static Deprecation? Read(JsonElement? deprecation)
    {
        if (deprecation is not { ValueKind: JsonValueKind.Object } given)
        {
            return null;
        }
        JsonElement? alternate = JsonMembers.Object(given, "alternatePackage");
        string? alternative = JsonMembers.Text(alternate, "id") is { } named && named.Trim().Length > 0 ? named.Trim() : null;
        return new Deprecation(JsonMembers.Texts(given, "reasons"), JsonMembers.Text(given, "message")?.Trim() ?? string.Empty,
            alternative, alternative is null ? null : JsonMembers.Text(alternate, "range"));
    }

    /// <summary>The deprecated versions among those asked on a registration page (its items' catalog entries), each keyed
    /// by the version as asked, which a catalog entry may write otherwise (1.0 for 1.0.0, build metadata after a +).</summary>
    internal static IEnumerable<(string Version, Deprecation Deprecation)> OnPage(JsonElement page, IReadOnlyCollection<string> versions)
    {
        foreach (JsonElement leaf in JsonMembers.Items(page, "items"))
        {
            JsonElement? entry = JsonMembers.Object(leaf, "catalogEntry");
            if (JsonMembers.Text(entry, "version") is { } version && Read(JsonMembers.Value(entry, "deprecation")) is { } deprecation)
            {
                foreach (string asked in versions.Where(v => VersionOrder.Instance.Compare(v, version) == 0))
                {
                    yield return (asked, deprecation);
                }
            }
        }
    }

    /// <summary>Whether a page a registration index gives by reference may hold one of the versions asked: one whose
    /// lowest and highest versions it names are around one of them, or that names neither.</summary>
    internal static bool MayHold(JsonElement page, IReadOnlyCollection<string> versions)
    {
        string? lower = JsonMembers.Text(page, "lower");
        string? upper = JsonMembers.Text(page, "upper");
        return versions.Any(v => (lower is null || VersionOrder.Instance.Compare(v, lower) >= 0) && (upper is null || VersionOrder.Instance.Compare(v, upper) <= 0));
    }
}

/// <summary>What feeds said of a package's versions being deprecated: those deprecated, by the version as asked, and the
/// feeds that did not answer, and why.</summary>
public sealed record FeedDeprecations(IReadOnlyDictionary<string, Deprecation> Deprecated, IReadOnlyList<(string Feed, string Problem)> Failed)
{
    public static FeedDeprecations None { get; } = new(new Dictionary<string, Deprecation>(), []);
}
