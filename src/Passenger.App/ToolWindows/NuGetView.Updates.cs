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

    public bool CanUpdateAll => showsUpdates && !checkingUpdates && !updatingAll && !model.IsBusy && updateRows.Count > 0;

    public string? UpdateAllWhyNot => CanUpdateAll ? null : model.IsBusy || updatingAll ? Refusals.PackageChangeUnderWay
        : checkingUpdates ? "Checking for updates" : !showsUpdates ? "Open the Updates tab first" : "No updates available";

    public void ShowUpdates()
    {
        showsUpdates = true;
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
                        PackageRow? row = latest is not null && at.Any(v => v.Version is { } version && VersionOrder.Instance.Compare(latest, version) > 0)
                            ? new PackageRow(package.Key, string.Join(", ", at.Select(v => v.Shown ?? "unknown").Distinct(StringComparer.OrdinalIgnoreCase)),
                                string.Empty, string.Empty, 0, package.Value) { UpdateVersion = latest, AvailableVersions = answer.Versions }
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
                Tell($"Checking for updates… {checkedCount}/{installed.Count}");
            }
            Dictionary<string, Dictionary<string, IReadOnlyList<TransitiveUse>>> transitive = await folder.TransitiveAsync();
            VulnerabilityData known = await folder.VulnerabilitiesAsync();
            cancel.Token.ThrowIfCancellationRequested();
            int updates = found.Count;
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
        PackageUpdate[] targets = [.. updateRows.Where(r => r.UpdateVersion is not null).Select(r => new PackageUpdate(r.Id, r.UpdateVersion!, [.. r.Installed.Keys])),
            .. updateRows.SelectMany(r => r.Fixes.Select(f => new PackageUpdate(r.Id, f.Value.Version, [f.Key]) { Frameworks = f.Value.Frameworks }))];
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
