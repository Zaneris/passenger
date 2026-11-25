using Avalonia.Controls;
using Passenger.App.Packages;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App.ToolWindows;

public sealed partial class NuGetView
{
    private readonly Button updateAll;
    private bool showsUpdates;
    private bool checkingUpdates;
    private bool updatingAll;
    private IReadOnlyList<PackageRow> updateRows = [];

    public bool ShowsUpdates => showsUpdates;

    public bool CanUpdateAll => showsUpdates && !checkingUpdates && !updatingAll && !model.IsBusy && ToUpdate.Count > 0;

    public string? UpdateAllWhyNot => CanUpdateAll ? null : model.IsBusy || updatingAll ? Refusals.PackageChangeUnderWay
        : checkingUpdates ? "Checking for updates" : !showsUpdates ? "Open the Updates tab first"
        : updateRows.Any(r => r.MovesTo is not null) ? "The filters leave no update" : "No updates available";

    /// <summary>The rows whose updates and fixes Update All makes: those the Sort and Filter leave, the search's words
    /// hiding none.</summary>
    private IReadOnlyList<PackageRow> ToUpdate => [.. Filtered(updateRows).Where(r => r.MovesTo is not null)];

    /// <summary>Whether Update All moves a project's references: every project's, else the one Used By names alone.</summary>
    private bool UpdatesIn(string project) => usedBy is null || PathNames.Same(usedBy, project);

    public void ShowUpdates()
    {
        showsUpdates = true;
        picked = null;
        updateAll.IsVisible = true;
        Show(packages);
        Hold(null);
        IdeErrors.Observe(RefreshAsync());
    }

    private Task SearchAsync()
    {
        if (!showsUpdates)
        {
            return RefreshAsync();
        }
        FilterUpdates();
        return Task.CompletedTask;
    }

    private void FilterUpdates(string? chosen = null)
    {
        string query = Query.Trim();
        List([.. updateRows.Where(r => r.Id.Contains(query, StringComparison.OrdinalIgnoreCase))], chosen ?? Selected?.Id);
    }

    private async Task CheckUpdatesAsync(CancellationTokenSource cancel, string? chosen)
    {
        FolderPackages folder = model;
        Task deprecating = Task.CompletedTask;
        checkingUpdates = true;
        updateRows = [];
        List([], null);
        EnableActions();
        Tell("Checking for updates…");
        try
        {
            Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>> installed = await folder.InstalledAsync();
            cancel.Token.ThrowIfCancellationRequested();
            IPackageFeed feed = Feed;
            bool preview = prerelease.IsChecked == true;
            List<PackageRow> found = [];
            List<string> problems = [];
            int checkedCount = 0;
            foreach (KeyValuePair<string, Dictionary<string, IReadOnlyList<InstalledVersion>>>[] batch in installed.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Chunk(4))
            {
                (PackageRow? Row, IReadOnlyList<string> Problems)[] answers = await Task.WhenAll(batch.Select(async package =>
                {
                    try
                    {
                        FeedVersions answer = feed is FeedSet set
                            ? await set.VersionsEachAsync(package.Key, preview, cancel.Token)
                            : new FeedVersions(await feed.VersionsAsync(package.Key, preview, cancel.Token), []);
                        List<string> failed = [.. answer.Failed.Select(f => $"{package.Key}: {f.Feed}: {f.Problem}")];
                        InstalledVersion[] at = [.. package.Value.Values.SelectMany(v => v)];
                        if (at.Any(v => v.Version is null))
                        {
                            failed.Add($"{package.Key}: installed version unknown");
                        }
                        string? latest = answer.Versions.FirstOrDefault();
                        if (latest is null)
                        {
                            failed.Add($"{package.Key}: no versions available from {feed.Name}");
                        }
                        Dictionary<string, string> moves = Moves(answer.Versions, package.Value);
                        string? held = latest is null ? null : HeldBack(latest, package.Value);
                        PackageRow? row = moves.Count > 0 || held is not null
                            ? new PackageRow(package.Key, string.Join(", ", at.Select(v => v.Shown ?? "unknown").Distinct(StringComparer.OrdinalIgnoreCase)),
                                string.Empty, string.Empty, 0, package.Value)
                            {
                                UpdateVersion = moves.Values.Max(VersionOrder.Instance),
                                Moves = moves,
                                HeldBack = held,
                                AvailableVersions = answer.Versions,
                            }
                            : null;
                        return (row, (IReadOnlyList<string>)failed);
                    }
                    catch (Exception failed) when (!cancel.IsCancellationRequested && FeedSet.IsFeedFailure(failed))
                    {
                        return ((PackageRow?)null, (IReadOnlyList<string>)[$"{package.Key}: {failed.Message}"]);
                    }
                }));
                cancel.Token.ThrowIfCancellationRequested();
                found.AddRange(answers.Select(a => a.Row).OfType<PackageRow>());
                problems.AddRange(answers.SelectMany(a => a.Problems));
                checkedCount += batch.Length;
                Tell($"Checking for updates… {Numbers.Said(checkedCount)}/{Numbers.Said(installed.Count)}");
            }
            Dictionary<string, Dictionary<string, IReadOnlyList<TransitiveUse>>> transitive = await folder.TransitiveAsync();
            VulnerabilityData known = await folder.VulnerabilitiesAsync();
            cancel.Token.ThrowIfCancellationRequested();
            int updates = found.Count(r => r.UpdateVersion is not null);
            found = [.. found.Select(r => Brought(r, transitive))];
            int fixes = await FixesAsync(found, installed, transitive, known, feed, preview, problems, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            updateRows = Marked([.. found.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)], known);
            FilterUpdates(chosen);
            string result = problems.Count == 0 && updates == 0 && fixes == 0 ? "No updates found"
                : string.Join(", ", new[] { updates > 0 || fixes == 0 ? $"{Plural.Of(updates, "update")} available" : null,
                    fixes > 0 ? $"{Plural.Of(fixes, "vulnerable transitive version")} to fix" : null }.OfType<string>());
            if (problems.Count > 0)
            {
                result += $"; check incomplete: {string.Join("; ", problems.Distinct())}";
            }
            Tell(result);
            deprecating = DeprecatedAsync(updateRows, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The next view owns its results.
        }
        finally
        {
            if (asking == cancel)
            {
                checkingUpdates = false;
                EnableActions();
            }
        }
        await deprecating;
    }

    /// <summary>By project, the newest of a package's versions (newest first) that every framework's reference of it takes,
    /// where that raises one of them: what the Updates tab moves each to.</summary>
    private static Dictionary<string, string> Moves(IReadOnlyList<string> newestFirst, IReadOnlyDictionary<string, IReadOnlyList<InstalledVersion>> byProject)
    {
        Dictionary<string, string> moves = new(PathNames.Comparer);
        foreach ((string path, IReadOnlyList<InstalledVersion> at) in byProject)
        {
            if (InstalledVersion.Newest(newestFirst, at) is { } taken && at.Any(v => v.Version is { } now && VersionOrder.Instance.Compare(taken, now) > 0))
            {
                moves[path] = taken;
            }
        }
        return moves;
    }

    /// <summary>Why the newest version is offered to no project whose range or floating version keeps it out though it is
    /// newer than what the project has ("15.0.0 is outside [14.0.0] in Api"); null where none does.</summary>
    private string? HeldBack(string newest, IReadOnlyDictionary<string, IReadOnlyList<InstalledVersion>> byProject)
    {
        string[] held = [.. byProject
            .SelectMany(p => p.Value.Where(v => v.Version is { } now && VersionOrder.Instance.Compare(newest, now) > 0 && !v.Takes(newest))
                .Select(v => $"{v.Given} in {model.Projects.FirstOrDefault(m => PathNames.Same(m.Path, p.Key))?.Name ?? Path.GetFileNameWithoutExtension(p.Key)}"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return held.Length == 0 ? null : $"{newest} is outside {Plural.Listed(held)}";
    }

    /// <summary>The Updates tab's fixes of vulnerable versions projects get only through others, beside the updates: each
    /// package with such a use has its fix per project (<see cref="TransitiveFix.Of"/>) among the feeds' versions, on its
    /// row of updates where it has one, else on a row of its own; how many projects' uses are fixed.</summary>
    private async Task<int> FixesAsync(List<PackageRow> found, Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>> installed,
        Dictionary<string, Dictionary<string, IReadOnlyList<TransitiveUse>>> transitive, VulnerabilityData known, IPackageFeed feed, bool preview, List<string> problems,
        CancellationToken cancel)
    {
        int fixes = 0;
        foreach (string id in transitive.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, IReadOnlyList<InstalledVersion>> at = installed.GetValueOrDefault(id) ?? [];
            PackageRow row = found.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? Brought(new PackageRow(id, string.Empty, string.Empty, string.Empty, 0, at), transitive);
            if (!row.Transitive.Values.SelectMany(u => u).Any(u => known.Of(id, u.Version).Count > 0))
            {
                continue;
            }
            IReadOnlyList<string> offered = row.AvailableVersions;
            try
            {
                if (offered.Count == 0)
                {
                    FeedVersions answer = feed is FeedSet set ? await set.VersionsEachAsync(id, preview, cancel) : new FeedVersions(await feed.VersionsAsync(id, preview, cancel), []);
                    problems.AddRange(answer.Failed.Select(f => $"{id}: {f.Feed}: {f.Problem}"));
                    offered = answer.Versions;
                }
            }
            catch (Exception failed) when (!cancel.IsCancellationRequested && FeedSet.IsFeedFailure(failed))
            {
                problems.Add($"{id}: {failed.Message}");
                continue;
            }
            Dictionary<string, TransitiveFix> each = new(PathNames.Comparer);
            foreach ((string path, IReadOnlyList<TransitiveUse> uses) in row.Transitive)
            {
                if (model.Projects.FirstOrDefault(p => PathNames.Same(p.Path, path)) is { } project
                    && TransitiveFix.Of(id, uses, at.GetValueOrDefault(path) ?? [], project.TargetFrameworks, known, offered) is { } fix)
                {
                    each[path] = fix;
                }
            }
            if (each.Count == 0)
            {
                continue;
            }
            fixes += each.Count;
            int i = found.IndexOf(row);
            PackageRow fixing = row with
            {
                Version = row.Version.Length > 0 ? row.Version
                    : string.Join(", ", each.Values.SelectMany(f => f.Fixes).Select(u => u.Version).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, VersionOrder.Instance)),
                Fixes = each,
                AvailableVersions = offered,
            };
            if (i >= 0)
            {
                found[i] = fixing;
            }
            else
            {
                found.Add(fixing);
            }
        }
        return fixes;
    }

    public async Task UpdateAllAsync()
    {
        if (!CanUpdateAll)
        {
            Hold(UpdateAllWhyNot);
            return;
        }
        FolderPackages folder = model;
        IReadOnlyList<PackageRow> shown = ToUpdate;
        PackageUpdate[] targets = [.. shown.SelectMany(r => r.Moves.Where(m => UpdatesIn(m.Key)).GroupBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PackageUpdate(r.Id, g.Key, [.. g.Select(m => m.Key)]))),
            .. shown.SelectMany(r => r.Fixes.Where(f => UpdatesIn(f.Key)).Select(f => new PackageUpdate(r.Id, f.Value.Version, [f.Key]) { Frameworks = f.Value.Frameworks }))];
        updatingAll = true;
        EnableActions();
        try
        {
            PackageUpdateResult result = await folder.UpdateAllAsync(targets, BeforeInstall);
            if (model == folder)
            {
                Hold(result.Message);
            }
        }
        finally
        {
            updatingAll = false;
            await RefreshAsync();
            EnableActions();
        }
    }
}
