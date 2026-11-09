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

    public string? UpdateAllWhyNot => CanUpdateAll ? null : model.IsBusy || updatingAll ? "A package change is under way"
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
            VulnerabilityData known = await folder.VulnerabilitiesAsync();
            cancel.Token.ThrowIfCancellationRequested();
            updateRows = Marked(found, known);
            FilterUpdates(chosen);
            string result = problems.Count == 0 && found.Count == 0 ? "No updates found" : $"{Plural.Of(found.Count, "update")} available";
            if (problems.Count > 0)
            {
                result += $"; check incomplete: {string.Join("; ", problems.Distinct())}";
            }
            Tell(result);
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
    }

    public async Task UpdateAllAsync()
    {
        if (!CanUpdateAll)
        {
            Hold(UpdateAllWhyNot);
            return;
        }
        FolderPackages folder = model;
        PackageUpdate[] targets = [.. updateRows.Select(r => new PackageUpdate(r.Id, r.UpdateVersion!, [.. r.Installed.Keys]))];
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
