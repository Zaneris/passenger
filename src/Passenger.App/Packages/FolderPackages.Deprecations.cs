using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App.Packages;

public sealed partial class FolderPackages
{
    /// <summary>Each package version's deprecation read (by "id/version"), a package's versions asked together sharing one
    /// read, each read once until the sources are read again.</summary>
    private Dictionary<string, Task<FeedDeprecations>> deprecations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many packages' registrations are read at once, so a solution's every package is not asked in one go.</summary>
    private readonly SemaphoreSlim deprecationTurns = new(4, 4);

    /// <summary>Reads the deprecations of the package versions not read yet, a package's versions asked together, from the
    /// enabled feeds the package source mapping allows for it (or the feed given in their place): ended once each is known,
    /// or its feeds did not answer. Nothing is read before the sources are.</summary>
    public Task DeprecationsAsync(IEnumerable<(string Id, string Version)> wanted)
    {
        if (Given is null && !IsCurrent)
        {
            return Task.CompletedTask;
        }
        IPackageFeed feed = Given ?? Chosen(FeedSet.All);
        foreach (IGrouping<string, string> package in wanted.Where(w => !deprecations.ContainsKey(Key(w.Id, w.Version)))
            .GroupBy(w => w.Id, w => w.Version, StringComparer.OrdinalIgnoreCase))
        {
            string[] versions = [.. package.Distinct(StringComparer.OrdinalIgnoreCase)];
            Task<FeedDeprecations> read = BackgroundWork.Run(() => ReadDeprecationsAsync(feed, package.Key, versions));
            foreach (string version in versions)
            {
                deprecations[Key(package.Key, version)] = read;
            }
        }
        return Task.WhenAll(wanted.Select(w => deprecations[Key(w.Id, w.Version)]).Distinct());
    }

    /// <summary>A package version's deprecation as read; null where it is not deprecated, or not read yet.</summary>
    public Deprecation? DeprecationOf(string id, string version) =>
        deprecations.TryGetValue(Key(id, version), out Task<FeedDeprecations>? read) && read.IsCompletedSuccessfully
            && read.Result.Deprecated.TryGetValue(version, out Deprecation? deprecation) ? deprecation : null;

    /// <summary>The feeds that did not say whether any of these versions is deprecated, each once.</summary>
    public IReadOnlyList<string> DeprecationsUnknown(IEnumerable<(string Id, string Version)> versions) =>
        [.. versions.Select(v => deprecations.GetValueOrDefault(Key(v.Id, v.Version))).OfType<Task<FeedDeprecations>>().Distinct()
            .SelectMany(read => read.IsCompletedSuccessfully ? read.Result.Failed.Select(f => f.Feed) : []).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string Key(string id, string version) => $"{id}/{version}";

    private async Task<FeedDeprecations> ReadDeprecationsAsync(IPackageFeed feed, string id, IReadOnlyCollection<string> versions)
    {
        await deprecationTurns.WaitAsync();
        try
        {
            return feed is FeedSet set
                ? await set.DeprecationsEachAsync(id, versions, CancellationToken.None)
                : new FeedDeprecations(await feed.DeprecationsAsync(id, versions, CancellationToken.None), []);
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            return FeedDeprecations.None with { Failed = [(feed.Name, failed.Message)] };
        }
        finally
        {
            deprecationTurns.Release();
        }
    }
}
