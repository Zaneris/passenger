using Passenger.App.Editors;
using Passenger.App.MSBuild;
using Passenger.App.Packages;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>What an Install or Update of one of EF Core's packages came to before the change asked: whether that change
/// goes on as asked (<paramref name="AsAsked"/>), else the change made in its place (<paramref name="Outcome"/>, null where
/// it was declined or the folder changed meanwhile); the packages that moved with the one asked, each with its version;
/// and what was said of it, the status's words or the provider it leaves behind.</summary>
public sealed record EfUpdate(bool AsAsked, PackageOutcome? Outcome, IReadOnlyList<string> With, Words? Said)
{
    /// <summary>The change asked goes on, nothing weighed or moved with it.</summary>
    public static readonly EfUpdate Asked = new(true, null, [], null);
}

/// <summary>The NuGet window's Install or Update of one of EF Core's packages, and each of Update All's, weighed before it
/// goes, as EF Core's packages move together and another maker's provider moves only when asked.</summary>
public sealed partial class MigrationsController
{
    /// <summary>Asked before the NuGet window's own Install or Update and each of Update All's (<see cref="UpdateAsync"/>):
    /// whether that change goes on as asked.</summary>
    public async Task<bool> WeighUpdateAsync(ProjectModel project, string id, string version, CommandHistory.Recording? within = null, CancellationToken cancel = default) =>
        (await UpdateAsync(project, id, version, within, cancel)).AsAsked;

    /// <summary>An Install or Update of one of EF Core's packages: the window's update of one the project references moves the
    /// project's others to its version (under central package management the folder's too) in one change, as Update All,
    /// recording <paramref name="within"/>, moves them itself. One leaving another maker's provider behind asks first,
    /// offering the set with the provider's version that supports it, else EF Core's newest the provider supports;
    /// Update Anyway leaves the provider as it is. An agent is never asked: its call goes on as Update Anyway.</summary>
    public async Task<EfUpdate> UpdateAsync(ProjectModel project, string id, string version, CommandHistory.Recording? within, CancellationToken cancel)
    {
        UiThread.Check();
        ProjectSession owning = parts.Session();
        cancel.ThrowIfCancellationRequested();
        if (!EfProviders.InSet(id))
        {
            return EfUpdate.Asked;
        }
        EfRestore? restore = EfRestore.Of(project.Path);
        EfProvider? left = restore is { Runtime: not null }
            ? restore.Providers.FirstOrDefault(p => !EfProviders.InSet(p.Id) && !EfRestore.InRange(p.Range, version))
            : null;
        // Update All moves the rest of the set itself, and an install adds the one asked alone.
        bool moves = within is null && FolderPackages.References(project).Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (left is null && !moves)
        {
            return EfUpdate.Asked;
        }
        IPackageFeed? feed = null;
        EfOffer offer = EfOffer.None;
        try
        {
            feed = await parts.Feed();
            if (left is not null)
            {
                offer = await EfProviders.OfferAsync(feed, left, version, restore!.Runtime!, restore.Framework, cancel);
            }
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            // The feeds unread: no provider's version is offered, and the set moves whole for its restore to say what they lack.
        }
        cancel.ThrowIfCancellationRequested();
        if (parts.Session() != owning)
        {
            return new EfUpdate(false, null, [], Refusals.SolutionClosed);
        }
        Words named = MigrationsWords.PackageMove(InstalledVersion.Move(version, await FolderPackages.VersionsInAsync(project, id)) == PackageMove.Downgrade ? "downgrade" : "update",
            id, version, project.Name);
        if (left is null)
        {
            EfSet set = await SetAsync(owning, project, id, version, feed, cancel);
            return set.Own.Count + set.Alongside.Count == 0 ? EfUpdate.Asked
                : await MoveAsync(project, named, [(id, version), .. set.Own.Select(o => (o, version))], set, version, null, null);
        }
        Words anyway = MigrationsWords.UpdateAnyway;
        Words together = MigrationsWords.UpdateSet(version);
        Words? back = offer.Core is { } core ? MigrationsWords.UpdateSet(core) : null;
        Words offering = offer.Up is { } up ? MigrationsWords.OfferUp(left.Id, up, together, version)
            : offer is { Core: { } newest, Fits: { } fits } ? (fits == left.Version ? MigrationsWords.OfferBack(left.Id, version, back!, newest)
                : MigrationsWords.OfferBackWith(left.Id, version, back!, newest, fits))
            : MigrationsWords.OfferNone(left.Id, version);
        Words said = MigrationsWords.Sentences(MigrationsWords.LeavesBehind(left.Id, left.Version, EfGaps.Supports(left.Range), id, version), offering);
        if (moves)
        {
            said = MigrationsWords.Sentences(said, MigrationsWords.AnywayNote(anyway, version, left.Id));
        }
        Words? offered = offer.Up is null ? back : together;
        Words? answer = !parts.AsksNoOne() && Mcp.AgentCall.Current is null
            ? await parts.Choose(MigrationsWords.UpdateTitle(id), said, offered is null ? [anyway] : [offered, anyway], offered)
            : anyway;
        cancel.ThrowIfCancellationRequested();
        if (parts.Session() != owning)
        {
            return new EfUpdate(false, null, [], Refusals.SolutionClosed);
        }
        if (answer is null)
        {
            return new EfUpdate(false, null, [], null);
        }
        if (answer.Equals(anyway))
        {
            Words behind = Behind(left, version, offer);
            EfSet set = moves ? await SetAsync(owning, project, id, version, feed, cancel) : EfSet.None;
            return set.Own.Count + set.Alongside.Count == 0 ? new EfUpdate(true, null, [], behind)
                : await MoveAsync(project, named, [(id, version), .. set.Own.Select(o => (o, version))], set, version, behind, null);
        }
        string to = answer.Equals(together) ? version : offer.Core!;
        EfSet moved = await SetAsync(owning, project, id, to, feed, cancel);
        string provider = answer.Equals(together) ? offer.Up! : offer.Fits!;
        List<(string Id, string Version)> packages = [(id, to), .. moved.Own.Select(o => (o, to))];
        if (provider != left.Version)
        {
            packages.Add((left.Id, provider));
        }
        return await MoveAsync(project, answer, packages, moved, to, null, within);
    }

    /// <summary>EF Core's packages moving with one of them: the project's own, the central versions alongside them, and those
    /// the feeds lack at the version, left as they are.</summary>
    private sealed record EfSet(IReadOnlyList<string> Own, IReadOnlyList<string> Alongside, IReadOnlyList<string> Lacking)
    {
        public static readonly EfSet None = new([], [], []);
    }

    /// <summary>EF Core's packages that move with one of them to a version: the project's others whose versions it moves
    /// (never one floating), and under central package management those the folder's other projects reference; each only
    /// where the feeds have that version (every one where they cannot say).</summary>
    private static async Task<EfSet> SetAsync(ProjectSession owning, ProjectModel project, string id, string version, IPackageFeed? feed, CancellationToken cancel)
    {
        List<string> own = [];
        foreach (string other in FolderPackages.References(project).Select(r => r.Id).Where(EfProviders.InSet).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            InstalledVersion[] at = await FolderPackages.VersionsInAsync(project, other);
            if (!other.Equals(id, StringComparison.OrdinalIgnoreCase) && at.Length > 0 && at.All(v => v.Version is not null && v.Range is not { Floating: true })
                && InstalledVersion.Move(version, at) != PackageMove.None)
            {
                own.Add(other);
            }
        }
        List<string> alongside = [];
        if (CentralPackages.PropsOf(project) is not null)
        {
            foreach ((string other, Dictionary<string, IReadOnlyList<InstalledVersion>> byProject) in await owning.Packages.InstalledAsync())
            {
                if (EfProviders.InSet(other) && !other.Equals(id, StringComparison.OrdinalIgnoreCase) && !byProject.ContainsKey(project.Path)
                    && byProject.Values.Any(at => at.All(v => v.Version is not null) && InstalledVersion.Move(version, at) != PackageMove.None))
                {
                    alongside.Add(other);
                }
            }
        }
        List<string> lacking = [];
        try
        {
            foreach (string other in own.Concat(alongside))
            {
                if (feed is not null && !(await feed.VersionsAsync(other, VersionOrder.IsPrerelease(version), cancel)).Any(v => VersionOrder.Instance.Compare(v, version) == 0))
                {
                    lacking.Add(other);
                }
            }
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            // The feeds unread: the set moves whole, its restore saying what they lack.
            lacking.Clear();
        }
        return new EfSet([.. own.Except(lacking)], [.. alongside.Except(lacking)], lacking);
    }

    /// <summary>Packages moved in one project as one change, the first the one asked, as one step of global undo (or of
    /// <paramref name="within"/>): the status names what moved with it, under central package management as Passenger's
    /// writes moved it (a property's sharers among them), then what was left as it was.</summary>
    private async Task<EfUpdate> MoveAsync(ProjectModel project, Words name, IReadOnlyList<(string Id, string Version)> packages, EfSet set, string version,
        Words? behind, CommandHistory.Recording? within)
    {
        using CommandHistory.Recording? recorded = within is null ? parts.Editors.Commands.RecordWrites(name.English) : null;
        PackageOutcome outcome = await parts.InstallSet(project, packages, within ?? recorded!, [.. set.Alongside.Select(a => (a, version))]);
        IEnumerable<string> central = outcome.Moved?.Where(m => !packages.Any(p => p.Id.Equals(m, StringComparison.OrdinalIgnoreCase))) ?? set.Alongside;
        IReadOnlyList<string> with = [.. packages.Skip(1).Select(p => $"{p.Id} {p.Version}"), .. central.Order(StringComparer.OrdinalIgnoreCase).Select(c => $"{c} {version}")];
        Words said = !outcome.Done ? (outcome.Refused is { } refused ? Words.Verbatim(refused) : MigrationsWords.MoveFailed(name))
            : with.Count == 0 ? MigrationsWords.MoveDone(name) : MigrationsWords.MovedWith(name, Plural.Listed(Words.Names(with)));
        // The NuGet window's own words of what it kept as it was, then what the move left behind.
        foreach (Words more in new[] { outcome.Done ? null : outcome.Left is { } kept ? Words.Verbatim(kept) : null, outcome.Done ? behind : null,
            outcome.Done ? Lacking(set.Lacking, version) : null }.OfType<Words>())
        {
            said = Clauses.Then(said, more);
        }
        parts.Say(said);
        return new EfUpdate(false, outcome, outcome.Done ? with : [], said);
    }

    /// <summary>A provider an update left behind, with what the feeds offer it.</summary>
    private static Words Behind(EfProvider left, string version, EfOffer offer) => offer switch
    {
        { Up: { } up } => MigrationsWords.BehindUp(left.Id, left.Version, EfGaps.Supports(left.Range), up, version),
        { Core: { } core, Fits: { } fits } when fits == left.Version => MigrationsWords.BehindOlder(left.Id, left.Version, EfGaps.Supports(left.Range), version, core),
        { Core: { } core, Fits: { } fits } => MigrationsWords.BehindOlderOther(left.Id, left.Version, EfGaps.Supports(left.Range), version, fits, core),
        _ => MigrationsWords.BehindNone(left.Id, left.Version, EfGaps.Supports(left.Range), version),
    };

    /// <summary>Those of EF Core's packages the feeds have no such version of, left as they were; null for none.</summary>
    private static Words? Lacking(IReadOnlyList<string> lacking, string version) => lacking.Count == 0 ? null
        : MigrationsWords.Lacking(lacking.Count, Plural.Listed(Words.Names(lacking)), version);
}
