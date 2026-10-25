using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>Packages to move together in one project, as one change: each id and the version it goes to.</summary>
public sealed record EfMove(ProjectModel Project, IReadOnlyList<(string Id, string Version)> Packages);

/// <summary>A fix of EF Core's packages and a provider that does not support them, worked out from the feed's own metadata
/// before anything changes: its title as its button says it, and the moves it makes.</summary>
public sealed record EfAlignment(string Title, IReadOnlyList<EfMove> Moves);

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
    private static string Lower(string range) => range.Trim().TrimStart('[', '(').Split(',')[0].Trim().TrimEnd(']', ')');
}
