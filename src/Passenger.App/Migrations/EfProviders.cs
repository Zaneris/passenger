using Passenger.Core;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>Packages to move together in one project, as one change: each id and the version it goes to.</summary>
public sealed record EfMove(ProjectModel Project, IReadOnlyList<(string Id, string Version)> Packages);

/// <summary>A fix of EF Core's packages and a provider that does not support them, worked out from the feed's own metadata
/// before anything changes: its title as its button says it, and the moves it makes.</summary>
public sealed record EfAlignment(string Title, IReadOnlyList<EfMove> Moves);

/// <summary>What the feeds offer a provider an update of EF Core would leave behind: the provider's newest version that
/// supports the EF Core asked (<paramref name="Up"/>); else, where none does, EF Core's newest version between the project's
/// and the one asked that a version of the provider at or above its own supports (<paramref name="Core"/>), with the
/// lowest such version of the provider (<paramref name="Fits"/>, its own where it does); nulls where the feeds have neither.</summary>
public sealed record EfOffer(string? Up, string? Core, string? Fits)
{
    public static readonly EfOffer None = new(null, null, null);
}

/// <summary>Keeps EF Core's packages (Core, Relational, Design, Tools, Microsoft's own providers) and a provider of another
/// maker in step: a provider supports a range of EF Core, its own dependency's in the feed's metadata, and its releases
/// trail EF Core's, so the version that fits is never EF Core's newest but what the provider supports.</summary>
public static class EfProviders
{
    private const string Relational = "Microsoft.EntityFrameworkCore.Relational";

    /// <summary>Whether a package is one of EF Core's own, which move together at one version.</summary>
    public static bool InSet(string id) => id.StartsWith(EfRestore.Core, StringComparison.OrdinalIgnoreCase);

    /// <summary>The range of EF Core a provider's version supports, from the feed's metadata: its dependency on EF Core's
    /// relational layer, else on EF Core; null when the feed cannot say.</summary>
    public static async Task<string?> RangeAsync(IPackageFeed feed, string provider, string version, string? framework, CancellationToken cancel)
    {
        PackageDependencies? dependencies = await feed.DependenciesAsync(provider, version, cancel);
        return dependencies?.RangeOf(Relational, framework) ?? dependencies?.RangeOf(EfRestore.Core, framework);
    }

    /// <summary>What the feeds offer a provider that an update of the project's EF Core (<paramref name="runtime"/>) to
    /// <paramref name="asked"/> leaves behind (<see cref="EfOffer"/>), from each version's range in their metadata.</summary>
    public static async Task<EfOffer> OfferAsync(IPackageFeed feed, EfProvider behind, string asked, string runtime, string? framework, CancellationToken cancel)
    {
        bool prerelease = VersionOrder.IsPrerelease(asked);
        List<(string Version, string Range)> held = [];
        foreach (string version in await feed.VersionsAsync(behind.Id, prerelease, cancel))
        {
            if (await RangeAsync(feed, behind.Id, version, framework, cancel) is not { } range)
            {
                continue;
            }
            if (EfRestore.InRange(range, asked))
            {
                return new EfOffer(version, null, null);
            }
            if (VersionOrder.Instance.Compare(version, behind.Version) >= 0)
            {
                held.Add((version, range));
            }
        }
        if (VersionOrder.Instance.Compare(asked, runtime) <= 0)
        {
            return EfOffer.None;
        }
        if (!held.Any(h => VersionOrder.Instance.Compare(h.Version, behind.Version) == 0))
        {
            held.Add((behind.Version, behind.Range));
        }
        foreach (string core in await feed.VersionsAsync(EfRestore.Core, prerelease, cancel))
        {
            if (VersionOrder.Instance.Compare(core, runtime) > 0 && VersionOrder.Instance.Compare(core, asked) < 0
                && held.Where(h => EfRestore.InRange(h.Range, core)).Select(h => h.Version).Order(VersionOrder.Instance).FirstOrDefault() is { } fits)
            {
                return new EfOffer(null, core, fits);
            }
        }
        return EfOffer.None;
    }

    /// <summary>The fix of a provider behind the projects' EF Core: the provider moved up to the newest version that supports
    /// that EF Core, where one exists; else EF Core's packages in the projects moved to the newest version the provider
    /// supports. Null when the feed has neither.</summary>
    public static async Task<EfAlignment?> AlignAsync(IPackageFeed feed, IReadOnlyList<ProjectModel> projects, EfProvider behind, string runtime, string? framework,
        CancellationToken cancel)
    {
        bool prerelease = VersionOrder.IsPrerelease(runtime);
        IReadOnlyList<string> providers = await feed.VersionsAsync(behind.Id, prerelease, cancel);
        foreach (string version in providers.Where(v => VersionOrder.Instance.Compare(v, behind.Version) > 0))
        {
            if (await RangeAsync(feed, behind.Id, version, framework, cancel) is { } range && EfRestore.InRange(range, runtime))
            {
                ProjectModel owner = projects.FirstOrDefault(p => p.Packages.Any(r => r.Name.Equals(behind.Id, StringComparison.OrdinalIgnoreCase))) ?? projects[0];
                return new EfAlignment($"Update {behind.Id} to {version}", [new EfMove(owner, [(behind.Id, version)])]);
            }
        }
        IReadOnlyList<string> cores = await feed.VersionsAsync(EfRestore.Core, VersionOrder.IsPrerelease(Lower(behind.Range)), cancel);
        if (cores.FirstOrDefault(v => EfRestore.InRange(behind.Range, v)) is not { } supported)
        {
            return null;
        }
        List<EfMove> moves = [.. projects.Select(p => new EfMove(p, [.. p.Packages.Where(r => InSet(r.Name)).Select(r => (r.Name, supported))]))
            .Where(m => m.Packages.Count > 0)];
        return moves.Count == 0 ? null : new EfAlignment($"Move EF Core to {supported}", moves);
    }

    /// <summary>A range's lower end: "9.0.1" of "[9.0.1, 10.0.0)" or of "9.0.1".</summary>
    private static string Lower(string range) => VersionRange.Parse(range)?.Min ?? string.Empty;
}
