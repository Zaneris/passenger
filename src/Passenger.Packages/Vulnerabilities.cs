using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Core;

namespace Passenger.Packages;

/// <summary>How grave an advisory says a vulnerability is: NuGet's four levels, 0 to 3 in a feed's data.</summary>
public enum AdvisorySeverity
{
    Low,
    Moderate,
    High,
    Critical,
}

/// <summary>A known vulnerability of a package, as a feed's VulnerabilityInfo resource gives it: how grave it is, its
/// advisory's address as the feed wrote it, and the versions it affects as a NuGet version range ("(, 1.4.1)",
/// "[2.0.0, 2.3.1)").</summary>
public sealed record Advisory(AdvisorySeverity Severity, string Address, string Versions)
{
    /// <summary>The advisory's page to open, only where its address is http or https: a feed may name any scheme.</summary>
    public Uri? Link { get; } = WebAddress.Of(Address);

    /// <summary>Whether a version is in the range, in NuGet's notation: a bare version is the least version, a bracket
    /// includes its end and a parenthesis leaves it out, an end left empty is unbounded, and "[1.0]" is that version alone.
    /// A version that is not plain (a floating "1.*") is in none.</summary>
    public bool Affects(string version)
    {
        string range = Versions.Trim();
        if (range.Length == 0 || version.Contains('*', StringComparison.Ordinal) || version.StartsWith('[') || version.StartsWith('('))
        {
            return false;
        }
        VersionOrder order = VersionOrder.Instance;
        if (range[0] is not ('[' or '('))
        {
            return order.Compare(version, range) >= 0;
        }
        bool fromIncluded = range[0] == '[';
        bool toIncluded = range[^1] == ']';
        string[] ends = range[1..^1].Split(',');
        if (ends.Length == 1)
        {
            return order.Compare(version, ends[0].Trim()) == 0;
        }
        string from = ends[0].Trim();
        string to = ends[1].Trim();
        int above = from.Length == 0 ? 1 : order.Compare(version, from);
        int below = to.Length == 0 ? -1 : order.Compare(version, to);
        return (above > 0 || (above == 0 && fromIncluded)) && (below < 0 || (below == 0 && toIncluded));
    }
}

/// <summary>Every advisory the audit's feeds know (the folder's auditSources, else its enabled package sources), by
/// package, with the feeds that did not answer.</summary>
public sealed class VulnerabilityData
{
    private readonly Dictionary<string, IReadOnlyList<Advisory>> byPackage;

    public VulnerabilityData(Dictionary<string, IReadOnlyList<Advisory>> byPackage, IReadOnlyList<(string Feed, string Problem)> failed)
    {
        this.byPackage = new Dictionary<string, IReadOnlyList<Advisory>>(byPackage, StringComparer.OrdinalIgnoreCase);
        Failed = failed;
    }

    public static VulnerabilityData None { get; } = new([], []);

    /// <summary>The feeds asked that did not answer, and why.</summary>
    public IReadOnlyList<(string Feed, string Problem)> Failed { get; }

    /// <summary>The advisories that affect a version of a package, the gravest first.</summary>
    public IReadOnlyList<Advisory> Of(string id, string version) =>
        byPackage.TryGetValue(id, out IReadOnlyList<Advisory>? known) ? [.. known.Where(a => a.Affects(version)).OrderByDescending(a => a.Severity)] : [];

    /// <summary>The version that fixes what affects an installed one: the least version above it that no advisory
    /// affects, among those offered; null when every one is affected.</summary>
    public string? FixedVersion(string id, string installed, IEnumerable<string> offered) =>
        offered.Where(v => VersionOrder.Instance.Compare(v, installed) > 0 && Of(id, v).Count == 0)
            .OrderBy(v => v, VersionOrder.Instance)
            .FirstOrDefault();

    /// <summary>Reads the advisories of the feeds the audit uses, each asked at once; only a v3 feed with a
    /// VulnerabilityInfo resource has any. A feed that does not answer is named in <see cref="Failed"/>, never
    /// thrown.</summary>
    public static async Task<VulnerabilityData> ReadAsync(IReadOnlyList<FeedSource> sources, CancellationToken cancel)
    {
        (string Feed, IReadOnlyDictionary<string, IReadOnlyList<Advisory>>? Found, string? Problem)[] answers = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                return PackageFeeds.For(source) is V3Feed feed
                    ? (source.Name, await feed.VulnerabilitiesAsync(cancel), (string?)null)
                    : (source.Name, new Dictionary<string, IReadOnlyList<Advisory>>(), null);
            }
            catch (Exception failed) when (!cancel.IsCancellationRequested && FeedSet.IsFeedFailure(failed))
            {
                return (source.Name, (IReadOnlyDictionary<string, IReadOnlyList<Advisory>>?)null, failed.Message);
            }
        }));
        Dictionary<string, IReadOnlyList<Advisory>> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach ((_, IReadOnlyDictionary<string, IReadOnlyList<Advisory>>? found, _) in answers)
        {
            foreach ((string id, IReadOnlyList<Advisory> advisories) in found ?? new Dictionary<string, IReadOnlyList<Advisory>>())
            {
                merged[id] = merged.TryGetValue(id, out IReadOnlyList<Advisory>? known) ? [.. known.Concat(advisories).Distinct()] : advisories;
            }
        }
        return new VulnerabilityData(merged, [.. answers.Where(a => a.Problem is not null).Select(a => (a.Feed, a.Problem!))]);
    }

    /// <summary>A page of a VulnerabilityInfo resource: lower-case package ids, each with its advisories.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<Advisory>> Page(Dictionary<string, List<Entry>>? page)
    {
        Dictionary<string, IReadOnlyList<Advisory>> read = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, List<Entry> entries) in page ?? [])
        {
            List<Advisory> advisories = [];
            foreach (Entry entry in entries)
            {
                if (Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? url) && entry.Versions is { Length: > 0 } versions)
                {
                    advisories.Add(new Advisory((AdvisorySeverity)Math.Clamp(entry.Severity, 0, 3), url.OriginalString, versions));
                }
            }
            read[id] = advisories;
        }
        return read;
    }

    internal sealed record Entry(int Severity, string? Url, string? Versions);

    /// <summary>A VulnerabilityInfo resource's index: its pages, a base and the updates since.</summary>
    internal sealed record IndexPage([property: JsonPropertyName("@name")] string? Name, [property: JsonPropertyName("@id")] string Id);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}
